using Forge.Application;
using Forge.Infrastructure;
using Forge.Tests.Support;

namespace Forge.IntegrationTests;

/// <summary>
/// Exercises <see cref="GitWorktreeManager.ListAsync"/> against a real, disposable Git repository —
/// `forge doctor --bundle`'s (ADR 0005/0038) worktree-registration source. A `FakeWorktreeManager`
/// has no concept of a registration surviving its own directory's deletion (see its own doc
/// comment), so the orphan-detection half of this primitive can only be proven against real
/// `git.exe`, matching this codebase's own precedent (<c>GitIsolationTests.cs</c>,
/// <c>GitRepositoryMergeTests.cs</c>).
/// </summary>
[Collection("External process tests")]
public sealed class GitWorktreeManagerListTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsyncReportsARegisteredWorktreeAndFlagsAnExternallyDeletedOneAsNotExisting()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        string commit = await repository.HeadAsync(cancellationToken);
        string worktreePath = Path.Combine(Path.GetDirectoryName(repository.Root)!, $"{Path.GetFileName(repository.Root)}-wt");
        GitWorktreeManager worktrees = new(new ProcessRunner());
        GitOperationResult created = await worktrees.CreateAsync(
            repository.Root, worktreePath, "forge/list-test", commit, cancellationToken);
        Assert.True(created.Succeeded, $"create failed: {created.DiagnosticCode} ({created.Detail})");

        IReadOnlyList<WorktreeRegistration> beforeDeletion =
            await worktrees.ListAsync(repository.Root, cancellationToken);
        WorktreeRegistration registration = Assert.Single(
            beforeDeletion, entry => SamePath(entry.Path, worktreePath));
        Assert.True(registration.Exists);
        // git worktree list always includes the primary worktree first -- the count must reflect
        // that reality, not silently drop it.
        Assert.Contains(beforeDeletion, entry => SamePath(entry.Path, repository.Root));

        // Simulates external deletion (not a Forge-driven RemoveAsync) -- git still believes the
        // worktree is registered until the next prune.
        Directory.Delete(worktreePath, true);

        IReadOnlyList<WorktreeRegistration> afterDeletion =
            await worktrees.ListAsync(repository.Root, cancellationToken);
        WorktreeRegistration orphaned = Assert.Single(afterDeletion, entry => SamePath(entry.Path, worktreePath));
        Assert.False(orphaned.Exists);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
