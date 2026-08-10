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

    public static async Task<GitTestRepository> CreateAsync(CancellationToken cancellationToken)
    {
        GitTestRepository repository = new();
        Directory.CreateDirectory(repository.Root);
        await repository.RunAsync(repository.Root, ["init", "--initial-branch=main"], cancellationToken)
            .ConfigureAwait(false);
        await repository.RunAsync(
            repository.Root, ["config", "user.email", "forge-tests@example.invalid"], cancellationToken)
            .ConfigureAwait(false);
        await repository.RunAsync(repository.Root, ["config", "user.name", "Forge Tests"], cancellationToken)
            .ConfigureAwait(false);
        await repository.CommitFileAsync("README.md", "forge test repo", "initial", cancellationToken)
            .ConfigureAwait(false);
        return repository;
    }

    /// <summary>Writes and commits one file on whatever branch is currently checked out at
    /// <paramref name="workingDirectory"/> (the main repo root by default, or a worktree path),
    /// returning the new commit.</summary>
    public async Task<string> CommitFileAsync(
        string relativePath,
        string content,
        string message,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        string directory = workingDirectory ?? Root;
        string path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        await RunAsync(directory, ["add", relativePath], cancellationToken).ConfigureAwait(false);
        await RunAsync(directory, ["commit", "-m", message], cancellationToken).ConfigureAwait(false);
        return await HeadAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> HeadAsync(CancellationToken cancellationToken) => HeadAsync(Root, cancellationToken);

    public async Task<string> HeadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(workingDirectory, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    public Task<ProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        runner.RunAsync(new("git", arguments, workingDirectory), cancellationToken);

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
