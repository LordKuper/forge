using System.Text.RegularExpressions;
using Forge.Application;

namespace Forge.Infrastructure;

/// <summary>
/// Real-Git implementation of <see cref="IWorktreeManager"/>. Every method runs `git` through
/// <see cref="IProcessRunner"/> — never a shell string — with the working directory set to either
/// the main repository (`projectRoot`, for `worktree`/`branch` plumbing commands) or the linked
/// worktree itself (for everything that inspects or mutates its checked-out content).
/// </summary>
public sealed partial class GitWorktreeManager(IProcessRunner processRunner) : IWorktreeManager
{
    // Matches `SprintOrchestrator.CommitIdPattern`: a commit-ish argument reaching this class must
    // already be a canonical, full-length hex object id — never an abbreviation, a ref name, or
    // anything that could be misread as a flag by `git`'s own argument parser. `\z`, not `$`, since
    // `$` in .NET also matches immediately before a trailing '\n'.
    [GeneratedRegex(@"\A[0-9a-f]{40}\z|\A[0-9a-f]{64}\z")]
    private static partial Regex CommitPattern();

    public async Task<bool> ExistsAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            projectRoot, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        string normalized = NormalizePath(path);
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal) &&
                string.Equals(NormalizePath(line["worktree ".Length..].Trim()), normalized, StringComparison.OrdinalIgnoreCase))
            {
                // `git worktree list` still reports an entry whose directory was deleted out from
                // under it (until the next `worktree prune`); treating that as "does not exist" is
                // what lets every caller's own exists-then-create/recover logic self-heal instead of
                // reaching a worktree-scoped git command with a working directory that is not there.
                return Directory.Exists(path);
            }
        }

        return false;
    }

    public async Task<GitOperationResult> CreateAsync(
        string projectRoot,
        string path,
        string branch,
        string commit,
        CancellationToken cancellationToken)
    {
        if (!CommitPattern().IsMatch(commit))
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeCommitInvalid);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? projectRoot);
        // `git` for Windows can silently work around Windows' default 260-character path limit for
        // its own file access once this local (repo-scoped, not machine- or user-wide) flag is set —
        // idempotent, so setting it again on every call is harmless. `WorktreeLayout` also keeps
        // every id short specifically so this class of failure is avoided by construction rather
        // than relied on this flag alone (see `WorktreeLayout.ShortId`'s own remarks).
        await RunAsync(projectRoot, ["config", "core.longpaths", "true"], cancellationToken).ConfigureAwait(false);
        ProcessResult created = await RunAsync(
            projectRoot, ["worktree", "add", "-b", branch, path, "--", commit], cancellationToken)
            .ConfigureAwait(false);
        if (created.ExitCode == 0)
        {
            return GitOperationResult.Ok(commit);
        }

        // A worktree whose directory was deleted out from under `git` (see `ExistsAsync`) still
        // holds its path registered until pruned — but pruning never touches the branch itself
        // (confirmed directly against real `git`), so a bare retry after pruning could still fail
        // with "a branch named '<branch>' already exists" forever. Clearing any leftover, no-longer-
        // registered directory content is likewise safe only once pruning has run: a *registered*
        // worktree's directory is never reached here — `ExistsAsync` would have short-circuited this
        // whole method's caller before it did.
        await RunAsync(projectRoot, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
        if (!await ExistsAsync(projectRoot, path, cancellationToken).ConfigureAwait(false) &&
            Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        if (await BranchExistsAsync(projectRoot, branch, cancellationToken).ConfigureAwait(false))
        {
            // `branch` already carries real, otherwise-unreachable history — most likely this exact
            // branch's own earlier worktree, whose directory later went missing (the integration
            // branch is the case this matters for: it can hold every commit integrated so far).
            // Re-attaching a *new* worktree to that *existing* branch preserves it. This never
            // force-deletes a branch to make room: only an explicit, caller-driven decision may ever
            // discard branch history, never this self-heal path.
            ProcessResult attached = await RunAsync(
                projectRoot, ["worktree", "add", path, "--", branch], cancellationToken).ConfigureAwait(false);
            return attached.ExitCode == 0
                ? GitOperationResult.Ok(await GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false))
                : GitOperationResult.Fail(DiagnosticCodes.WorktreeCreateFailed, attached.StandardError);
        }

        ProcessResult retried = await RunAsync(
            projectRoot, ["worktree", "add", "-b", branch, path, "--", commit], cancellationToken)
            .ConfigureAwait(false);
        return retried.ExitCode == 0
            ? GitOperationResult.Ok(commit)
            : GitOperationResult.Fail(DiagnosticCodes.WorktreeCreateFailed, retried.StandardError);
    }

    private async Task<bool> BranchExistsAsync(string projectRoot, string branch, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(projectRoot, ["branch", "--list", "--", branch], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0;
    }

    public async Task<bool> IsDirtyAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunInWorktreeAsync(path, ["status", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode != 0 || result.StandardOutput.Trim().Length > 0;
    }

    public async Task<GitOperationResult> ResetHardAsync(
        string projectRoot,
        string path,
        string commit,
        CancellationToken cancellationToken)
    {
        if (!CommitPattern().IsMatch(commit))
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeCommitInvalid);
        }

        ProcessResult reset = await RunInWorktreeAsync(path, ["reset", "--hard", commit], cancellationToken)
            .ConfigureAwait(false);
        if (reset.ExitCode != 0)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeResetFailed, reset.StandardError);
        }

        ProcessResult clean = await RunInWorktreeAsync(path, ["clean", "-fd"], cancellationToken)
            .ConfigureAwait(false);
        return clean.ExitCode == 0
            ? GitOperationResult.Ok(commit)
            : GitOperationResult.Fail(DiagnosticCodes.WorktreeResetFailed, clean.StandardError);
    }

    public async Task<string> GetHeadAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunInWorktreeAsync(path, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        string head = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || head.Length == 0)
        {
            throw new InvalidOperationException(
                $"'git rev-parse HEAD' did not resolve a commit in '{path}': {result.StandardError}");
        }

        return head;
    }

    public async Task<GitOperationResult> IntegrateFastForwardAsync(
        string projectRoot,
        string path,
        string sourceBranch,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunInWorktreeAsync(
            path, ["merge", "--ff-only", "--", sourceBranch], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeIntegrationDiverged, result.StandardError);
        }

        return GitOperationResult.Ok(await GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false));
    }

    public async Task<GitOperationResult> RebaseOntoAsync(
        string projectRoot,
        string path,
        string upstream,
        string ontoCommit,
        CancellationToken cancellationToken)
    {
        if (!CommitPattern().IsMatch(upstream) || !CommitPattern().IsMatch(ontoCommit))
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeCommitInvalid);
        }

        ProcessResult result = await RunInWorktreeAsync(
            path, ["rebase", "--onto", ontoCommit, upstream], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return GitOperationResult.Ok(
                await GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false));
        }

        // Fails closed: a conflicted rebase is aborted rather than left for the caller to discover
        // the worktree mid-rebase later. The abort's own outcome is not surfaced — the caller only
        // needs to know the gated rebase did not happen.
        await RunInWorktreeAsync(path, ["rebase", "--abort"], cancellationToken).ConfigureAwait(false);
        return GitOperationResult.Fail(DiagnosticCodes.WorktreeRebaseConflict, result.StandardError);
    }

    /// <summary>
    /// Removes a linked worktree and its directory. Returns <see langword="true"/> only once the
    /// directory is actually gone — whether it was already gone, `git` removed it cleanly, or (a
    /// worktree `git` no longer tracks at all, e.g. after an earlier partial removal, or a
    /// registration `ExistsAsync` never saw because the directory itself had already vanished) this
    /// call deleted it directly — and <see langword="false"/> if anything left real content behind
    /// (e.g. an open file handle on Windows), so a caller can tell a genuinely leaked worktree apart
    /// from a clean removal instead of assuming success because `git` itself had nothing registered
    /// to refuse.
    /// </summary>
    public async Task<bool> RemoveAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(projectRoot, path, cancellationToken).ConfigureAwait(false))
        {
            ProcessResult remove = await RunAsync(
                projectRoot, ["worktree", "remove", "--force", path], cancellationToken).ConfigureAwait(false);
            await RunAsync(projectRoot, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
            if (remove.ExitCode != 0)
            {
                return false;
            }
        }
        else
        {
            // A registration with no matching directory must not linger either, even though this
            // call's job is the directory below, not the registration.
            await RunAsync(projectRoot, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Best-effort branch deletion. Returns <see langword="true"/> if the branch ends up
    /// not existing — whether it was already gone or this call deleted it — and
    /// <see langword="false"/> if `git` refused to delete a branch that still exists (e.g. it is
    /// still checked out somewhere).</summary>
    public async Task<bool> DeleteBranchAsync(string projectRoot, string branch, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(projectRoot, ["branch", "-D", "--", branch], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return true;
        }

        ProcessResult list = await RunAsync(projectRoot, ["branch", "--list", "--", branch], cancellationToken)
            .ConfigureAwait(false);
        return list.ExitCode == 0 && list.StandardOutput.Trim().Length == 0;
    }

    private Task<ProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(new("git", arguments, workingDirectory), cancellationToken);

    /// <summary>Guards every command whose working directory is a linked worktree (as opposed to
    /// `projectRoot` itself): `git worktree list` can still report a worktree whose directory was
    /// deleted out from under it (see `ExistsAsync`), and starting a native process with a
    /// nonexistent working directory throws an unhandled `Win32Exception` instead of failing
    /// closed. Every such caller already has a defined failure path (a non-zero exit code, or
    /// `GetHeadAsync`'s own `InvalidOperationException`), so this only needs to route around the
    /// crash — it never needs its own diagnostic code.</summary>
    private Task<ProcessResult> RunInWorktreeAsync(
        string path,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        Directory.Exists(path)
            ? RunAsync(path, arguments, cancellationToken)
            : Task.FromResult(new ProcessResult(-1, string.Empty, $"'{path}' does not exist."));

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/');
}
