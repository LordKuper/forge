using System.Diagnostics;
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

/// <summary>
/// <paramref name="StandardInput"/>, when non-null, is written to the child's stdin and the pipe
/// then closed — ADR 0006: "Forge sends prompts through redirected standard input, never a
/// command-line argument." <paramref name="ReplaceEnvironment"/> selects between this request's two
/// legal environment shapes: <see langword="false"/> (the default, used by every pre-Stage-11
/// caller — git plumbing, provider install/version/auth probes) merges <see cref="EnvironmentVariables"/>
/// onto the full inherited process environment; <see langword="true"/> (ADR 0006: "Provider children
/// receive a minimal environment assembled by Forge") starts from nothing and uses
/// <see cref="EnvironmentVariables"/> as the complete child environment.
/// </summary>
public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? StandardInput = null,
    bool ReplaceEnvironment = false);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Notified as a running child's stdout/stderr lines arrive, not after it exits — ADR 0006:
/// "Stdout and stderr are consumed concurrently as bounded streams." A caller with no interest in
/// incremental delivery passes no sink at all (<see cref="IProcessRunner"/>'s two-argument
/// overload); <see cref="ProcessResult"/> still carries the complete joined output either way, so a
/// sink is purely an additional, non-exclusive delivery path.
/// </summary>
public interface IProcessOutputSink
{
    Task OnStandardOutputLineAsync(string line, CancellationToken cancellationToken);

    Task OnStandardErrorLineAsync(string line, CancellationToken cancellationToken);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken);

    /// <summary>Every call site before Stage 11 P11.32-P11.40 — no incremental delivery, matching
    /// this method's original, still-unchanged behavior exactly.</summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
        RunAsync(request, null, cancellationToken);
}

public interface INetworkClient
{
    Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// Plan section 12.4's process-containment port: ties a just-started child process's lifetime to
/// the current process, on whichever platforms the current adapter can actually offer that, so an
/// abrupt death of the current process (crash, `kill -9`, `taskkill /F`) does not necessarily leave
/// the child -- and whatever descendants it has spawned -- running forever as an orphan. This port
/// promises only "the strongest containment the installed adapter can offer, applied consistently
/// to every process <see cref="IProcessRunner"/> spawns"; it never promises every platform behaves
/// identically. On Windows, <c>Forge.Runtime.Windows.WindowsJobObjectProcessContainment</c> gives a
/// real kill-on-parent-death guarantee via a Windows Job Object. Linux and macOS have no containment
/// adapter at all today (a known limitation, not a lesser guarantee): a POSIX process group was
/// investigated and found unusable for this purpose -- `setpgid` can only take effect from inside the
/// child between `fork` and `exec`, a window `System.Diagnostics.Process.Start` gives no hook to
/// reach, and even a successful group change carries no OS-level kill-on-parent-death semantic
/// without a separate reaper process this codebase does not have -- so <see
/// cref="Infrastructure.NullProcessContainment"/>, the no-op every composition root gets until it
/// installs an adapter, is also what Linux/macOS composition roots run with permanently today.
/// </summary>
public interface IProcessContainment
{
    /// <summary>
    /// Attaches containment to <paramref name="process"/>, which must already be started. Returns a
    /// handle the caller disposes once <paramref name="process"/> is known to have exited (normally
    /// or via a kill the caller itself issued) -- disposing releases whatever OS resource backs the
    /// containment; it never itself terminates a child that, by the time a caller disposes, has
    /// already exited.
    /// </summary>
    IDisposable Attach(Process process);
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

    /// <summary>Resolves the branch currently checked out in <paramref name="projectRoot"/> itself
    /// (`git symbolic-ref --short HEAD`) — a new sprint freezes this as
    /// <see cref="SprintDefinition.DefaultBranch"/>, alongside its base commit.
    /// <see langword="null"/> for a detached `HEAD`, which has no branch name to freeze.</summary>
    Task<string?> GetCurrentBranchAsync(string projectRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Finalization's own merge primitive (ADR 0036) — the first operation in this codebase that
    /// ever mutates <paramref name="projectRoot"/>'s own checked-out content rather than an isolated
    /// worktree's. Deliberately as narrow as <see cref="IWorktreeManager.IntegrateFastForwardAsync"/>'s
    /// own fast-forward-only philosophy, extended with two guards that primitive never needed
    /// (a worktree is always created fresh at a known commit; the main checkout is not): refuses with
    /// <see cref="DiagnosticCodes.RepositoryDirty"/> if <paramref name="projectRoot"/> has
    /// uncommitted changes, and with <see cref="DiagnosticCodes.RepositoryBranchMismatch"/> if the
    /// branch currently checked out there is not <paramref name="defaultBranch"/> — this method
    /// never runs `git checkout` itself, so the project's own working directory never changes which
    /// branch it is on because Forge ran. Only then does it attempt
    /// `git merge --ff-only -- &lt;sourceBranch&gt;`, failing closed with
    /// <see cref="DiagnosticCodes.WorktreeIntegrationDiverged"/> — the same diagnostic
    /// <see cref="IWorktreeManager.IntegrateFastForwardAsync"/> already uses for the identical
    /// failure shape — on any divergence, never a real three-way merge or automatic conflict
    /// resolution.
    /// </summary>
    Task<GitOperationResult> MergeSprintIntoDefaultBranchAsync(
        string projectRoot, string defaultBranch, string sourceBranch, CancellationToken cancellationToken);
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
/// <paramref name="Diff"/> is <see langword="null"/> exactly when <paramref name="Succeeded"/> is
/// <see langword="false"/>. <paramref name="Truncated"/> is meaningful only on success: the diff
/// itself was cut to a bound (<see cref="GitWorktreeManagerDiffBudget.MaxCharacters"/>) rather than
/// a failure — a large, real diff degrades by truncation, not by failure, matching every other
/// context-assembly budget in this codebase (ADR 0012).
/// </summary>
public sealed record GitDiffResult(
    bool Succeeded, string? Diff, bool Truncated, string DiagnosticCode, string? Detail = null)
{
    public static GitDiffResult Ok(string diff, bool truncated) => new(true, diff, truncated, DiagnosticCodes.None);

    public static GitDiffResult Fail(string diagnosticCode, string? detail = null) =>
        new(false, null, false, diagnosticCode, Detail: Truncate(detail));

    private static string? Truncate(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Length <= 500 ? text.Trim() : string.Concat(text.AsSpan(0, 500), "…");
}

/// <summary>The single, MVP-fixed bound <see cref="IWorktreeManager.DiffAsync"/> truncates a raw
/// `git diff` to before it ever reaches a prompt — an unverified guess (matching
/// <c>IntakeExecutionHostedService.DefaultTokenBudget</c>'s own precedent for a frozen fallback
/// with no configuration source), sized to leave real room for a review prompt's other sections
/// (the handoff, rules, knowledge) under the same context-manifest token budget they already
/// share.</summary>
public static class GitWorktreeManagerDiffBudget
{
    public const int MaxCharacters = 50_000;
}

/// <summary>ADR 0059's own bound, deliberately separate from
/// <see cref="GitWorktreeManagerDiffBudget"/>: that one sizes a raw diff for a *prompt*, this one
/// bounds how many per-file rows a single durable journal line may carry. Sized to stay well inside
/// <see cref="Forge.Application.SprintTimelineProjector.MaxItemsPerPage"/>-scale rendering while
/// still covering the overwhelming majority of real single-attempt changes; anything beyond it is
/// reported as <see cref="Forge.Domain.DiffPayload.ElidedFiles"/> rather than dropped silently. Must
/// equal `payload.diff.files`'s own `maxItems` in docs/contracts/v1/schemas/event.schema.json --
/// raising one without the other would make every diff record fail its own schema validation, which
/// the audit-only write path catches and logs rather than surfaces, so the two are pinned together
/// by a contract test (`TheEventSchemasDiffFileCapMatchesTheBoundTheProducerActuallyApplies`)
/// instead of by hand.</summary>
public static class GitWorktreeManagerDiffStatBudget
{
    public const int MaxFiles = 50;
}

/// <summary>ADR 0060's counterpart to <see cref="GitWorktreeManagerDiffStatBudget"/>, and sized the
/// same way and for the same reason: it bounds how many per-call rows a single durable journal line
/// may carry, while the totals in <see cref="Forge.Domain.ToolUsePayload"/> still cover every observed
/// call and the remainder is reported as <see cref="Forge.Domain.ToolUsePayload.ElidedCalls"/> rather
/// than dropped silently. Must equal `payload.tool_use.calls`'s own `maxItems` in
/// docs/contracts/v1/schemas/event.schema.json -- raising one without the other would make every
/// tool-use record fail its own schema validation, which the audit-only write path catches and logs
/// rather than surfaces, so the two are pinned together by a contract test
/// (`TheEventSchemasToolCallCapMatchesTheBoundTheProducerActuallyApplies`) instead of by hand.</summary>
public static class ProviderToolUseBudget
{
    public const int MaxCalls = 50;
}

/// <summary>The structural counterpart to <see cref="GitDiffResult"/>: `git diff --numstat` parsed
/// into per-file statistics, never diff hunk content. <paramref name="Stat"/> is
/// <see langword="null"/> exactly when <paramref name="Succeeded"/> is
/// <see langword="false"/>.</summary>
public sealed record GitDiffStatResult(bool Succeeded, DiffPayload? Stat, string DiagnosticCode, string? Detail = null)
{
    public static GitDiffStatResult Ok(DiffPayload stat) => new(true, stat, DiagnosticCodes.None);

    public static GitDiffStatResult Fail(string diagnosticCode, string? detail = null) =>
        new(false, null, diagnosticCode, Truncate(detail));

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

    /// <summary>
    /// Stages every tracked and untracked change in <paramref name="path"/> (`git add -A`) and
    /// commits it with <paramref name="message"/>, authored and committed as Forge itself — never
    /// the ambient `user.name`/`user.email` the project's own repository config happens (or fails)
    /// to have configured, so this succeeds identically in a project that never set a git identity
    /// at all. Fails closed (<c>worktree_commit_failed</c>) on any `git` error, including calling
    /// this on an already-clean working tree ("nothing to commit") — a caller that means to no-op on
    /// a clean tree checks <see cref="IsDirtyAsync"/> first rather than relying on this to do so.
    /// Never partial: a failed `commit` after a successful `add` leaves the index staged but no new
    /// commit created, exactly like an ordinary failed `git commit` would.
    /// </summary>
    Task<GitOperationResult> CommitAllAsync(
        string projectRoot, string path, string message, CancellationToken cancellationToken);

    /// <summary>
    /// The read-only counterpart to <see cref="CommitAllAsync"/>: `git diff` between two already-
    /// resolved commits (never a ref, matching every other commit-shaped argument this interface
    /// takes), never the working tree at <paramref name="path"/> itself — reading history, not the
    /// checkout's own state. <paramref name="path"/> only needs to be a worktree of the same
    /// repository; git's object store is shared, so the diff does not depend on what that worktree
    /// happens to have checked out.
    /// </summary>
    Task<GitDiffResult> DiffAsync(
        string projectRoot, string path, string fromCommit, string toCommit, CancellationToken cancellationToken);

    /// <summary>
    /// ADR 0059: the same two-commit range as <see cref="DiffAsync"/>, read as per-file statistics
    /// (`git diff --numstat` plus `git diff --name-status`) instead of diff text — the only diff
    /// shape Forge persists. Paths come back exactly as git reports them: repository-root-relative
    /// and forward-slashed on every OS. A binary file (`-`/`-` in `--numstat`) is reported as
    /// <see cref="Forge.Domain.DiffChangeKinds.Binary"/> with zero counts. At most
    /// <see cref="GitWorktreeManagerDiffStatBudget.MaxFiles"/> per-file rows are returned; the
    /// remainder is counted in <see cref="Forge.Domain.DiffPayload.ElidedFiles"/> while
    /// <see cref="Forge.Domain.DiffPayload.FilesChanged"/> and the insertion/deletion totals still
    /// cover every changed file.
    /// </summary>
    Task<GitDiffStatResult> DiffStatAsync(
        string projectRoot, string path, string fromCommit, string toCommit, CancellationToken cancellationToken);

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

    /// <summary>Every worktree `git` currently has registered for <paramref name="projectRoot"/> —
    /// the enumeration counterpart to <see cref="ExistsAsync"/>'s single-path check, for `forge
    /// doctor --bundle` (ADR 0005/0038). <see cref="WorktreeRegistration.Exists"/> applies the same
    /// distinction <see cref="ExistsAsync"/>'s own doc comment names: `git` keeps a registration
    /// around after its directory is deleted out from under it until the next prune, so a caller
    /// diagnosing orphaned state needs both facts, not just the registration. `git worktree list`
    /// always includes the primary worktree (<paramref name="projectRoot"/> itself) first — this
    /// returns that entry too, unfiltered, matching git's own reality exactly rather than guessing
    /// at which entries a caller considers "Forge's own."</summary>
    Task<IReadOnlyList<WorktreeRegistration>> ListAsync(string projectRoot, CancellationToken cancellationToken);
}

/// <summary><paramref name="Path"/> is one worktree `git` has registered for a repository;
/// <paramref name="Exists"/> is whether that path is still a real, present directory.</summary>
public sealed record WorktreeRegistration(string Path, bool Exists);

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

    Task SaveTestWorkAsync(string projectRoot, TestWorkArtifact testWork, CancellationToken cancellationToken);

    Task<IReadOnlyList<TestWorkArtifact>> GetTestWorkAsync(
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
    /// it never competes with or blocks a real transition. <paramref name="kind"/> defaults to
    /// <see cref="AttemptActivityKind.Heartbeat"/>, matching every activity event recorded before
    /// Stage 11 P11.32-P11.40 introduced typed activity.</summary>
    Task AppendAttemptActivityAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken,
        AttemptActivityKind kind = AttemptActivityKind.Heartbeat);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptSupersededType"/> event carrying the
    /// operator's bounded <paramref name="instruction"/> (ADR 0006, Stage 11 P11.48-P11.55) — like
    /// <see cref="AppendAttemptActivityAsync"/>, not gated by <see cref="AppendTransitionAsync"/>'s
    /// optimistic concurrency, since the caller (<c>SprintScheduler.SupersedeAttemptAsync</c>)
    /// already validated the attempt's version and idempotency key before appending its own real
    /// `cancelled` transition; this only augments that already-committed record.</summary>
    Task AppendAttemptSupersededAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        string instruction,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.UserMessagePostedType"/> event carrying the
    /// operator's bounded <paramref name="text"/> (post-release timeline gap closure, ADR 0054) --
    /// like <see cref="AppendAttemptSupersededAsync"/>, not gated by <see cref="AppendTransitionAsync"/>'s
    /// optimistic concurrency, since a message post never conflicts with concurrent workflow
    /// progress. Deduplicated by <paramref name="messageId"/> (the event's own caller-supplied
    /// <see cref="WorkflowEvent.EventId"/>) rather than a version/idempotency-key pair -- a second
    /// call with the same id is always a replay and returns without appending again.</summary>
    Task AppendUserMessageAsync(
        string projectRoot,
        SprintId sprintId,
        Guid messageId,
        string text,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AgentSummaryRecordedType"/> event carrying
    /// <paramref name="summaryText"/> (<see cref="Handoff.Summary"/>) on the producing node's own
    /// aggregate -- ADR 0054, redesigned (PR #104 review, finding 1): replaces the original design's
    /// borrowed <c>Handoff.Sequence</c> anchor with a real journal entry of its own, the same "give it
    /// a real, dense <see cref="WorkflowEvent.Sequence"/>" shape <see cref="AppendUserMessageAsync"/>
    /// already uses. <paramref name="handoffId"/> becomes the event's own
    /// <see cref="WorkflowEvent.CorrelationId"/> -- both the dedup key (a second call for the same
    /// handoff is always a replay, mirroring <see cref="AppendAttemptSupersededAsync"/>'s "recorded
    /// once" idiom) and how <c>SprintTimelineProjector.MergeAndPage</c> later matches this immutable
    /// event back to its owning (and possibly since-superseded) <see cref="Handoff"/>.</summary>
    Task AppendAgentSummaryRecordedAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        Guid handoffId,
        string summaryText,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptDiffRecordedType"/> event carrying
    /// <paramref name="diff"/> on <paramref name="attemptId"/>'s own aggregate (ADR 0059) -- like
    /// <see cref="AppendAttemptSupersededAsync"/>, not gated by <see cref="AppendTransitionAsync"/>'s
    /// optimistic concurrency: a diff record augments an outcome the caller has already committed to
    /// git and never competes with concurrent workflow progress. Recorded at most once per attempt
    /// (an attempt produces exactly one commit), deduplicated by scanning the journal for this event
    /// type on this attempt rather than by a caller-supplied idempotency key -- a second call is
    /// always a replay of the same already-landed commit. The event's flat
    /// <see cref="WorkflowEvent.Arguments"/> summary (<see cref="WorkflowEvent.DiffFilesChangedArgument"/>
    /// and friends, which is what the localized timeline template substitutes) is derived here from
    /// <paramref name="diff"/> itself, never supplied separately, so the rendered summary and the
    /// structured payload cannot drift apart.</summary>
    Task AppendAttemptDiffRecordedAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        DiffPayload diff,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptToolUseRecordedType"/> event carrying
    /// <paramref name="toolUse"/> on <paramref name="attemptId"/>'s own aggregate (ADR 0060) -- the
    /// exact contract <see cref="AppendAttemptDiffRecordedAsync"/> already documents, applied to the
    /// second payload family: not gated by optimistic concurrency, recorded at most once per attempt
    /// (deduplicated by scanning the journal for this event type on this attempt, since a second call
    /// is always a replay of the same already-finished provider run), and with the event's flat
    /// <see cref="WorkflowEvent.Arguments"/> summary (<see cref="WorkflowEvent.ToolCallsArgument"/>
    /// and friends) derived here from <paramref name="toolUse"/> itself rather than supplied
    /// separately, so the rendered summary and the structured payload cannot drift apart.</summary>
    Task AppendAttemptToolUseRecordedAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        ToolUsePayload toolUse,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptUsageRecordedType"/> event carrying
    /// <paramref name="usage"/> on <paramref name="attemptId"/>'s own aggregate (ADR 0061) -- the exact
    /// contract <see cref="AppendAttemptDiffRecordedAsync"/> already documents, applied to the third
    /// payload family: not gated by optimistic concurrency, recorded at most once per attempt
    /// (deduplicated by scanning the journal for this event type on this attempt, since an attempt runs
    /// its provider exactly once and a second call is always a replay of the same finished run), and
    /// with the event's flat <see cref="WorkflowEvent.Arguments"/> summary
    /// (<see cref="WorkflowEvent.UsageTotalTokensArgument"/> and friends) derived here from
    /// <paramref name="usage"/> itself rather than supplied separately, so the rendered summary and the
    /// structured payload cannot drift apart.</summary>
    Task AppendAttemptUsageRecordedAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        UsagePayload usage,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptStopRequestedType"/> event for
    /// <paramref name="attemptId"/> (plan section 7.3's durable stop intent) -- recorded once per
    /// attempt, like <see cref="AppendAttemptSupersededAsync"/>: a second call for the same attempt
    /// is always a replay, deduplicated by scanning the journal rather than by a caller-supplied
    /// idempotency key, and returns a replayed <see cref="AppendOutcome"/> rather than appending
    /// again. A fresh (non-replayed) append is still gated on <paramref name="expectedAttemptVersion"/>
    /// matching the attempt's current version, exactly like <see cref="AppendTransitionAsync"/>'s own
    /// optimistic concurrency: the stop coordinator
    /// (<c>Forge.Application.StopOperationCoordinator.RequestStopAsync</c>) validates the attempt is
    /// the sprint's exact active operation before calling this, but does not hold any lock across
    /// that read and this call, so a concurrent compound operation
    /// (<c>SprintScheduler.CompleteAttemptAsync</c>/<c>SupersedeAttemptAsync</c>) that moves the
    /// attempt off being current in that window must be detected here, inside this store's own
    /// per-sprint critical section, rather than let the intent silently attach to a now-stale
    /// attempt. Returns <see cref="AppendOutcome.Conflict"/> on a version mismatch.</summary>
    Task<AppendOutcome> AppendAttemptStopRequestedAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        long expectedAttemptVersion,
        CancellationToken cancellationToken);

    /// <summary>Appends one <see cref="WorkflowEvent.AttemptStopConvergedType"/> event for
    /// <paramref name="attemptId"/> -- <see cref="Forge.Application.StopOperationCoordinator.FinishStopAsync"/>'s
    /// own last, unconditional step, marking the whole stop saga durably done. Recorded once per
    /// attempt, deduplicated by scanning the journal like <see cref="AppendAttemptStopRequestedAsync"/>;
    /// not gated by <see cref="AppendTransitionAsync"/>'s optimistic concurrency, since it is only
    /// ever appended after <c>FinishStopAsync</c>'s own attempt-cancellation step already landed (or
    /// was already a no-op because it had landed earlier).</summary>
    Task AppendAttemptStopConvergedAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A pure, side-effect-free check for whether <paramref name="idempotencyKey"/>'s whole
    /// `MoveSprintToStage` saga has already *fully converged* (<see cref="AppendStageTransitionConvergedAsync"/>
    /// already landed for it) -- returns the current folded state when it has, or <see langword="null"/>
    /// otherwise. Round 1 review of PR #96 (finding 1): this deliberately does NOT check the raw
    /// <see cref="AppendTransitionAsync"/>/<see cref="AppendStageRevisionRecordedAsync"/> idempotency
    /// ledger the way the original (defective) design did -- that ledger entry lands at step 2 of the
    /// six-step rewind saga, before evidence supersession, node reopen/invalidate, graph re-advance,
    /// and the sprint-ready walk (steps 3-6) have run, so a crash in that window made every future
    /// replay of the same key report success on a permanently half-finished rewind. Checked first,
    /// unconditionally, before any fresh assessment: by the time a rewind has already committed once,
    /// current state has moved on (the target is no longer at the terminal outcome that made it a
    /// rewind in the first place), so re-deriving direction from scratch on a replay would no longer
    /// even classify the call as the same operation. See <c>StageTransitionCoordinator.MoveAsync</c>.
    /// </summary>
    Task<SprintWorkflowState?> TryGetConvergedStageTransitionAsync(
        string projectRoot, SprintId sprintId, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one <see cref="WorkflowEvent.StageTransitionConvergedType"/> event carrying
    /// <paramref name="idempotencyKey"/> -- <c>StageTransitionCoordinator.MoveAsync</c>'s own last,
    /// unconditional step on a successful advance or rewind commit, marking the whole saga durably
    /// done (round 1 review of PR #96, findings 1 and 4). Recorded once per key, deduplicated by
    /// scanning the journal like <see cref="AppendAttemptStopConvergedAsync"/>; not gated by
    /// <see cref="AppendTransitionAsync"/>'s optimistic concurrency, since it is only ever appended
    /// after the commit's own steps already landed (or were already a no-op because an earlier call
    /// landed them). Use <see cref="TryGetConvergedStageTransitionAsync"/> to check for a replay
    /// without appending anything.
    /// </summary>
    Task AppendStageTransitionConvergedAsync(
        string projectRoot, SprintId sprintId, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Plan section 8.4/8.5's committed-rewind marker (Slice 3): appends one
    /// <see cref="WorkflowEvent.StageRevisionRecordedType"/> event on the sprint's own aggregate,
    /// gated on <paramref name="expectedSprintVersion"/> matching the sprint's current version
    /// (exactly like <see cref="AppendTransitionAsync"/>'s own optimistic concurrency) and
    /// deduplicated through the *same* durable idempotency-key ledger
    /// <see cref="AppendTransitionAsync"/> already maintains -- reused deliberately, not a second
    /// mechanism, so replaying <paramref name="idempotencyKey"/> returns the original result and
    /// never records a second revision, the same "durable marker checked before acting" discipline
    /// <see cref="AppendAttemptStopRequestedAsync"/> uses for its own (differently-scoped) replay
    /// detection. Unlike that method, a sprint can be legitimately rewound more than once over its
    /// life, so deduplication here cannot be "has this event type ever landed for this aggregate" --
    /// it must be keyed by the caller's own idempotency key, exactly like an ordinary
    /// <see cref="AppendTransitionAsync"/> call. This ledger entry alone is NOT a safe outer
    /// replay-succeeded signal (round 1 review of PR #96, finding 1) -- see
    /// <see cref="TryGetConvergedStageTransitionAsync"/>/<see cref="AppendStageTransitionConvergedAsync"/>
    /// for the dedicated saga-completion marker that is.
    /// </summary>
    Task<AppendOutcome> AppendStageRevisionRecordedAsync(
        string projectRoot,
        SprintId sprintId,
        string targetStageId,
        string reason,
        StageRevision newRevision,
        long expectedSprintVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Marks one already-recorded <see cref="NodeResult"/> (identified by its owning
    /// attempt) as superseded, if it is not already -- idempotent and safe to call repeatedly, the
    /// same discipline every other rewind-coordinator step uses so a Host crash mid-supersession
    /// converges on retry instead of duplicating or skipping a marker. A no-op if no result is
    /// recorded for <paramref name="attemptId"/> at all.</summary>
    Task MarkNodeResultSupersededAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        SupersededBy marker,
        CancellationToken cancellationToken);

    /// <summary>Same idempotent-mark discipline as <see cref="MarkNodeResultSupersededAsync"/>, for
    /// one recorded <see cref="Handoff"/>.</summary>
    Task MarkHandoffSupersededAsync(
        string projectRoot,
        SprintId sprintId,
        Guid handoffId,
        SupersededBy marker,
        CancellationToken cancellationToken);

    /// <summary>Same idempotent-mark discipline as <see cref="MarkNodeResultSupersededAsync"/>, for
    /// one recorded <see cref="ConfirmationArtifact"/>.</summary>
    Task MarkConfirmationSupersededAsync(
        string projectRoot,
        SprintId sprintId,
        Guid confirmationId,
        SupersededBy marker,
        CancellationToken cancellationToken);

    /// <summary>Same idempotent-mark discipline as <see cref="MarkNodeResultSupersededAsync"/>, for
    /// one recorded <see cref="TestWorkArtifact"/>.</summary>
    Task MarkTestWorkSupersededAsync(
        string projectRoot,
        SprintId sprintId,
        Guid testWorkId,
        SupersededBy marker,
        CancellationToken cancellationToken);

    /// <summary>Same idempotent-mark discipline as <see cref="MarkNodeResultSupersededAsync"/>, for
    /// one recorded <see cref="Finding"/>.</summary>
    Task MarkFindingSupersededAsync(
        string projectRoot,
        SprintId sprintId,
        Guid findingId,
        SupersededBy marker,
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

/// <summary>A redacted, persisted operational log distinct from any console/`ILogger` output — see
/// <see cref="Infrastructure.SafeLogger"/> for the destination and redaction guarantee.</summary>
public interface ISafeLogger
{
    ValueTask InformationAsync(
        string eventName,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
