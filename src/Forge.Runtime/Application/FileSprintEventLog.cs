using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// File-based, event-sourced sprint/node/attempt store. Append-only <c>events.jsonl</c> under
/// <c>.forge/sprints/{id}/</c> is the sole source of truth; every read folds current state from
/// it, so a crash can never desynchronize cached state from history. A companion
/// <c>idempotency.json</c> remembers which command keys already produced a durable append, so a
/// retried command is a safe no-op instead of a duplicate transition.
/// </summary>
/// <remarks>
/// ponytail: no snapshot cache. Sprint event streams are small (dozens of events), so folding on
/// every read avoids a second crash-recovery surface for cheap I/O. Add a cache if sprint.inspect
/// profiling ever shows folding is the bottleneck.
/// </remarks>
public sealed class FileSprintEventLog(IClock clock) : ISprintStore
{
    private const string SprintsDirectoryName = "sprints";
    private const string EventsFileName = "events.jsonl";
    private const string IdempotencyFileName = "idempotency.json";
    private const string DefinitionFileName = "definition.json";
    private static readonly JsonSerializerOptions DefinitionJsonOptions = ConfigurationSchemaCodec.SerializerOptions;

    public static string SprintDirectory(string projectRoot, SprintId id) =>
        Path.Combine(SprintsRoot(projectRoot), id.Value.ToString("N"));

    public async Task<SprintWorkflowState?> LoadAsync(
        string projectRoot,
        SprintId id,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkflowEvent> events = await ReadEventsAsync(
            EventsPath(SprintDirectory(projectRoot, id)),
            cancellationToken).ConfigureAwait(false);
        return events.Count == 0 ? null : WorkflowFold.Apply(id, events);
    }

    public async Task SaveDefinitionAsync(
        string projectRoot,
        SprintDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string directory = SprintDirectory(projectRoot, definition.Id);
        Directory.CreateDirectory(directory);
        PersistedDefinition persisted = new()
        {
            BaseCommit = definition.BaseCommit,
            Workflow = definition.Workflow,
            WorkflowVersion = definition.WorkflowVersion,
            ConfigurationSnapshot = new(definition.ConfigurationSnapshot, StringComparer.Ordinal),
            Dependencies = [.. definition.Dependencies.Select(ToPersisted)],
            Graph = [.. definition.Graph.Select(ToPersisted)],
            ConversationLanguage = definition.ConversationLanguage,
            ArtifactPolicySnapshotHash = definition.ArtifactPolicySnapshotHash,
            FrozenAt = definition.FrozenAt,
        };
        await AtomicConfigurationFile.WriteAsync(
            DefinitionPath(directory),
            JsonSerializer.SerializeToUtf8Bytes(persisted, DefinitionJsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SprintDefinition?> LoadDefinitionAsync(
        string projectRoot,
        SprintId id,
        CancellationToken cancellationToken)
    {
        string path = DefinitionPath(SprintDirectory(projectRoot, id));
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        PersistedDefinition persisted = JsonSerializer.Deserialize<PersistedDefinition>(bytes, DefinitionJsonOptions) ??
            throw new InvalidDataException($"The definition for sprint '{id.Value}' is empty.");
        return new(
            id,
            persisted.BaseCommit,
            persisted.Workflow,
            persisted.WorkflowVersion,
            persisted.ConfigurationSnapshot,
            [.. persisted.Dependencies.Select(FromPersisted)],
            [.. persisted.Graph.Select(FromPersisted)],
            persisted.ConversationLanguage,
            persisted.ArtifactPolicySnapshotHash,
            persisted.FrozenAt);
    }

    public async Task SaveNodeResultAsync(
        string projectRoot,
        NodeResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        WorkflowRecordCodec.ValidateNodeResult(result);
        string directory = ResultsDirectory(SprintDirectory(projectRoot, result.SprintId));
        Directory.CreateDirectory(directory);
        PersistedNodeResult persisted = new()
        {
            NodeId = result.NodeId.Value,
            AttemptId = result.AttemptId.Value.ToString("D"),
            State = WorkflowStateNames.ToSnakeCase(result.State),
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            InputDigest = result.InputDigest,
            Outputs = [.. result.Outputs],
            Diagnostics = [.. result.Diagnostics.Select(ToPersisted)],
        };
        await AtomicConfigurationFile.WriteAsync(
            Path.Combine(directory, $"{result.AttemptId.Value:N}.json"),
            JsonSerializer.SerializeToUtf8Bytes(persisted, DefinitionJsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeResult>> GetNodeResultsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string directory = ResultsDirectory(SprintDirectory(projectRoot, sprintId));
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<NodeResult> results = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            PersistedNodeResult persisted =
                JsonSerializer.Deserialize<PersistedNodeResult>(bytes, DefinitionJsonOptions) ??
                throw new InvalidDataException($"The node result at '{path}' is empty.");
            results.Add(new(
                sprintId,
                new(persisted.NodeId),
                new(Guid.Parse(persisted.AttemptId)),
                WorkflowStateNames.Parse<NodeOutcome>(persisted.State),
                persisted.StartedAt,
                persisted.CompletedAt,
                persisted.InputDigest,
                persisted.Outputs,
                [.. persisted.Diagnostics.Select(FromPersisted)]));
        }

        return results;
    }

    public async Task SaveFindingAsync(string projectRoot, Finding finding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finding);
        WorkflowRecordCodec.ValidateFinding(finding);
        Dictionary<Guid, PersistedFinding> findings = await ReadFindingsAsync(projectRoot, finding.SprintId, cancellationToken)
            .ConfigureAwait(false);
        findings[finding.FindingId] = ToPersisted(finding);
        await AtomicConfigurationFile.WriteAsync(
            FindingsPath(SprintDirectory(projectRoot, finding.SprintId)),
            JsonSerializer.SerializeToUtf8Bytes(findings, DefinitionJsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Finding>> GetFindingsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        [.. (await ReadFindingsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false))
            .Select(entry => FromPersisted(sprintId, entry.Value))];

    public async Task SaveHandoffAsync(string projectRoot, Handoff handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        WorkflowRecordCodec.ValidateHandoff(handoff);
        string directory = HandoffsDirectory(SprintDirectory(projectRoot, handoff.SprintId));
        Directory.CreateDirectory(directory);
        PersistedHandoff persisted = new()
        {
            HandoffId = handoff.HandoffId,
            NodeId = handoff.NodeId.Value,
            BaseSha = handoff.BaseSha,
            Summary = handoff.Summary,
            Decisions = [.. handoff.Decisions],
            Artifacts = [.. handoff.Artifacts.Select(ToPersisted)],
            OpenRisks = [.. handoff.OpenRisks],
            NextNodeIds = handoff.NextNodeIds is null ? null : [.. handoff.NextNodeIds],
        };
        await AtomicConfigurationFile.WriteAsync(
            Path.Combine(directory, $"{SanitizeFileName(handoff.NodeId.Value)}.json"),
            JsonSerializer.SerializeToUtf8Bytes(persisted, DefinitionJsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Handoff>> GetHandoffsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string directory = HandoffsDirectory(SprintDirectory(projectRoot, sprintId));
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<Handoff> handoffs = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            PersistedHandoff persisted =
                JsonSerializer.Deserialize<PersistedHandoff>(bytes, DefinitionJsonOptions) ??
                throw new InvalidDataException($"The handoff at '{path}' is empty.");
            handoffs.Add(FromPersisted(sprintId, persisted));
        }

        return handoffs;
    }

    public Task<IReadOnlyList<SprintId>> ListAsync(string projectRoot, CancellationToken cancellationToken)
    {
        string root = SprintsRoot(projectRoot);
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<SprintId>>([]);
        }

        List<SprintId> ids = [];
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid value))
            {
                ids.Add(new(value));
            }
        }

        return Task.FromResult<IReadOnlyList<SprintId>>(ids);
    }

    public async Task<AppendOutcome> AppendTransitionAsync(
        string projectRoot,
        SprintId sprintId,
        AggregateKind aggregateKind,
        string aggregateId,
        string type,
        string messageKey,
        string toState,
        long expectedAggregateVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? extraArguments = null)
    {
        string directory = SprintDirectory(projectRoot, sprintId);
        Directory.CreateDirectory(directory);
        string eventsPath = EventsPath(directory);
        string idempotencyPath = IdempotencyPath(directory);

        Dictionary<Guid, DateTimeOffset> applied =
            await ReadIdempotencyAsync(idempotencyPath, cancellationToken).ConfigureAwait(false);
        if (applied.ContainsKey(idempotencyKey))
        {
            SprintWorkflowState? replayed =
                await LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            return new(true, replayed, DiagnosticCodes.None);
        }

        IReadOnlyList<WorkflowEvent> events =
            await ReadEventsAsync(eventsPath, cancellationToken).ConfigureAwait(false);
        long currentVersion = CurrentVersion(events, aggregateKind, aggregateId);
        if (currentVersion != expectedAggregateVersion)
        {
            return AppendOutcome.Conflict;
        }

        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.ToStateArgument] = toState,
        };
        if (extraArguments is not null)
        {
            foreach ((string key, string? value) in extraArguments)
            {
                arguments[key] = value;
            }
        }

        WorkflowEvent proposed = new(
            Guid.NewGuid(),
            events.Count,
            clock.UtcNow,
            type,
            new(aggregateKind, aggregateId, expectedAggregateVersion + 1),
            messageKey,
            arguments);

        await AppendLineAsync(eventsPath, WorkflowEventCodec.Serialize(proposed), cancellationToken)
            .ConfigureAwait(false);

        applied[idempotencyKey] = clock.UtcNow;
        await WriteIdempotencyAsync(idempotencyPath, applied, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WorkflowEvent> persisted =
            await ReadEventsAsync(eventsPath, cancellationToken).ConfigureAwait(false);
        return new(true, WorkflowFold.Apply(sprintId, persisted), DiagnosticCodes.None);
    }

    private static string SprintsRoot(string projectRoot) =>
        Path.Combine(ProjectRootResolver.ForgeDirectory(projectRoot), SprintsDirectoryName);

    private static string EventsPath(string sprintDirectory) => Path.Combine(sprintDirectory, EventsFileName);

    private static string IdempotencyPath(string sprintDirectory) =>
        Path.Combine(sprintDirectory, IdempotencyFileName);

    private static string DefinitionPath(string sprintDirectory) =>
        Path.Combine(sprintDirectory, DefinitionFileName);

    private static string ResultsDirectory(string sprintDirectory) => Path.Combine(sprintDirectory, "results");

    private static string HandoffsDirectory(string sprintDirectory) => Path.Combine(sprintDirectory, "handoffs");

    private static string FindingsPath(string sprintDirectory) => Path.Combine(sprintDirectory, "findings.json");

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static async Task<Dictionary<Guid, PersistedFinding>> ReadFindingsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string path = FindingsPath(SprintDirectory(projectRoot, sprintId));
        if (!File.Exists(path))
        {
            return new();
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<Guid, PersistedFinding>>(bytes, DefinitionJsonOptions) ?? new();
    }

    private static PersistedDiagnostic ToPersisted(NodeDiagnostic diagnostic) =>
        new()
        {
            Code = diagnostic.Code,
            Category = diagnostic.Category,
            MessageKey = diagnostic.MessageKey,
            Arguments = new(diagnostic.Arguments, StringComparer.Ordinal),
        };

    private static NodeDiagnostic FromPersisted(PersistedDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Category, diagnostic.MessageKey, diagnostic.Arguments);

    private static PersistedFinding ToPersisted(Finding finding) =>
        new()
        {
            FindingId = finding.FindingId,
            Fingerprint = finding.Fingerprint,
            Severity = WorkflowStateNames.ToSnakeCase(finding.Severity),
            Status = WorkflowStateNames.ToSnakeCase(finding.Status),
            MessageKey = finding.MessageKey,
            Arguments = new(finding.Arguments, StringComparer.Ordinal),
            Evidence = [.. finding.Evidence],
            LocationPath = finding.Location?.Path,
            LocationLine = finding.Location?.Line,
        };

    private static Finding FromPersisted(SprintId sprintId, PersistedFinding finding) =>
        new(
            finding.FindingId,
            sprintId,
            finding.Fingerprint,
            WorkflowStateNames.Parse<FindingSeverity>(finding.Severity),
            WorkflowStateNames.Parse<FindingStatus>(finding.Status),
            finding.MessageKey,
            finding.Arguments,
            finding.Evidence,
            finding.LocationPath is { } path ? new(path, finding.LocationLine) : null);

    private static PersistedArtifact ToPersisted(HandoffArtifact artifact) =>
        new()
        {
            Digest = artifact.Digest,
            MediaType = artifact.MediaType,
            Audience = WorkflowStateNames.ToSnakeCase(artifact.Audience),
            Language = artifact.Language,
            PolicySnapshotHash = artifact.PolicySnapshotHash,
            GeneratorVersion = artifact.GeneratorVersion,
        };

    private static HandoffArtifact FromPersisted(PersistedArtifact artifact) =>
        new(
            artifact.Digest,
            artifact.MediaType,
            WorkflowStateNames.Parse<ArtifactAudience>(artifact.Audience),
            artifact.Language,
            artifact.PolicySnapshotHash,
            artifact.GeneratorVersion);

    private static Handoff FromPersisted(SprintId sprintId, PersistedHandoff handoff) =>
        new(
            handoff.HandoffId,
            sprintId,
            new(handoff.NodeId),
            handoff.BaseSha,
            handoff.Summary,
            handoff.Decisions,
            [.. handoff.Artifacts.Select(FromPersisted)],
            handoff.OpenRisks,
            handoff.NextNodeIds);

    private static PersistedDependency ToPersisted(SprintDependency dependency) =>
        new()
        {
            Kind = WorkflowStateNames.ToSnakeCase(dependency.Kind),
            Reference = dependency.Reference,
            SourceSprintId = dependency.SourceSprintId?.Value.ToString("D"),
        };

    private static SprintDependency FromPersisted(PersistedDependency dependency) =>
        new(
            WorkflowStateNames.Parse<SprintDependencyKind>(dependency.Kind),
            dependency.Reference,
            dependency.SourceSprintId is { } sourceSprintId ? new(Guid.Parse(sourceSprintId)) : null);

    private static PersistedNode ToPersisted(NodeDefinition node) =>
        new()
        {
            Id = node.Id,
            Kind = WorkflowStateNames.ToSnakeCase(node.Kind),
            DependsOn = [.. node.DependsOn],
        };

    private static NodeDefinition FromPersisted(PersistedNode node) =>
        new(node.Id, WorkflowStateNames.Parse<NodeKind>(node.Kind), node.DependsOn);

    private static long CurrentVersion(IReadOnlyList<WorkflowEvent> events, AggregateKind kind, string id) =>
        events
            .Where(item => item.Aggregate.Kind == kind && item.Aggregate.Id == id)
            .Select(item => item.Aggregate.Version)
            .DefaultIfEmpty(0)
            .Max();

    private static async Task<IReadOnlyList<WorkflowEvent>> ReadEventsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        List<WorkflowEvent> events = new(lines.Length);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                events.Add(WorkflowEventCodec.Deserialize(line));
            }
            catch (Exception error) when (index == lines.Length - 1 && IsTornWrite(error))
            {
                // Only the last line can be a torn write left by a crash mid-append: every earlier
                // line was already flushed to disk before its append call returned success.
                break;
            }
        }

        return events;
    }

    private static bool IsTornWrite(Exception error) =>
        error is JsonException or InvalidDataException or FormatException;

    private static async Task AppendLineAsync(string path, string line, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("The event log path has no directory.");
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await using (FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }

        DirectoryFlusher.Flush(directory);
    }

    private static async Task<Dictionary<Guid, DateTimeOffset>> ReadIdempotencyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new();
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<Guid, DateTimeOffset>>(bytes) ?? new();
        }
        catch (JsonException)
        {
            // An unreadable idempotency cache degrades to "nothing applied yet": at worst a
            // retried command re-validates its expected version instead of short-circuiting.
            return new();
        }
    }

    private static Task WriteIdempotencyAsync(
        string path,
        Dictionary<Guid, DateTimeOffset> applied,
        CancellationToken cancellationToken) =>
        AtomicConfigurationFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(applied), cancellationToken);

    private sealed class PersistedDefinition
    {
        public string BaseCommit { get; set; } = string.Empty;

        public string Workflow { get; set; } = string.Empty;

        public string WorkflowVersion { get; set; } = string.Empty;

        public Dictionary<string, string> ConfigurationSnapshot { get; set; } = new(StringComparer.Ordinal);

        public List<PersistedDependency> Dependencies { get; set; } = [];

        public List<PersistedNode> Graph { get; set; } = [];

        public string ConversationLanguage { get; set; } = "en";

        public string ArtifactPolicySnapshotHash { get; set; } = string.Empty;

        public DateTimeOffset FrozenAt { get; set; }
    }

    private sealed class PersistedDependency
    {
        public string Kind { get; set; } = string.Empty;

        public string Reference { get; set; } = string.Empty;

        public string? SourceSprintId { get; set; }
    }

    private sealed class PersistedNode
    {
        public string Id { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public List<string> DependsOn { get; set; } = [];
    }

    private sealed class PersistedNodeResult
    {
        public string NodeId { get; set; } = string.Empty;

        public string AttemptId { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset CompletedAt { get; set; }

        public string InputDigest { get; set; } = string.Empty;

        public List<string> Outputs { get; set; } = [];

        public List<PersistedDiagnostic> Diagnostics { get; set; } = [];
    }

    private sealed class PersistedDiagnostic
    {
        public string Code { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string MessageKey { get; set; } = string.Empty;

        public Dictionary<string, string?> Arguments { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PersistedFinding
    {
        public Guid FindingId { get; set; }

        public string Fingerprint { get; set; } = string.Empty;

        public string Severity { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string MessageKey { get; set; } = string.Empty;

        public Dictionary<string, string?> Arguments { get; set; } = new(StringComparer.Ordinal);

        public List<string> Evidence { get; set; } = [];

        public string? LocationPath { get; set; }

        public int? LocationLine { get; set; }
    }

    private sealed class PersistedHandoff
    {
        public Guid HandoffId { get; set; }

        public string NodeId { get; set; } = string.Empty;

        public string BaseSha { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public List<string> Decisions { get; set; } = [];

        public List<PersistedArtifact> Artifacts { get; set; } = [];

        public List<string> OpenRisks { get; set; } = [];

        public List<string>? NextNodeIds { get; set; }
    }

    private sealed class PersistedArtifact
    {
        public string Digest { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string Audience { get; set; } = string.Empty;

        public string? Language { get; set; }

        public string PolicySnapshotHash { get; set; } = string.Empty;

        public string GeneratorVersion { get; set; } = string.Empty;
    }
}
