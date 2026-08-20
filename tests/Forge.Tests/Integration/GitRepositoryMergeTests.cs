using Forge.Application;
using Forge.Infrastructure;
using Forge.Tests.Support;

namespace Forge.IntegrationTests;

/// <summary>
/// Exercises <see cref="GitRepository.MergeSprintIntoDefaultBranchAsync"/> against a real,
/// disposable Git repository — ADR 0036's highest-risk primitive, the first Forge operation that
/// ever mutates a project's own checked-out working directory. A <c>FakeRepository</c> can assert
/// the calling contract but cannot prove the actual `git.exe` guards (dirty-tree refusal,
/// wrong-branch refusal, fast-forward-only divergence handling) hold against real `git` behavior.
/// </summary>
[Collection("External process tests")]
public sealed class GitRepositoryMergeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeSprintIntoDefaultBranchAsyncFastForwardsTheDefaultBranchOntoTheSourceBranch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "-b", "integration"], cancellationToken);
        string tip = await repository.CommitFileAsync(
            "feature.md", "sprint changes", "sprint work", cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "main"], cancellationToken);
        GitRepository git = new(new ProcessRunner());

        GitOperationResult result = await git.MergeSprintIntoDefaultBranchAsync(
            repository.Root, "main", "integration", cancellationToken);

        Assert.True(result.Succeeded, $"merge failed: {result.DiagnosticCode} ({result.Detail})");
        Assert.Equal(tip, result.Commit);
        Assert.Equal(tip, await repository.HeadAsync(cancellationToken));
        Assert.Equal("main", await git.GetCurrentBranchAsync(repository.Root, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeSprintIntoDefaultBranchAsyncRefusesADirtyDefaultBranch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "-b", "integration"], cancellationToken);
        await repository.CommitFileAsync("feature.md", "sprint changes", "sprint work", cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "main"], cancellationToken);
        string headBefore = await repository.HeadAsync(cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(repository.Root, "uncommitted.txt"), "noise", cancellationToken);
        GitRepository git = new(new ProcessRunner());

        GitOperationResult result = await git.MergeSprintIntoDefaultBranchAsync(
            repository.Root, "main", "integration", cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.RepositoryDirty, result.DiagnosticCode);
        Assert.Equal(headBefore, await repository.HeadAsync(cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeSprintIntoDefaultBranchAsyncRefusesWhenTheCheckedOutBranchDoesNotMatch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "-b", "integration"], cancellationToken);
        await repository.CommitFileAsync("feature.md", "sprint changes", "sprint work", cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "-b", "other"], cancellationToken);
        string headBefore = await repository.HeadAsync(cancellationToken);
        GitRepository git = new(new ProcessRunner());

        GitOperationResult result = await git.MergeSprintIntoDefaultBranchAsync(
            repository.Root, "main", "integration", cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.RepositoryBranchMismatch, result.DiagnosticCode);
        Assert.Equal("other", await git.GetCurrentBranchAsync(repository.Root, cancellationToken));
        Assert.Equal(headBefore, await repository.HeadAsync(cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MergeSprintIntoDefaultBranchAsyncFailsClosedOnDivergentHistoryWithoutMovingTheDefaultBranch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "-b", "integration"], cancellationToken);
        await repository.CommitFileAsync("feature.md", "sprint changes", "sprint work", cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "main"], cancellationToken);
        string headBefore = await repository.CommitFileAsync(
            "unrelated.md", "unrelated main-branch progress", "unrelated work", cancellationToken);
        GitRepository git = new(new ProcessRunner());

        GitOperationResult result = await git.MergeSprintIntoDefaultBranchAsync(
            repository.Root, "main", "integration", cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorktreeIntegrationDiverged, result.DiagnosticCode);
        Assert.Equal(headBefore, await repository.HeadAsync(cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetCurrentBranchAsyncReturnsNullForADetachedHead()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        string head = await repository.HeadAsync(cancellationToken);
        await repository.RunAsync(repository.Root, ["checkout", "--detach", head], cancellationToken);
        GitRepository git = new(new ProcessRunner());

        string? branch = await git.GetCurrentBranchAsync(repository.Root, cancellationToken);

        Assert.Null(branch);
    }
}
