using System.Collections.Concurrent;
using Forge.Compiler;
using Forge.Domain;
using Forge.Infrastructure;

namespace Forge.Application;

/// <summary>Deterministic filesystem/branch naming for every worktree Stage 7 creates. Worktrees
/// live under the user's local application data, keyed by project id — never inside the project's
/// own working tree — so a sprint's isolated mutation can never appear as untracked content in the
/// user's main checkout and needs no `.gitignore` coordination.</summary>
public static class WorktreeLayout
{
    public static string IntegrationBranch(SprintId sprintId) => $"forge/sprint/{ShortId(sprintId.Value)}";

    public static string AttemptBranch(AttemptId attemptId) => AttemptBranchFromShortId(ShortId(attemptId.Value));

    /// <summary>The exact branch name <see cref="AttemptBranch"/> would produce for the attempt id
    /// this <paramref name="shortId"/> was derived from — usable by a caller (crash recovery) that
    /// only has an attempt worktree's directory name, not its original full <see cref="AttemptId"/>,
    /// since that full id cannot be recovered from the (deliberately lossy) short form.</summary>
    internal static string AttemptBranchFromShortId(string shortId) => $"forge/attempt/{shortId}";

    public static string SprintRoot(IEnvironmentPaths paths, Guid projectId, SprintId sprintId) =>
        Path.Combine(
            paths.LocalApplicationData, "Forge", paths.InstanceId, "wt", ShortId(projectId),
            ShortId(sprintId.Value));

    public static string IntegrationPath(IEnvironmentPaths paths, Guid projectId, SprintId sprintId) =>
        Path.Combine(SprintRoot(paths, projectId, sprintId), "i");

    public static string AttemptsRoot(IEnvironmentPaths paths, Guid projectId, SprintId sprintId) =>
        Path.Combine(SprintRoot(paths, projectId, sprintId), "a");

    public static string AttemptPath(IEnvironmentPaths paths, Guid projectId, SprintId sprintId, AttemptId attemptId) =>
        Path.Combine(AttemptsRoot(paths, projectId, sprintId), ShortId(attemptId.Value));

    /// <summary>
    /// The first 16 hex characters of a v4 GUID — 60 bits of actual randomness once the fixed
    /// version nibble inside that prefix is excluded — astronomically collision-safe for a single
    /// user's local worktree cache, but far shorter than the full
    /// 32-character form. Worktree paths nest several directory levels below
    /// <c>%LOCALAPPDATA%</c>, and `git` itself nests further administrative files below *that*
    /// (`.git\worktrees\&lt;name&gt;\...`, used during a rebase); several full-length ids stacked
    /// across that whole depth measurably risks Windows path-length limits that vary by machine and
    /// were confirmed to fail in CI even with `core.longpaths` set (see the Stage 7 evidence in
    /// `docs/plans/implementation-plan.md`), so every id in this filesystem/branch layout is kept
    /// short unconditionally rather than relying on any one machine's path-length configuration.
    /// </summary>
    internal static string ShortId(Guid value) => value.ToString("N")[..16];
}

/// <summary>
/// Git-level isolation for sprint execution: one integration worktree per sprint and one throwaway
/// worktree per node write attempt, both branched under <see cref="WorktreeLayout"/>'s deterministic
/// paths so ownership never needs a separate durable map — an attempt's worktree path *is* its
/// ownership record, and the event-sourced attempt state Stage 6 already persists is enough to tell
/// a live attempt's worktree from an orphaned one on recovery (see <see cref="ReconcileAsync"/>).
/// Integration is a fast-forward-only merge behind a per-sprint barrier (<see cref="Locks"/>), so a
/// caller learns immediately, from <see cref="IntegrateAsync"/>'s own result, whether its attempt's
/// recorded base has gone stale rather than silently landing over an unknown diff. A stale base is
/// recovered only through the explicit <see cref="RebaseAttemptAsync"/> — never automatically.
/// </summary>
/// <remarks>
/// ponytail: no executor lives here. What a `Work` node's attempt actually does inside its worktree
/// (invoke a provider, commit its edits) is Stage 11's job; this class only creates, integrates,
/// rebases, and discards the worktrees such an executor will use, matching how
/// <c>SprintScheduler</c> was built ahead of its own executor in Stage 6.
/// </remarks>
public sealed class SprintGitIsolation(IWorktreeManager worktrees, ISprintStore store, IEnvironmentPaths paths)
{
    // ponytail: one entry per distinct sprint ever integrated into, never evicted — matches
    // `FileSprintEventLog.Locks`'s own documented tradeoff. Fine at MVP scale (a CLI process is
    // short-lived); add eviction (e.g. on sprint completion) if a long-lived host process ever
    // makes this measurable.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    /// <summary>Creates the sprint's integration worktree at its frozen base commit if it does not
    /// exist yet; otherwise recovers it from any uncommitted noise a crash mid-integration may have
    /// left behind (dirty recovery) without moving its committed history.</summary>
    public async Task<GitOperationResult> EnsureIntegrationWorktreeAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        string path = WorktreeLayout.IntegrationPath(paths, projectId, sprintId);
        if (!await worktrees.ExistsAsync(projectRoot, path, cancellationToken).ConfigureAwait(false))
        {
            return await worktrees.CreateAsync(
                projectRoot, path, WorktreeLayout.IntegrationBranch(sprintId), baseCommit, cancellationToken)
                .ConfigureAwait(false);
        }

        return await RecoverIfDirtyAsync(projectRoot, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a fresh worktree for exactly one node attempt, branched from the
    /// integration worktree's current tip. Idempotent only for a retried call with the *same*
    /// attempt id (a crash between this call landing and its caller observing success); a new
    /// attempt id — which is what every scheduler retry uses — always gets a brand-new worktree, so
    /// a failed attempt's content is never reused by its replacement (clean replay).</summary>
    public async Task<GitOperationResult> CreateAttemptWorktreeAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        string attemptPath = WorktreeLayout.AttemptPath(paths, projectId, sprintId, attemptId);
        if (await worktrees.ExistsAsync(projectRoot, attemptPath, cancellationToken).ConfigureAwait(false))
        {
            string? existingHead = await TryGetHeadAsync(projectRoot, attemptPath, cancellationToken)
                .ConfigureAwait(false);
            return existingHead is null
                ? GitOperationResult.Fail(DiagnosticCodes.WorktreeUnavailable)
                : GitOperationResult.Ok(existingHead);
        }

        string integrationPath = WorktreeLayout.IntegrationPath(paths, projectId, sprintId);
        string? baseCommit = await TryGetHeadAsync(projectRoot, integrationPath, cancellationToken)
            .ConfigureAwait(false);
        if (baseCommit is null)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeUnavailable);
        }

        return await worktrees.CreateAsync(
            projectRoot, attemptPath, WorktreeLayout.AttemptBranch(attemptId), baseCommit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Stages and commits every change in an attempt's own worktree, authored as Forge
    /// itself (<see cref="IWorktreeManager.CommitAllAsync"/>) — a thin path-resolution wrapper, the
    /// same shape as <see cref="CreateAttemptWorktreeAsync"/>. Whether committing an unmodified
    /// (clean) attempt worktree is meaningful is the caller's own policy decision (it is not, for
    /// a role whose whole job is producing an edit), not this class's: callers that care check
    /// <see cref="IWorktreeManager.IsDirtyAsync"/> themselves before ever reaching this method.
    /// </summary>
    public Task<GitOperationResult> CommitAttemptAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        string message,
        CancellationToken cancellationToken)
    {
        string attemptPath = WorktreeLayout.AttemptPath(paths, projectId, sprintId, attemptId);
        return worktrees.CommitAllAsync(projectRoot, attemptPath, message, cancellationToken);
    }

    /// <summary>Reads the diff an attempt's own worktree can see between two already-resolved
    /// commits — a thin path-resolution wrapper, the same shape as <see cref="CommitAttemptAsync"/>.
    /// A review-role attempt is this method's first intended caller, reading what changed between a
    /// sprint's frozen base and its current integration tip.</summary>
    public Task<GitDiffResult> ReadDiffAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        string fromCommit,
        string toCommit,
        CancellationToken cancellationToken)
    {
        string attemptPath = WorktreeLayout.AttemptPath(paths, projectId, sprintId, attemptId);
        return worktrees.DiffAsync(projectRoot, attemptPath, fromCommit, toCommit, cancellationToken);
    }

    /// <summary>
    /// ADR 0059: the structural counterpart to <see cref="ReadDiffAsync"/> — the same two-commit
    /// range read as per-file statistics, which is the only diff shape Forge ever persists. Applies
    /// the two safety rules a durable record needs and the raw git surface deliberately does not:
    /// every path must be syntactically safe and worktree-root-relative
    /// (<see cref="RelativePathShape.IsSyntacticallySafe"/>), and every retained path is then run
    /// through <see cref="SecretRedactor"/> anyway.
    /// </summary>
    /// <remarks>
    /// The path check is normalization, not redaction: git reports repository-root-relative,
    /// forward-slashed paths by construction, so an absolute, drive-prefixed, backslashed, or
    /// `..`-traversing entry cannot arise from a healthy repository at all and is dropped rather
    /// than rewritten — there is no safe interpretation of it to record. A dropped entry still
    /// counts toward <see cref="DiffPayload.FilesChanged"/> and the insertion/deletion totals, and is
    /// added to <see cref="DiffPayload.ElidedFiles"/>, so a reader is never told a change was smaller
    /// than it was.
    ///
    /// Redaction runs BEFORE any bounding (ADR 0057): a redaction placeholder can be longer than the
    /// text it replaces, so bounding first could leave a partial secret behind. Nothing here is
    /// length-bounded after redaction — the per-file cap is applied on entry count by
    /// <see cref="GitDiffStatParser"/> upstream, never by trimming a path — but the ordering is kept
    /// explicit so a future bound cannot be added on the wrong side of it. A credential-shaped file
    /// path is not expected in practice; this is the same belt-and-braces discipline every other
    /// durable free-text field already gets.
    /// </remarks>
    public async Task<GitDiffStatResult> ReadDiffStatAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        string fromCommit,
        string toCommit,
        CancellationToken cancellationToken)
    {
        string attemptPath = WorktreeLayout.AttemptPath(paths, projectId, sprintId, attemptId);
        GitDiffStatResult result = await worktrees
            .DiffStatAsync(projectRoot, attemptPath, fromCommit, toCommit, cancellationToken)
            .ConfigureAwait(false);
        return result.Stat is { } stat ? result with { Stat = Sanitize(stat) } : result;
    }

    private static DiffPayload Sanitize(DiffPayload stat)
    {
        List<DiffFileStat> safe = [];
        int dropped = 0;
        foreach (DiffFileStat file in stat.Files)
        {
            if (!RelativePathShape.IsSyntacticallySafe(file.Path))
            {
                dropped++;
                continue;
            }

            safe.Add(file with
            {
                Path = SecretRedactor.Redact(file.Path),
                ChangeKind = DiffChangeKinds.IsKnown(file.ChangeKind) ? file.ChangeKind : DiffChangeKinds.Modified,
            });
        }

        return stat with { Files = safe, ElidedFiles = stat.ElidedFiles + dropped };
    }

    /// <summary>
    /// Fast-forwards the sprint's integration branch to an attempt's branch tip, but only while the
    /// integration branch is still exactly at <paramref name="expectedIntegrationTip"/> — the base
    /// check. Serialized per sprint so two attempts finishing at once are integrated one at a time
    /// rather than racing. A base mismatch (something else integrated first) fails closed with
    /// <see cref="DiagnosticCodes.WorktreeBaseMismatch"/> and changes nothing; the caller must
    /// rebase (<see cref="RebaseAttemptAsync"/>) before retrying. On success the attempt's now-merged
    /// worktree and branch are discarded — nothing is ever left around to be reused by a later,
    /// unrelated attempt. The merge itself having succeeded is never conflated with that discard
    /// also succeeding: a failed discard is reported through the result's own
    /// <see cref="GitOperationResult.CleanupSucceeded"/>, not through <c>Succeeded</c> — the
    /// integration is real (the commit is on the integration branch) even if a leaked worktree
    /// needs a later reconciliation pass to clean up.
    /// </summary>
    public async Task<GitOperationResult> IntegrateAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        string expectedIntegrationTip,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetOrAdd(sprintId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string integrationPath = WorktreeLayout.IntegrationPath(paths, projectId, sprintId);
            string? actualTip = await TryGetHeadAsync(projectRoot, integrationPath, cancellationToken)
                .ConfigureAwait(false);
            if (actualTip is null)
            {
                return GitOperationResult.Fail(DiagnosticCodes.WorktreeUnavailable);
            }

            if (!string.Equals(actualTip, expectedIntegrationTip, StringComparison.OrdinalIgnoreCase))
            {
                return GitOperationResult.Fail(DiagnosticCodes.WorktreeBaseMismatch);
            }

            GitOperationResult merged = await worktrees.IntegrateFastForwardAsync(
                projectRoot, integrationPath, WorktreeLayout.AttemptBranch(attemptId), cancellationToken)
                .ConfigureAwait(false);
            if (!merged.Succeeded)
            {
                return merged;
            }

            bool cleanedUp = await DiscardAttemptAsync(projectRoot, projectId, sprintId, attemptId, cancellationToken)
                .ConfigureAwait(false);
            return merged with { CleanupSucceeded = cleanedUp };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The one explicit, gated way to recover an attempt whose base has gone stale: replays the
    /// attempt branch's own commits onto the integration branch's current tip. Never resolves a
    /// conflict — a conflicted rebase is aborted and fails closed with
    /// <see cref="DiagnosticCodes.WorktreeRebaseConflict"/>; the caller's only recovery from that is
    /// to discard the attempt and start a clean replay, never to continue over an unknown diff.
    /// </summary>
    public async Task<GitOperationResult> RebaseAttemptAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        string previousBase,
        CancellationToken cancellationToken)
    {
        string attemptPath = WorktreeLayout.AttemptPath(paths, projectId, sprintId, attemptId);
        string integrationPath = WorktreeLayout.IntegrationPath(paths, projectId, sprintId);
        string? newBase = await TryGetHeadAsync(projectRoot, integrationPath, cancellationToken)
            .ConfigureAwait(false);
        if (newBase is null)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeUnavailable);
        }

        return await worktrees.RebaseOntoAsync(
            projectRoot, attemptPath, previousBase, newBase, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="IWorktreeManager.GetHeadAsync"/> throws when it cannot resolve a commit — the
    /// right contract for a caller that already knows the worktree must exist, but every method
    /// here calls it on a worktree this class does not fully control the lifetime of (an external
    /// deletion, a not-yet-created attempt). Converts that failure into
    /// <see langword="null"/> so every caller above fails closed with an ordinary
    /// <see cref="GitOperationResult"/> instead of an uncaught exception.
    /// </summary>
    private async Task<string?> TryGetHeadAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await worktrees.GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Discards an attempt's worktree and branch outright — the failure path of clean
    /// replay: a failed attempt is never continued in place, only ever replaced by a fresh one.
    /// Returns <see langword="false"/> if either removal did not fully succeed (e.g. an open file
    /// handle on Windows refusing the worktree removal) so a caller can tell a leaked worktree apart
    /// from a clean discard instead of assuming success silently; either way, a leaked worktree here
    /// is self-healed later by <see cref="ReconcileAsync"/> once the owning attempt reaches a
    /// terminal state.</summary>
    public async Task<bool> DiscardAttemptAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        string attemptPath = WorktreeLayout.AttemptPath(paths, projectId, sprintId, attemptId);
        bool worktreeRemoved = await worktrees.RemoveAsync(projectRoot, attemptPath, cancellationToken)
            .ConfigureAwait(false);
        bool branchDeleted = await worktrees
            .DeleteBranchAsync(projectRoot, WorktreeLayout.AttemptBranch(attemptId), cancellationToken)
            .ConfigureAwait(false);
        return worktreeRemoved && branchDeleted;
    }

    /// <summary>
    /// Crash/restart recovery: removes only the attempt worktrees whose owning attempt is either
    /// unknown to the sprint's durable event-sourced state at all, or already terminal there
    /// (settled by a call that crashed before <see cref="DiscardAttemptAsync"/> ran). An attempt
    /// still in a non-terminal state is left untouched — its worktree may still be legitimately
    /// in use, and only the (future) executor that owns it may decide it is safe to discard.
    /// </summary>
    /// <remarks>
    /// A worktree's directory name is only the *short*, deliberately lossy form of its owning
    /// attempt id (see <see cref="WorktreeLayout.ShortId"/>) — the full <see cref="AttemptId"/>
    /// cannot be recovered from it. This matches every live, non-terminal attempt in durable state
    /// by computing *its* short id instead (the same direction every other caller already derives
    /// paths/branches in), rather than trying to parse a full id back out of the directory name.
    /// </remarks>
    public async Task ReconcileAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string attemptsRoot = WorktreeLayout.AttemptsRoot(paths, projectId, sprintId);
        if (!Directory.Exists(attemptsRoot))
        {
            return;
        }

        SprintWorkflowState? state = await store.LoadAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> liveShortIds = new(StringComparer.Ordinal);
        if (state is not null)
        {
            foreach (AttemptSnapshot attempt in state.Attempts.Values)
            {
                if (!WorkflowStateMachines.IsTerminal(attempt.State))
                {
                    liveShortIds.Add(WorktreeLayout.ShortId(attempt.Id.Value));
                }
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(attemptsRoot))
        {
            string shortId = Path.GetFileName(directory);
            if (!liveShortIds.Contains(shortId))
            {
                await worktrees.RemoveAsync(projectRoot, directory, cancellationToken).ConfigureAwait(false);
                await worktrees
                    .DeleteBranchAsync(projectRoot, WorktreeLayout.AttemptBranchFromShortId(shortId), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<GitOperationResult> RecoverIfDirtyAsync(
        string projectRoot,
        string path,
        CancellationToken cancellationToken)
    {
        if (!await worktrees.IsDirtyAsync(projectRoot, path, cancellationToken).ConfigureAwait(false))
        {
            string? cleanHead = await TryGetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false);
            return cleanHead is null
                ? GitOperationResult.Fail(DiagnosticCodes.WorktreeUnavailable)
                : GitOperationResult.Ok(cleanHead);
        }

        // Recovers only committed history — the branch pointer itself never moves here, so this
        // never continues over an unknown diff; it only discards uncommitted noise a crash left.
        string? head = await TryGetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeUnavailable);
        }

        return await worktrees.ResetHardAsync(projectRoot, path, head, cancellationToken).ConfigureAwait(false);
    }
}
