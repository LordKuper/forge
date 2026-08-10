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

public sealed record GitOperationResult(bool Succeeded, string? Commit, string DiagnosticCode)
{
    public static GitOperationResult Ok(string? commit = null) => new(true, commit, DiagnosticCodes.None);

    public static GitOperationResult Fail(string diagnosticCode) => new(false, null, diagnosticCode);
}

/// <summary>
/// Low-level, real-Git worktree operations. Every method targets a linked worktree by its absolute
/// path; <c>projectRoot</c> is the main repository every worktree links back to. No method leaves
/// the main repository itself checked out to a different branch or dirty — all mutation happens
/// inside a linked worktree. Higher-level policy (which path/branch to use, when to create vs.
/// reuse, ownership, the integration barrier, gated rebase) lives in
/// <c>SprintGitIsolation</c>; this interface is intentionally as close to the `git` CLI surface as
/// a typed contract can be.
/// </summary>
public interface IWorktreeManager
{
    /// <summary>True if a worktree is already registered at <paramref name="path"/>.</summary>
    Task<bool> ExistsAsync(string projectRoot, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new linked worktree at <paramref name="path"/>, checked out on a fresh
    /// <paramref name="branch"/> created at <paramref name="commit"/>. Fails closed
    /// (<c>worktree_create_failed</c>) if the path or branch is already in use — this never reuses
    /// or resets an existing one; a caller that wants idempotent "ensure" semantics checks
    /// <see cref="ExistsAsync"/> first.
    /// </summary>
    Task<GitOperationResult> CreateAsync(
        string projectRoot,
        string path,
        string branch,
        string commit,
        CancellationToken cancellationToken);

    /// <summary>True if <paramref name="path"/>'s working tree has any uncommitted (tracked or
    /// untracked) change.</summary>
    Task<bool> IsDirtyAsync(string projectRoot, string path, CancellationToken cancellationToken);

    /// <summary>Discards every uncommitted change and untracked file in <paramref name="path"/>,
    /// then resets it to <paramref name="commit"/> — the dirty-recovery primitive: never continues
    /// over an unknown diff.</summary>
    Task<GitOperationResult> ResetHardAsync(
        string projectRoot,
        string path,
        string commit,
        CancellationToken cancellationToken);

    Task<string> GetHeadAsync(string projectRoot, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Fast-forwards <paramref name="path"/>'s checked-out branch to <paramref name="sourceBranch"/>'s
    /// tip. Fails closed (<c>worktree_integration_diverged</c>, no merge commit, no conflict
    /// resolution) the moment history has diverged — the integration barrier's base check.
    /// </summary>
    Task<GitOperationResult> IntegrateFastForwardAsync(
        string projectRoot,
        string path,
        string sourceBranch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replays the commits unique to <paramref name="path"/>'s checked-out branch since
    /// <paramref name="upstream"/> onto <paramref name="ontoCommit"/>. Aborts and fails closed
    /// (<c>worktree_rebase_conflict</c>) on the first conflict rather than leaving the worktree
    /// mid-rebase — a gated rebase never resolves a conflict on its own.
    /// </summary>
    Task<GitOperationResult> RebaseOntoAsync(
        string projectRoot,
        string path,
        string upstream,
        string ontoCommit,
        CancellationToken cancellationToken);

    /// <summary>Removes a linked worktree and its directory. Safe to call on a path that is not
    /// currently registered.</summary>
    Task RemoveAsync(string projectRoot, string path, CancellationToken cancellationToken);

    /// <summary>Best-effort branch deletion; a missing branch is not an error.</summary>
    Task DeleteBranchAsync(string projectRoot, string branch, CancellationToken cancellationToken);
}

public sealed record AppendOutcome(bool Succeeded, SprintWorkflowState? State, string DiagnosticCode, bool Replayed = false)
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
    /// Marks a sprint's creation as durably complete: until this is called, <see cref="ListAsync"/>
    /// will not return the sprint, even though every other write for it may already be durable and
    /// individually addressable by id. This lets sprint creation retry safely from a crash at any
    /// point without ever exposing a partially built sprint through enumeration.
    /// </summary>
    Task MarkSprintCreatedAsync(string projectRoot, SprintId id, CancellationToken cancellationToken);

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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? extraArguments = null);

    /// <summary>
    /// Persists a sprint's frozen definition once. Nothing in this store ever updates it again;
    /// callers must write it exactly once, before the sprint becomes visible to other operations.
    /// </summary>
    Task SaveDefinitionAsync(string projectRoot, SprintDefinition definition, CancellationToken cancellationToken);

    Task<SprintDefinition?> LoadDefinitionAsync(
        string projectRoot,
        SprintId id,
        CancellationToken cancellationToken);

    /// <summary>Node results, findings, and handoffs are small mutable/append records, not state
    /// machines — they need no event sourcing of their own.</summary>
    Task SaveNodeResultAsync(string projectRoot, NodeResult result, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeResult>> GetNodeResultsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);

    Task SaveFindingAsync(string projectRoot, Finding finding, CancellationToken cancellationToken);

    Task<IReadOnlyList<Finding>> GetFindingsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);

    Task SaveHandoffAsync(string projectRoot, Handoff handoff, CancellationToken cancellationToken);

    Task<IReadOnlyList<Handoff>> GetHandoffsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);
}

public interface IArtifactStore;

/// <summary>
/// Durable routing state for one sprint: a circuit breaker per <see cref="HealthKey"/>, one shared
/// retry budget, and an append-only, reproducible log of every routing decision made. Scoped per
/// sprint like every other durable record Stage 6 introduced — see <c>RoutingLedger</c>'s own
/// remarks for why cross-sprint sharing is deliberately out of scope for now.
/// </summary>
public interface IRoutingStore
{
    Task<CircuitBreakerRecord?> GetCircuitBreakerAsync(
        string projectRoot,
        SprintId sprintId,
        HealthKey key,
        CancellationToken cancellationToken);

    Task SaveCircuitBreakerAsync(
        string projectRoot,
        SprintId sprintId,
        CircuitBreakerRecord record,
        CancellationToken cancellationToken);

    Task<RetryBudgetRecord?> GetRetryBudgetAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);

    Task SaveRetryBudgetAsync(
        string projectRoot,
        SprintId sprintId,
        RetryBudgetRecord record,
        CancellationToken cancellationToken);

    Task AppendRouteDecisionAsync(string projectRoot, RouteDecision decision, CancellationToken cancellationToken);

    Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);
}

public interface ISafeLogger
{
    void Information(string eventName, IReadOnlyDictionary<string, object?> properties);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
