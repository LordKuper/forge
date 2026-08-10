using System.Collections.Concurrent;
using Forge.Domain;

namespace Forge.Application;

/// <summary>Deterministic filesystem/branch naming for every worktree Stage 7 creates. Worktrees
/// live under the user's local application data, keyed by project id — never inside the project's
/// own working tree — so a sprint's isolated mutation can never appear as untracked content in the
/// user's main checkout and needs no `.gitignore` coordination.</summary>
public static class WorktreeLayout
{
    public static string IntegrationBranch(SprintId sprintId) => $"forge/sprint/{sprintId.Value:N}";

    public static string AttemptBranch(AttemptId attemptId) => $"forge/attempt/{attemptId.Value:N}";

    public static string SprintRoot(IEnvironmentPaths paths, Guid projectId, SprintId sprintId) =>
        Path.Combine(
            paths.LocalApplicationData, "worktrees", projectId.ToString("N"), "sprints", sprintId.Value.ToString("N"));

    public static string IntegrationPath(IEnvironmentPaths paths, Guid projectId, SprintId sprintId) =>
        Path.Combine(SprintRoot(paths, projectId, sprintId), "integration");

    public static string AttemptsRoot(IEnvironmentPaths paths, Guid projectId, SprintId sprintId) =>
        Path.Combine(SprintRoot(paths, projectId, sprintId), "attempts");

    public static string AttemptPath(IEnvironmentPaths paths, Guid projectId, SprintId sprintId, AttemptId attemptId) =>
        Path.Combine(AttemptsRoot(paths, projectId, sprintId), attemptId.Value.ToString("N"));
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
/// (invoke a provider, commit its edits) is Stage 10's job; this class only creates, integrates,
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
            string existingHead = await worktrees.GetHeadAsync(projectRoot, attemptPath, cancellationToken)
                .ConfigureAwait(false);
            return GitOperationResult.Ok(existingHead);
        }

        string integrationPath = WorktreeLayout.IntegrationPath(paths, projectId, sprintId);
        string baseCommit = await worktrees.GetHeadAsync(projectRoot, integrationPath, cancellationToken)
            .ConfigureAwait(false);
        return await worktrees.CreateAsync(
            projectRoot, attemptPath, WorktreeLayout.AttemptBranch(attemptId), baseCommit, cancellationToken)
            .ConfigureAwait(false);
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
            string actualTip = await worktrees.GetHeadAsync(projectRoot, integrationPath, cancellationToken)
                .ConfigureAwait(false);
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
        string newBase = await worktrees.GetHeadAsync(projectRoot, integrationPath, cancellationToken)
            .ConfigureAwait(false);
        return await worktrees.RebaseOntoAsync(
            projectRoot, attemptPath, previousBase, newBase, cancellationToken).ConfigureAwait(false);
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
        foreach (string directory in Directory.EnumerateDirectories(attemptsRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid rawAttemptId))
            {
                continue;
            }

            AttemptId attemptId = new(rawAttemptId);
            AttemptSnapshot? attempt = null;
            state?.Attempts.TryGetValue(rawAttemptId.ToString("D"), out attempt);
            bool terminalOrUnknown = attempt is null ||
                attempt.State is AttemptState.Succeeded or AttemptState.Failed or AttemptState.Abandoned or AttemptState.Cancelled;
            if (terminalOrUnknown)
            {
                await DiscardAttemptAsync(projectRoot, projectId, sprintId, attemptId, cancellationToken)
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
            return GitOperationResult.Ok(
                await worktrees.GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false));
        }

        // Recovers only committed history — the branch pointer itself never moves here, so this
        // never continues over an unknown diff; it only discards uncommitted noise a crash left.
        string head = await worktrees.GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false);
        return await worktrees.ResetHardAsync(projectRoot, path, head, cancellationToken).ConfigureAwait(false);
    }
}
