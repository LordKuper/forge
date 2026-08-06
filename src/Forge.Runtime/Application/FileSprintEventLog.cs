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
            persisted.FrozenAt);
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
        CancellationToken cancellationToken)
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

        WorkflowEvent proposed = new(
            Guid.NewGuid(),
            events.Count,
            clock.UtcNow,
            type,
            new(aggregateKind, aggregateId, expectedAggregateVersion + 1),
            messageKey,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkflowEvent.ToStateArgument] = toState,
            });

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

        public DateTimeOffset FrozenAt { get; set; }
    }

    private sealed class PersistedDependency
    {
        public string Kind { get; set; } = string.Empty;

        public string Reference { get; set; } = string.Empty;

        public string? SourceSprintId { get; set; }
    }
}
