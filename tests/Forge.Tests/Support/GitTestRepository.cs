using Forge.Application;
using Forge.Infrastructure;

namespace Forge.Tests.Support;

/// <summary>
/// A real, disposable temporary Git repository for Stage 7 tests — `git.exe` itself, not a fake,
/// since worktree/merge/rebase behavior is exactly what those tests must prove.
/// </summary>
internal sealed class GitTestRepository : IDisposable
{
    private readonly ProcessRunner runner = new();

    private GitTestRepository()
    {
        Root = Path.Combine(Path.GetTempPath(), $"forge-git-tests-{Guid.NewGuid():N}");
    }

    public string Root { get; }

    /// <summary>
    /// Points `git` at a global/system config that cannot exist, instead of whatever machine this
    /// happens to run on: a `commit.gpgsign=true` with no usable key, a `core.hooksPath` pointing at
    /// real hooks, or a `commit.template`/`user.signingkey` gap would otherwise break every commit
    /// here unpredictably, and reproducing that failure would depend on the developer's or CI
    /// runner's own machine state rather than this repository's own local config (set explicitly in
    /// <see cref="CreateAsync"/>).
    /// </summary>
    private IReadOnlyDictionary<string, string> IsolatedEnvironment =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "no-such-global-gitconfig"),
            ["GIT_CONFIG_SYSTEM"] = Path.Combine(Root, "no-such-system-gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };

    public static async Task<GitTestRepository> CreateAsync(CancellationToken cancellationToken)
    {
        GitTestRepository repository = new();
        Directory.CreateDirectory(repository.Root);
        ProcessResult init = await repository
            .RunAsync(repository.Root, ["init", "--initial-branch=main"], cancellationToken)
            .ConfigureAwait(false);
        if (init.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git init' failed in '{repository.Root}': {init.StandardError}");
        }

        await repository.RunAsync(
            repository.Root, ["config", "user.email", "forge-tests@example.invalid"], cancellationToken)
            .ConfigureAwait(false);
        await repository.RunAsync(repository.Root, ["config", "user.name", "Forge Tests"], cancellationToken)
            .ConfigureAwait(false);
        await repository.RunAsync(repository.Root, ["config", "commit.gpgsign", "false"], cancellationToken)
            .ConfigureAwait(false);
        await repository.CommitFileAsync("README.md", "forge test repo", "initial", cancellationToken)
            .ConfigureAwait(false);
        return repository;
    }

    /// <summary>Writes and commits one file on whatever branch is currently checked out at
    /// <paramref name="workingDirectory"/> (the main repo root by default, or a worktree path),
    /// returning the new commit. Every `git` step's exit code is checked: a test whose setup
    /// silently failed to actually produce a new commit (e.g. a lock, or a config gap on some
    /// machine) must fail loudly at that setup step, never quietly degrade into asserting a
    /// vacuously true result later — e.g. a fast-forward integration test "passing" only because it
    /// happened to already be at the expected commit.</summary>
    public async Task<string> CommitFileAsync(
        string relativePath,
        string content,
        string message,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        string directory = workingDirectory ?? Root;
        string? beforeHead = await TryHeadAsync(directory, cancellationToken).ConfigureAwait(false);
        string path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        ProcessResult add = await RunAsync(directory, ["add", relativePath], cancellationToken).ConfigureAwait(false);
        if (add.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git add' failed in '{directory}': {add.StandardError}");
        }

        ProcessResult commit = await RunAsync(directory, ["commit", "-m", message], cancellationToken)
            .ConfigureAwait(false);
        if (commit.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git commit' failed in '{directory}': {commit.StandardError}");
        }

        string afterHead = await HeadAsync(directory, cancellationToken).ConfigureAwait(false);
        if (afterHead == beforeHead)
        {
            throw new InvalidOperationException($"'git commit' in '{directory}' did not advance HEAD.");
        }

        return afterHead;
    }

    private async Task<string?> TryHeadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(workingDirectory, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public Task<string> HeadAsync(CancellationToken cancellationToken) => HeadAsync(Root, cancellationToken);

    public async Task<string> HeadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(workingDirectory, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'git rev-parse HEAD' failed in '{workingDirectory}': {result.StandardError}");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>A missing `workingDirectory` throws an unhandled `Win32Exception` from
    /// `Process.Start` on Windows rather than failing in any way callers can distinguish from a real
    /// `git` failure; guarded the same way `GitWorktreeManager.RunInWorktreeAsync` guards production
    /// code, so a genuine creation failure surfaces as a normal, diagnosable non-zero exit instead of
    /// an unrelated-looking crash several calls later.</summary>
    public Task<ProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        Directory.Exists(workingDirectory)
            ? runner.RunAsync(new("git", arguments, workingDirectory, IsolatedEnvironment), null, cancellationToken)
            : Task.FromResult(new ProcessResult(-1, string.Empty, $"'{workingDirectory}' does not exist."));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (IOException)
        {
            // Temporary directories are reclaimed by the operating system.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary directories are reclaimed by the operating system.
        }
    }
}
