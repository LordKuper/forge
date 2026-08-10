using Forge.Application;

namespace Forge.Infrastructure;

/// <summary>
/// Real-Git implementation of <see cref="IWorktreeManager"/>. Every method runs `git` through
/// <see cref="IProcessRunner"/> — never a shell string — with the working directory set to either
/// the main repository (`projectRoot`, for `worktree`/`branch` plumbing commands) or the linked
/// worktree itself (for everything that inspects or mutates its checked-out content).
/// </summary>
public sealed class GitWorktreeManager(IProcessRunner processRunner) : IWorktreeManager
{
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
                return true;
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
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? projectRoot);
        // A worktree lives under `%LOCALAPPDATA%\Forge\worktrees\<project>\<sprint>\...`, several
        // directory levels deeper than the user's own repository; combined with `git`'s own
        // administrative files under `.git\worktrees\<name>\...`, that total can exceed Windows'
        // default 260-character path limit even though no single segment looks unreasonable. `git`
        // for Windows silently works around that limit once this local (repo-scoped, not machine-
        // or user-wide) flag is set — idempotent, so setting it again on every call is harmless.
        await RunAsync(projectRoot, ["config", "core.longpaths", "true"], cancellationToken).ConfigureAwait(false);
        ProcessResult result = await RunAsync(
            projectRoot, ["worktree", "add", "-b", branch, path, commit], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0
            ? GitOperationResult.Ok(commit)
            : GitOperationResult.Fail(DiagnosticCodes.WorktreeCreateFailed);
    }

    public async Task<bool> IsDirtyAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(path, ["status", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode != 0 || result.StandardOutput.Trim().Length > 0;
    }

    public async Task<GitOperationResult> ResetHardAsync(
        string projectRoot,
        string path,
        string commit,
        CancellationToken cancellationToken)
    {
        ProcessResult reset = await RunAsync(path, ["reset", "--hard", commit], cancellationToken)
            .ConfigureAwait(false);
        if (reset.ExitCode != 0)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeResetFailed);
        }

        ProcessResult clean = await RunAsync(path, ["clean", "-fd"], cancellationToken).ConfigureAwait(false);
        return clean.ExitCode == 0
            ? GitOperationResult.Ok(commit)
            : GitOperationResult.Fail(DiagnosticCodes.WorktreeResetFailed);
    }

    public async Task<string> GetHeadAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(path, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        string head = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || head.Length == 0)
        {
            throw new InvalidOperationException($"'git rev-parse HEAD' did not resolve a commit in '{path}'.");
        }

        return head;
    }

    public async Task<GitOperationResult> IntegrateFastForwardAsync(
        string projectRoot,
        string path,
        string sourceBranch,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            path, ["merge", "--ff-only", sourceBranch], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeIntegrationDiverged);
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
        ProcessResult result = await RunAsync(
            path, ["rebase", "--onto", ontoCommit, upstream], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return GitOperationResult.Ok(
                await GetHeadAsync(projectRoot, path, cancellationToken).ConfigureAwait(false));
        }

        // Fails closed: a conflicted rebase is aborted rather than left for the caller to discover
        // the worktree mid-rebase later. The abort's own outcome is not surfaced — the caller only
        // needs to know the gated rebase did not happen.
        await RunAsync(path, ["rebase", "--abort"], cancellationToken).ConfigureAwait(false);
        return GitOperationResult.Fail(DiagnosticCodes.WorktreeRebaseConflict);
    }

    public async Task RemoveAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(projectRoot, path, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await RunAsync(projectRoot, ["worktree", "remove", "--force", path], cancellationToken)
            .ConfigureAwait(false);
        await RunAsync(projectRoot, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBranchAsync(string projectRoot, string branch, CancellationToken cancellationToken) =>
        await RunAsync(projectRoot, ["branch", "-D", branch], cancellationToken).ConfigureAwait(false);

    private Task<ProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(new("git", arguments, workingDirectory), cancellationToken);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/');
}
