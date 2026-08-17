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
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

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

    /// <summary>
    /// Namespaces every Forge-owned path under <see cref="LocalApplicationData"/> (user
    /// configuration, worktrees, and — once they exist — logs/caches) so release, Debug, and test
    /// processes never collide on the same files (ADR 0005: "Instance identity namespaces IPC
    /// endpoints, user configuration, logs, caches, and worktrees"). The project lease
    /// deliberately stays outside this namespace (see <c>InstanceIdentity.ComputeLeaseName</c> in
    /// <c>Forge.Host.Client</c>) so it still prevents two instances from mutating the same project
    /// concurrently.
    /// </summary>
    string InstanceId { get; }
}

public interface IRepository
{
    /// <summary>Resolves the current commit a new sprint would freeze as its immutable base.</summary>
    Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken);
}

/// <summary>
/// <paramref name="CleanupSucceeded"/> is independent of <paramref name="Succeeded"/>: it only ever
/// turns <see langword="false"/> when the operation itself already succeeded but a best-effort
/// cleanup step afterward (removing an already-merged attempt's worktree/branch) did not — never
/// conflated with the operation's own outcome, since a leaked worktree is a resource to reconcile
/// later, not a reason to report the operation as failed. <paramref name="Detail"/> carries the
/// failing `git` invocation's own `stderr` (truncated), present only on a failed
/// <paramref name="DiagnosticCode"/> — `git`'s own diagnostic text about its worktree/branch/ref
/// state is not sensitive, and without it a caller (or a CI log) has no way to tell *why* `git`
/// refused beyond a fixed code shared by several distinct real causes.
/// </summary>
public sealed record GitOperationResult(
    bool Succeeded, string? Commit, string DiagnosticCode, bool CleanupSucceeded = true, string? Detail = null)
{
    public static GitOperationResult Ok(string? commit = null) => new(true, commit, DiagnosticCodes.None);

    public static GitOperationResult Fail(string diagnosticCode) => new(false, null, diagnosticCode);

    public static GitOperationResult Fail(string diagnosticCode, string? detail) =>
        new(false, null, diagnosticCode, Detail: Truncate(detail));

    private static string? Truncate(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Length <= 500 ? text.Trim() : string.Concat(text.AsSpan(0, 500), "…");
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
    /// <summary>True only if a worktree is both registered at <paramref name="path"/> *and* that
    /// path is still a real, present directory — `git` itself keeps a registration around after its
    /// directory is deleted out from under it until the next prune, and that alone must never read
    /// as "exists" to a caller.</summary>
    Task<bool> ExistsAsync(string projectRoot, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new linked worktree at <paramref name="path"/>, checked out on a fresh
    /// <paramref name="branch"/> created at <paramref name="commit"/>. If that path/branch is
    /// already in use because a prior worktree's directory went missing (see
    /// <see cref="ExistsAsync"/>), this self-heals: a stale path registration is pruned, and if
    /// <paramref name="branch"/> itself still exists — carrying real, otherwise-unreachable history
    /// — a new worktree is re-attached to that *existing* branch instead of creating a fresh one, in
    /// which case the returned <see cref="GitOperationResult.Commit"/> is that branch's actual tip,
    /// not necessarily <paramref name="commit"/>. This never force-deletes a branch to make room —
    /// only an explicit, caller-driven decision may ever discard branch history. Fails closed
    /// (<c>worktree_create_failed</c>) only once neither a fresh nor an existing branch explains the
    /// failure; a caller that wants idempotent "ensure" semantics for an *already-live* worktree
    /// still checks <see cref="ExistsAsync"/> first rather than relying on this self-heal.
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
    /// currently registered. Returns <see langword="false"/> instead of throwing when `git` refuses
    /// (e.g. an open file handle on Windows) — a caller must be able to tell a leaked worktree apart
    /// from a clean removal.</summary>
    Task<bool> RemoveAsync(string projectRoot, string path, CancellationToken cancellationToken);

    /// <summary>Best-effort branch deletion; a missing branch is not an error. Returns
    /// <see langword="false"/> if `git` refused to delete a branch that still exists.</summary>
    Task<bool> DeleteBranchAsync(string projectRoot, string branch, CancellationToken cancellationToken);
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

    Task SaveConfirmationAsync(string projectRoot, ConfirmationArtifact confirmation, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfirmationArtifact>> GetConfirmationsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);

    Task SaveReviewIterationAsync(string projectRoot, ReviewIterationRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewIterationRecord>> GetReviewIterationsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);

    /// <summary>Whether an operator has already chosen to continue at the critical severity floor
    /// for this (sprint, node, dimension) — see <c>SprintScheduler.PinReviewFloorAsync</c>.
    /// ADR 0006: "User-approved continuation keeps the counter and pins the floor at critical; it
    /// never resets or re-admits lower severities." A plain marker, not a versioned record: the
    /// decision is a one-way pin, never revoked.</summary>
    Task SetReviewFloorPinnedAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        ReviewDimension dimension,
        CancellationToken cancellationToken);

    Task<bool> IsReviewFloorPinnedAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        ReviewDimension dimension,
        CancellationToken cancellationToken);

    Task AppendRouteDecisionAsync(string projectRoot, RouteDecision decision, CancellationToken cancellationToken);

    Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptActivityRecordedType"/> heartbeat for
    /// <paramref name="attemptId"/>. Not gated by <see cref="AppendTransitionAsync"/>'s optimistic
    /// concurrency or state-machine legality — a caller repeats this freely while the attempt runs;
    /// it never competes with or blocks a real transition.</summary>
    Task AppendAttemptActivityAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every durable transition and routing record for one sprint, in append order, including the
    /// records <see cref="LoadAsync"/> folds away — the raw stream a read model needs to derive
    /// creation order (its first record's <see cref="WorkflowEvent.OccurredAt"/>) and incremental
    /// cursors (its own <see cref="WorkflowEvent.Sequence"/> numbers) without a second store
    /// abstraction. Matches every other read here: it re-reads the whole journal rather than
    /// maintaining a partial-read index, which is fine at MVP sprint scale (see
    /// <see cref="FileSprintEventLog"/>'s own remarks).
    /// </summary>
    Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        string projectRoot,
        SprintId sprintId,
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
