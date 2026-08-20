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
        string worktreePath = Path.Combine(
            Path.GetDirectoryName(repository.Root)!, $"{Path.GetFileName(repository.Root)}-wt");
        GitWorktreeManager worktrees = new(new ProcessRunner());
        GitOperationResult created = await worktrees.CreateAsync(
            repository.Root, worktreePath, "forge/list-test", commit, cancellationToken);
        Assert.True(created.Succeeded, $"create failed: {created.DiagnosticCode} ({created.Detail})");

        IReadOnlyList<WorktreeRegistration> beforeDeletion =
            await worktrees.ListAsync(repository.Root, cancellationToken);
        // `git worktree list --porcelain` always reports the primary worktree first, then linked
        // ones in creation order -- matched by position rather than comparing `Path` against a
        // string this test built itself: a CI runner whose temp directory resolves through a
        // symlink (macOS's `/tmp` -> `/private/tmp`) has `git` canonicalize the registered path in
        // its own output, which a plain `Path.GetFullPath` comparison against this test's own,
        // uncanonicalized `worktreePath`/`repository.Root` strings does not account for -- confirmed
        // failing exactly this way in CI. Letting `ListAsync`'s own output be the source of truth
        // for "which entry is the new one" avoids needing to reproduce git's own path resolution.
        Assert.Equal(2, beforeDeletion.Count);
        Assert.True(beforeDeletion[0].Exists, "the primary worktree must report as existing.");
        Assert.True(beforeDeletion[1].Exists, "the newly created worktree must report as existing.");

        // Simulates external deletion (not a Forge-driven RemoveAsync) -- git still believes the
        // worktree is registered until the next prune. Deleted through this test's own path
        // (equivalent to the registered one regardless of symlink resolution -- filesystem
        // operations follow symlinks transparently; only the *comparison* above needed avoiding).
        Directory.Delete(worktreePath, true);

        IReadOnlyList<WorktreeRegistration> afterDeletion =
            await worktrees.ListAsync(repository.Root, cancellationToken);
        Assert.Equal(2, afterDeletion.Count);
        Assert.True(afterDeletion[0].Exists, "the primary worktree must still report as existing.");
        Assert.False(afterDeletion[1].Exists, "the externally deleted worktree must report as orphaned.");
    }
}
