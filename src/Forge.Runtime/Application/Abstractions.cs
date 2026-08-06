using Forge.Domain;

namespace Forge.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IFileSystem
{
    bool FileExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken);
}

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public interface INetworkClient
{
    Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken);
}

public interface IEnvironmentPaths
{
    string LocalApplicationData { get; }

    string UserProfile { get; }

    string CurrentDirectory { get; }
}

public interface IRepository
{
    /// <summary>Resolves the current commit a new sprint would freeze as its immutable base.</summary>
    Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken);
}

public sealed record AppendOutcome(bool Succeeded, SprintWorkflowState? State, string DiagnosticCode)
{
    public static AppendOutcome Conflict { get; } = new(false, null, DiagnosticCodes.WorkflowEventConflict);
}

/// <summary>
/// Durable, event-sourced sprint/node/attempt state. Every mutation is an append-only
/// <see cref="WorkflowEvent"/>; current state is always folded from that stream, never cached
/// authoritatively, so a crash can never leave state inconsistent with its own history.
/// </summary>
public interface ISprintStore
{
    Task<SprintWorkflowState?> LoadAsync(string projectRoot, SprintId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SprintId>> ListAsync(string projectRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one transition if <paramref name="expectedAggregateVersion"/> still matches the
    /// aggregate's current version (0 for an aggregate that does not exist yet) and
    /// <paramref name="idempotencyKey"/> was not already applied. A replayed key returns the
    /// current state without appending again; a version mismatch returns
    /// <see cref="AppendOutcome.Conflict"/> without any side effect.
    /// </summary>
    Task<AppendOutcome> AppendTransitionAsync(
        string projectRoot,
        SprintId sprintId,
        AggregateKind aggregateKind,
        string aggregateId,
        string type,
        string messageKey,
        string toState,
        long expectedAggregateVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a sprint's frozen definition once. Nothing in this store ever updates it again;
    /// callers must write it exactly once, before the sprint becomes visible to other operations.
    /// </summary>
    Task SaveDefinitionAsync(string projectRoot, SprintDefinition definition, CancellationToken cancellationToken);

    Task<SprintDefinition?> LoadDefinitionAsync(
        string projectRoot,
        SprintId id,
        CancellationToken cancellationToken);
}

public interface IArtifactStore;

public interface ISafeLogger
{
    void Information(string eventName, IReadOnlyDictionary<string, object?> properties);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
