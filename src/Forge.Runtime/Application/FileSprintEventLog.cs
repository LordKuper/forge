using System.Collections.Concurrent;
using System.Globalization;
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
/// every read avoids a second crash-recovery surface for cheap I/O. Add a cache if project snapshot
/// profiling ever shows folding is the bottleneck.
/// </remarks>
public sealed class FileSprintEventLog(IClock clock) : ISprintStore
{
    private const string SprintsDirectoryName = "sprints";
    private const string EventsFileName = "events.jsonl";
    private const string IdempotencyFileName = "idempotency.json";
    private const string DefinitionFileName = "definition.json";
    private const string CreatedMarkerFileName = "created.marker";
    private const string LegacyFindingsFileName = "findings.json";
    private const string RouteDecisionEventType = WorkflowEvent.RouteDecisionRecordedType;
    private const string LegacyRoutingDirectoryName = "routing";
    private const string LegacyRoutingMigratedMarker = "migrated-to-sprint-journal";
    private static readonly JsonSerializerOptions DefinitionJsonOptions = ConfigurationSchemaCodec.SerializerOptions;

    public static string SprintDirectory(string projectRoot, SprintId id) =>
        Path.Combine(SprintsRoot(projectRoot), id.Value.ToString("N"));

    /// <summary>
    /// Marks a sprint's creation as durably complete: only sprints with this marker are returned by
    /// <see cref="ListAsync"/>. Every other write below is safe to call before this marker exists —
    /// a partially built sprint is fully addressable by every method that already knows its id — so
    /// a crash before this call simply leaves an invisible, safely resumable sprint behind instead
    /// of one <see cref="ListAsync"/> would otherwise surface as an orphan.
    /// </summary>
    public Task MarkSprintCreatedAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
        AtomicConfigurationFile.WriteAsync(
            Path.Combine(SprintDirectory(projectRoot, id), CreatedMarkerFileName),
            ReadOnlyMemory<byte>.Empty,
            cancellationToken);

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
        string path = Path.Combine(directory, $"{result.AttemptId.Value:N}.json");
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
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, DefinitionJsonOptions);

        // Write-once: a node result is immutable once recorded for a given attempt id, and a
        // resumable compound operation retried after a crash must be free to call this again with
        // the exact same result and get a safe no-op. The existence check and the write below share
        // one lock per path so "already exists" is never raced by a second, concurrent write for the
        // same attempt id — and an existing file's content is compared, not just its presence, so a
        // genuinely different result for the same id surfaces as a conflict instead of silently
        // keeping whichever write happened to land first.
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                byte[] existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidOperationException(
                        $"A different node result already exists for attempt '{result.AttemptId.Value:D}'.");
                }

                return;
            }

            await AtomicConfigurationFile.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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
        string sprintDirectory = SprintDirectory(projectRoot, finding.SprintId);
        await MigrateLegacyFindingsAsync(sprintDirectory, cancellationToken).ConfigureAwait(false);
        string directory = FindingsDirectory(sprintDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{finding.FindingId:N}.json");

        // One atomic file per finding id: two findings never share a file, so a concurrent write to
        // a *different* id can never lose this one. A concurrent write to the *same* id is still a
        // real race (e.g. two RecordFinding/ResolveFinding calls racing) — serialized here rather
        // than left to whichever atomic replace lands last silently winning.
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicConfigurationFile.WriteAsync(
                path,
                JsonSerializer.SerializeToUtf8Bytes(ToPersisted(finding), DefinitionJsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<Finding>> GetFindingsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string sprintDirectory = SprintDirectory(projectRoot, sprintId);
        await MigrateLegacyFindingsAsync(sprintDirectory, cancellationToken).ConfigureAwait(false);
        string directory = FindingsDirectory(sprintDirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<Finding> findings = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(item => item, StringComparer.Ordinal))
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            PersistedFinding persisted = JsonSerializer.Deserialize<PersistedFinding>(bytes, DefinitionJsonOptions) ??
                throw new InvalidDataException($"The finding at '{path}' is empty.");
            findings.Add(FromPersisted(sprintId, persisted));
        }

        return findings;
    }

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
            Path.Combine(directory, $"{handoff.HandoffId:N}.json"),
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

    public async Task AppendRouteDecisionAsync(
        string projectRoot,
        RouteDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        string directory = SprintDirectory(projectRoot, decision.SprintId);
        Directory.CreateDirectory(directory);
        string eventsPath = EventsPath(directory);
        SemaphoreSlim gate = Locks.GetOrAdd(directory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<WorkflowEvent> events = [.. await ReadEventsAsync(eventsPath, cancellationToken).ConfigureAwait(false)];
            ValidateJournal(events);
            await MigrateLegacyRoutingAsync(directory, decision.SprintId, events, cancellationToken)
                .ConfigureAwait(false);
            await AppendRoutingEventAsync(eventsPath, decision, events, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAttemptActivityAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        string directory = SprintDirectory(projectRoot, sprintId);
        Directory.CreateDirectory(directory);
        string eventsPath = EventsPath(directory);
        SemaphoreSlim gate = Locks.GetOrAdd(directory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<WorkflowEvent> events = [.. await ReadEventsAsync(eventsPath, cancellationToken).ConfigureAwait(false)];
            ValidateJournal(events);
            string attemptKey = attemptId.Value.ToString("D");
            long attemptVersion = CurrentVersion(events, AggregateKind.Attempt, attemptKey);
            WorkflowEvent activity = new(
                Guid.NewGuid(),
                events.Count,
                clock.UtcNow,
                WorkflowEvent.AttemptActivityRecordedType,
                new(AggregateKind.Attempt, attemptKey, attemptVersion),
                "workflow.attempt_activity",
                new Dictionary<string, string?>(StringComparer.Ordinal));
            await AppendLineAsync(eventsPath, WorkflowEventCodec.Serialize(activity), cancellationToken)
                .ConfigureAwait(false);
            events.Add(activity);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        List<WorkflowEvent> events = await LoadValidatedEventsAsync(
            SprintDirectory(projectRoot, sprintId), sprintId, cancellationToken).ConfigureAwait(false);
        return events
            .Where(item => item.Type == RouteDecisionEventType)
            .Select(item => FromRoutingEvent(sprintId, item))
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        await LoadValidatedEventsAsync(SprintDirectory(projectRoot, sprintId), sprintId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The lock+read+validate+migrate sequence every raw-event reader (<see cref="GetEventsAsync"/>,
    /// <see cref="GetRouteDecisionsAsync"/>) shares, so future validation/migration changes apply to
    /// both instead of risking the two silently diverging.</summary>
    private static async Task<List<WorkflowEvent>> LoadValidatedEventsAsync(
        string directory,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetOrAdd(directory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                List<WorkflowEvent> events =
                    [.. await ReadEventsAsync(EventsPath(directory), cancellationToken).ConfigureAwait(false)];
                ValidateJournal(events);
                await MigrateLegacyRoutingAsync(directory, sprintId, events, cancellationToken).ConfigureAwait(false);
                return events;
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"The sprint journal for '{sprintId.Value}' is corrupt.", error);
            }
        }
        finally
        {
            gate.Release();
        }
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
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid value) &&
                File.Exists(Path.Combine(directory, CreatedMarkerFileName)))
            {
                ids.Add(new(value));
            }
        }

        return Task.FromResult<IReadOnlyList<SprintId>>(ids);
    }

    /// <summary>
    /// One lock per key serializes a critical section below: sprint directory keys guard the
    /// event log's read-check-append sequence, and finding/result/legacy-migration file path keys
    /// guard a same-id read-then-replace. Different keys never contend (different sprints, different
    /// findings, different results).
    /// </summary>
    /// <remarks>
    /// ponytail: entries are never evicted, so this grows by one `SemaphoreSlim` per distinct sprint
    /// directory and per distinct finding/result/legacy-migration path ever touched, for the life of
    /// the process. Fine at MVP scale (a CLI process is short-lived; a long-running host would still
    /// need one entry per active sprint/record, not per operation). Add eviction (e.g. on sprint
    /// completion) if a long-lived host process ever makes this measurable.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

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

        SemaphoreSlim gate = Locks.GetOrAdd(directory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<Guid, DateTimeOffset> applied =
                await ReadIdempotencyAsync(idempotencyPath, cancellationToken).ConfigureAwait(false);
            if (applied.ContainsKey(idempotencyKey))
            {
                SprintWorkflowState? replayed =
                    await LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
                return new(true, replayed, DiagnosticCodes.None, true);
            }

            IReadOnlyList<WorkflowEvent> events =
                await ReadEventsAsync(eventsPath, cancellationToken).ConfigureAwait(false);
            ValidateJournal(events);
            long currentVersion = CurrentVersion(events, aggregateKind, aggregateId);
            if (currentVersion != expectedAggregateVersion)
            {
                return AppendOutcome.Conflict;
            }

            string? currentStateText = CurrentStateText(events, aggregateKind, aggregateId);
            if (!IsLegalTransition(aggregateKind, currentStateText, toState))
            {
                return new(false, null, DiagnosticCodes.WorkflowTransitionInvalid);
            }

            Dictionary<string, string?> arguments = new(StringComparer.Ordinal);
            if (extraArguments is not null)
            {
                foreach ((string key, string? value) in extraArguments)
                {
                    arguments[key] = value;
                }
            }

            // Assigned last so no caller-supplied extra argument — accidental or adversarial — can
            // override the state this append was validated against above.
            arguments[WorkflowEvent.ToStateArgument] = toState;

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
        catch (IOException)
        {
            // A second process (or, one day, a second machine) holding the file is the only other
            // writer this store can ever encounter; fail with a diagnostic instead of a raw
            // exception rather than pretend the append happened.
            return new(false, null, DiagnosticCodes.WorkflowStoreBusy);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException)
        {
            // Real corruption in an already-terminated line (never produced by this store's own
            // write path) — a diagnostic, not a crash reaching all the way out to the caller.
            return new(false, null, DiagnosticCodes.WorkflowLogCorrupted);
        }
        finally
        {
            gate.Release();
        }
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

    private static string FindingsDirectory(string sprintDirectory) => Path.Combine(sprintDirectory, "findings");

    /// <summary>
    /// v0.9.0 stored every finding in one shared <c>findings.json</c>; the per-finding-file layout
    /// replaced it without a migration, which would silently drop every existing finding on first
    /// read after an update. Migrating lazily here — on the first finding read or write for a sprint
    /// — needs no separate startup pass and stays correct for a sprint nobody touches again. Each
    /// finding file is only ever written if absent, and the legacy file is deleted only once every
    /// finding has landed in its own file, so an interrupted migration simply repeats the still-safe
    /// no-op writes on the next call and completes normally.
    /// </summary>
    private static async Task MigrateLegacyFindingsAsync(string sprintDirectory, CancellationToken cancellationToken)
    {
        string legacyPath = Path.Combine(sprintDirectory, LegacyFindingsFileName);
        if (!File.Exists(legacyPath))
        {
            return;
        }

        SemaphoreSlim gate = Locks.GetOrAdd(legacyPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(legacyPath))
            {
                return;
            }

            byte[] bytes = await File.ReadAllBytesAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            Dictionary<Guid, PersistedFinding> legacy =
                JsonSerializer.Deserialize<Dictionary<Guid, PersistedFinding>>(bytes, DefinitionJsonOptions) ?? new();

            string directory = FindingsDirectory(sprintDirectory);
            Directory.CreateDirectory(directory);
            foreach ((Guid findingId, PersistedFinding persisted) in legacy)
            {
                string findingPath = Path.Combine(directory, $"{findingId:N}.json");
                if (!File.Exists(findingPath))
                {
                    await AtomicConfigurationFile.WriteAsync(
                        findingPath,
                        JsonSerializer.SerializeToUtf8Bytes(persisted, DefinitionJsonOptions),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            File.Delete(legacyPath);
        }
        finally
        {
            gate.Release();
        }
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
            .Where(item => item.Aggregate.Kind == kind && item.Aggregate.Id == id &&
                WorkflowFold.IsTransitionRecord(item))
            .Select(item => item.Aggregate.Version)
            .DefaultIfEmpty(0)
            .Max();

    private static void ValidateJournal(IEnumerable<WorkflowEvent> events)
    {
        foreach (WorkflowEvent item in events)
        {
            _ = WorkflowFold.IsTransitionRecord(item);
        }
    }

    private static string? CurrentStateText(IReadOnlyList<WorkflowEvent> events, AggregateKind kind, string id) =>
        events
            .Where(item => item.Aggregate.Kind == kind && item.Aggregate.Id == id &&
                WorkflowFold.IsTransitionRecord(item))
            .OrderByDescending(item => item.Aggregate.Version)
            .FirstOrDefault()
            ?.Arguments.GetValueOrDefault(WorkflowEvent.ToStateArgument);

    private static RouteDecision FromRoutingEvent(SprintId sprintId, WorkflowEvent item)
    {
        try
        {
            string Required(string key) => item.Arguments.GetValueOrDefault(key) ??
                throw new InvalidDataException($"Routing event '{item.EventId}' is missing '{key}'.");
            return new(
                item.EventId,
                sprintId,
                Required("node_id"),
                new(Guid.Parse(Required("attempt_id"))),
                new(Required("provider"), Required("model"), Required("surface")),
                WorkflowStateNames.Parse<RouteOutcome>(Required("outcome")),
                item.Arguments.GetValueOrDefault("failure_class") is { } failure
                    ? WorkflowStateNames.Parse<FailureClass>(failure)
                    : null,
                item.OccurredAt,
                item.Arguments.GetValueOrDefault("resume_not_before") is { } resumeNotBefore
                    ? DateTimeOffset.Parse(resumeNotBefore, CultureInfo.InvariantCulture)
                    : null);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException($"Routing event '{item.EventId}' is corrupt.", error);
        }
    }

    private static async Task AppendRoutingEventAsync(
        string eventsPath,
        RouteDecision decision,
        List<WorkflowEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Any(item => item.EventId == decision.DecisionId))
        {
            return;
        }

        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            ["node_id"] = decision.NodeId,
            ["attempt_id"] = decision.AttemptId.Value.ToString("D"),
            ["provider"] = decision.Key.Provider,
            ["model"] = decision.Key.Model,
            ["surface"] = decision.Key.Surface,
            ["outcome"] = WorkflowStateNames.ToSnakeCase(decision.Outcome),
            ["failure_class"] = decision.FailureClass is { } failure
                ? WorkflowStateNames.ToSnakeCase(failure)
                : null,
            ["resume_not_before"] = decision.ResumeNotBefore?.ToString("O", CultureInfo.InvariantCulture),
        };
        long sprintVersion = CurrentVersion(
            events,
            AggregateKind.Sprint,
            decision.SprintId.Value.ToString("D"));
        WorkflowEvent routingEvent = new(
            decision.DecisionId,
            events.Count,
            decision.DecidedAt,
            RouteDecisionEventType,
            new(AggregateKind.Sprint, decision.SprintId.Value.ToString("D"), Math.Max(1, sprintVersion)),
            "routing.decision_recorded",
            arguments);
        await AppendLineAsync(eventsPath, WorkflowEventCodec.Serialize(routingEvent), cancellationToken)
            .ConfigureAwait(false);
        events.Add(routingEvent);
    }

    /// <summary>Imports the pre-v0.11 routing sidecar once. Deterministic event ids make an
    /// interrupted import safe to repeat; the old files remain read-only rollback evidence.</summary>
    private static async Task MigrateLegacyRoutingAsync(
        string sprintDirectory,
        SprintId sprintId,
        List<WorkflowEvent> events,
        CancellationToken cancellationToken)
    {
        string legacyDirectory = Path.Combine(sprintDirectory, LegacyRoutingDirectoryName);
        string marker = Path.Combine(legacyDirectory, LegacyRoutingMigratedMarker);
        if (!Directory.Exists(legacyDirectory) || File.Exists(marker))
        {
            return;
        }

        string eventsPath = EventsPath(sprintDirectory);
        string decisionsPath = Path.Combine(legacyDirectory, "decisions.jsonl");
        if (File.Exists(decisionsPath))
        {
            string content = await File.ReadAllTextAsync(decisionsPath, cancellationToken).ConfigureAwait(false);
            int completeLength = content.LastIndexOf('\n') + 1;
            foreach (string line in content[..completeLength].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                RouteDecision decision = new(
                    root.GetProperty("decision_id").GetGuid(),
                    sprintId,
                    root.GetProperty("node_id").GetString()!,
                    new(Guid.Parse(root.GetProperty("attempt_id").GetString()!)),
                    new(
                        root.GetProperty("provider").GetString()!,
                        root.GetProperty("model").GetString()!,
                        root.GetProperty("surface").GetString()!),
                    WorkflowStateNames.Parse<RouteOutcome>(root.GetProperty("outcome").GetString()!),
                    root.TryGetProperty("failure_class", out JsonElement failure) &&
                        failure.ValueKind == JsonValueKind.String
                        ? WorkflowStateNames.Parse<FailureClass>(failure.GetString()!)
                        : null,
                    root.GetProperty("decided_at").GetDateTimeOffset());
                await AppendRoutingEventAsync(eventsPath, decision, events, cancellationToken).ConfigureAwait(false);
            }
        }

        // The old store updated retry-budget.json before appending a Routed decision and updated
        // it before appending an Excluded refund. A crash between those writes leaves the snapshot
        // one unit ahead of or behind decisions.jsonl. Reconcile that durable snapshot into
        // deterministic journal records before marking migration complete.
        string budgetPath = Path.Combine(legacyDirectory, "retry-budget.json");
        if (File.Exists(budgetPath))
        {
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllBytesAsync(budgetPath, cancellationToken).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            int total = root.GetProperty("total").GetInt32();
            int targetConsumed = root.GetProperty("consumed").GetInt32();
            if (total != RoutingLedger.DefaultRetryBudget || targetConsumed < 0 || targetConsumed > total)
            {
                throw new InvalidDataException("The legacy routing retry budget is invalid.");
            }

            int recordedConsumed = RoutingConsumption(events);
            DateTimeOffset migratedAt = new(File.GetLastWriteTimeUtc(budgetPath));
            HealthKey migrationKey = new("migration", "legacy-retry-budget", "sprint");
            for (int index = recordedConsumed; index < targetConsumed; index++)
            {
                await AppendRoutingEventAsync(
                    eventsPath,
                    LegacyDecision(sprintId, migrationKey, RouteOutcome.Routed, migratedAt, $"budget-consume-{index}"),
                    events,
                    cancellationToken).ConfigureAwait(false);
            }

            for (int index = targetConsumed; index < recordedConsumed; index++)
            {
                await AppendRoutingEventAsync(
                    eventsPath,
                    LegacyDecision(
                        sprintId, migrationKey, RouteOutcome.Excluded, migratedAt, $"budget-refund-{index}",
                        FailureClass.Policy),
                    events,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        string breakersDirectory = Path.Combine(legacyDirectory, "breakers");
        if (Directory.Exists(breakersDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(breakersDirectory, "*.json"))
            {
                using JsonDocument document = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                HealthKey key = new(
                    root.GetProperty("provider").GetString()!,
                    root.GetProperty("model").GetString()!,
                    root.GetProperty("surface").GetString()!);
                CircuitState state = WorkflowStateNames.Parse<CircuitState>(root.GetProperty("state").GetString()!);
                int failures = root.GetProperty("consecutive_failures").GetInt32();
                DateTimeOffset updatedAt = root.GetProperty("updated_at").GetDateTimeOffset();
                DateTimeOffset failureTime = root.TryGetProperty("opened_at", out JsonElement openedAt) &&
                    openedAt.ValueKind == JsonValueKind.String
                    ? openedAt.GetDateTimeOffset()
                    : updatedAt;
                int syntheticFailures = state == CircuitState.Open || state == CircuitState.HalfOpen
                    ? Math.Max(RoutingLedger.DefaultFailureThreshold, failures)
                    : failures;
                for (int index = 0; index < syntheticFailures; index++)
                {
                    await AppendRoutingEventAsync(
                        eventsPath,
                        LegacyDecision(sprintId, key, RouteOutcome.Failed, failureTime, $"breaker-failed-{index}"),
                        events,
                        cancellationToken).ConfigureAwait(false);
                }

                if (state == CircuitState.Closed && failures == 0)
                {
                    await AppendRoutingEventAsync(
                        eventsPath,
                        LegacyDecision(sprintId, key, RouteOutcome.Succeeded, updatedAt, "breaker-closed"),
                        events,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (state == CircuitState.HalfOpen)
                {
                    await AppendRoutingEventAsync(
                        eventsPath,
                        LegacyDecision(sprintId, key, RouteOutcome.Routed, updatedAt, "breaker-half-open"),
                        events,
                        cancellationToken).ConfigureAwait(false);
                    await AppendRoutingEventAsync(
                        eventsPath,
                        LegacyDecision(
                            sprintId, key, RouteOutcome.Excluded, updatedAt, "breaker-half-open-refund",
                            FailureClass.Policy),
                        events,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await AtomicConfigurationFile.WriteAsync(marker, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    private static int RoutingConsumption(IEnumerable<WorkflowEvent> events)
    {
        int consumed = 0;
        foreach (WorkflowEvent item in events.Where(item => item.Type == RouteDecisionEventType))
        {
            RouteOutcome outcome = WorkflowStateNames.Parse<RouteOutcome>(
                item.Arguments.GetValueOrDefault("outcome") ??
                throw new InvalidDataException($"Routing event '{item.EventId}' is missing 'outcome'."));
            consumed = outcome switch
            {
                RouteOutcome.Routed => consumed + 1,
                RouteOutcome.Excluded => Math.Max(0, consumed - 1),
                _ => consumed,
            };
        }

        return consumed;
    }

    private static RouteDecision LegacyDecision(
        SprintId sprintId,
        HealthKey key,
        RouteOutcome outcome,
        DateTimeOffset decidedAt,
        string discriminator,
        FailureClass? failureClass = null)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"routing-migration|{sprintId.Value:D}|{key.Canonical}|{discriminator}"));
        return new(
            new Guid(hash.AsSpan(0, 16)),
            sprintId,
            "migration",
            new(Guid.Empty),
            key,
            outcome,
            failureClass,
            decidedAt);
    }

    /// <summary>
    /// The single store-level guarantee that every append, from any caller, actually respects the
    /// frozen state machine — a caller-side check (as <c>SprintOrchestrator</c> already does for
    /// its own sprint transitions) is not enough, since nothing stopped a bug elsewhere in the
    /// engine from appending an illegal node/attempt transition directly.
    /// </summary>
    private static bool IsLegalTransition(AggregateKind kind, string? fromStateText, string toStateText)
    {
        switch (kind)
        {
            case AggregateKind.Sprint:
                {
                    SprintState to = WorkflowStateNames.Parse<SprintState>(toStateText);
                    return fromStateText is null
                        ? to == WorkflowStateMachines.SprintInitial
                        : WorkflowStateMachines.CanTransition(WorkflowStateNames.Parse<SprintState>(fromStateText), to);
                }

            case AggregateKind.Node:
                {
                    NodeState to = WorkflowStateNames.Parse<NodeState>(toStateText);
                    return fromStateText is null
                        ? to == WorkflowStateMachines.NodeInitial
                        : WorkflowStateMachines.CanTransition(WorkflowStateNames.Parse<NodeState>(fromStateText), to);
                }

            case AggregateKind.Attempt:
                {
                    AttemptState to = WorkflowStateNames.Parse<AttemptState>(toStateText);
                    return fromStateText is null
                        ? to == WorkflowStateMachines.AttemptInitial
                        : WorkflowStateMachines.CanTransition(WorkflowStateNames.Parse<AttemptState>(fromStateText), to);
                }

            default:
                throw new InvalidDataException($"Unknown aggregate kind '{kind}'.");
        }
    }

    /// <summary>
    /// Reads events by byte offset, not by pre-split lines, because a torn trailing line must be
    /// truncated away — not merely skipped — before the file is trusted again. Skipping it on read
    /// but leaving the bytes in place let the next append concatenate a fresh event onto the
    /// garbage, silently losing that event while still reporting success.
    /// </summary>
    /// <summary>
    /// Every append writes its whole "json + '\n'" buffer as a single contiguous write, so a
    /// short/torn write from a mid-append crash can only ever drop a *suffix* of that buffer — it
    /// can never produce a terminating '\n' without every byte before it in the same write having
    /// landed too. A trailing segment with no '\n' at all is therefore always torn and always
    /// discarded whole, whether or not it happens to parse as valid JSON: a crash can land exactly
    /// after a complete object but before that object's own newline, and keeping it just because it
    /// parses would keep an append its own caller never received a success result for. A
    /// newline-terminated line that still fails to parse is real corruption, not a torn write —
    /// this store never produces one, so that always propagates rather than being silently dropped.
    /// </summary>
    private static async Task<IReadOnlyList<WorkflowEvent>> ReadEventsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        List<WorkflowEvent> events = [];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int newlineIndex = Array.IndexOf(bytes, (byte)'\n', offset);
            if (newlineIndex < 0)
            {
                await TruncateAsync(path, offset, cancellationToken).ConfigureAwait(false);
                return events;
            }

            int lineLength = newlineIndex - offset;
            if (lineLength > 0)
            {
                events.Add(WorkflowEventCodec.Deserialize(Encoding.UTF8.GetString(bytes, offset, lineLength)));
            }

            offset = newlineIndex + 1;
        }

        return events;
    }

    private static async Task TruncateAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(length);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            DirectoryFlusher.Flush(directory);
        }
    }

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

    // ponytail: idempotency.json grows by one entry per append, forever, and is rewritten in full
    // on every append (O(n) per write, not amortized). Fine at MVP sprint scale (events number in
    // the tens to low hundreds); prune or restructure if a long-lived sprint's event count ever
    // makes this measurable. A crash between the event append above and this write is deliberate:
    // it leaves a retried command re-validating its expected version rather than silently
    // replaying, since writing the key *before* the event would risk marking a never-durable
    // transition as already applied — the current ordering is the safer of the two failure modes.
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
