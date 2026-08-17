using Forge.Domain;
using Forge.Infrastructure;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class GitContextReaderTests
{
    private static readonly string[] GitShowCapability = [ContextCapabilityIds.GitShow];
    private static readonly string[] GitGrepCapability = [ContextCapabilityIds.GitGrep];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GitShowReadsFileContentAtThePinnedCommit()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.CommitFileAsync(
            "notes.md", "hello from the pinned commit", "add notes", TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, ShowOperation("read-notes", "notes.md"));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);

        Assert.Equal(ContextQueryPlanDiagnostic.None, result.Diagnostic);
        ContextQueryResult operation = Assert.Single(result.Bundle!.Results);
        Assert.Equal(ContextQueryOperationDiagnostic.None, operation.Diagnostic);
        Assert.Equal("hello from the pinned commit", operation.Content);
        Assert.False(operation.Truncated);
        Assert.NotNull(operation.ContentDigest);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GitShowForAMissingPathReportsNotFound()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.HeadAsync(TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, ShowOperation("read-missing", "does-not-exist.md"));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);

        ContextQueryResult operation = Assert.Single(result.Bundle!.Results);
        Assert.Equal(ContextQueryOperationDiagnostic.NotFound, operation.Diagnostic);
        Assert.Null(operation.Content);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GitGrepFindsAMatchingLineAtThePinnedCommit()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.CommitFileAsync(
            "src/note.md", "line one\nfindme right here\nline three", "add note", TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, GrepOperation("search", "findme"));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitGrepCapability, TestContext.Current.CancellationToken);

        ContextQueryResult operation = Assert.Single(result.Bundle!.Results);
        Assert.Equal(ContextQueryOperationDiagnostic.None, operation.Diagnostic);
        Assert.Contains("findme right here", operation.Content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GitGrepWithNoMatchesIsAnEmptySuccessNotAFailure()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.HeadAsync(TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, GrepOperation("search", "no-such-pattern-anywhere"));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitGrepCapability, TestContext.Current.CancellationToken);

        ContextQueryResult operation = Assert.Single(result.Bundle!.Results);
        Assert.Equal(ContextQueryOperationDiagnostic.None, operation.Diagnostic);
        Assert.Equal(string.Empty, operation.Content);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ContentOverMaxResultBytesIsTruncatedAndFlagged()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.CommitFileAsync(
            "big.md", new string('x', 100), "add big file", TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, ShowOperation("read-big", "big.md", maxResultBytes: 10));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);

        ContextQueryResult operation = Assert.Single(result.Bundle!.Results);
        Assert.True(operation.Truncated);
        Assert.Equal(10, operation.ByteCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnOperationWhoseCapabilityIsNotAllowlistedRejectsTheWholePlan()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.HeadAsync(TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, ShowOperation("read", "README.md"));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitGrepCapability, TestContext.Current.CancellationToken);

        Assert.Equal(ContextQueryPlanDiagnostic.CapabilityDenied, result.Diagnostic);
        Assert.Null(result.Bundle);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("../outside.md")]
    [InlineData("/absolute.md")]
    [InlineData("a/../b.md")]
    public async Task AnUnsafePathRejectsTheWholePlan(string unsafePath)
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.HeadAsync(TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, ShowOperation("read", unsafePath));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);

        Assert.Equal(ContextQueryPlanDiagnostic.PathUnsafe, result.Diagnostic);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANonCanonicalCommitRejectsTheWholePlan()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan("HEAD", ShowOperation("read", "README.md"));

        ContextQueryPlanResult result = await new GitContextReader(new ProcessRunner()).ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);

        Assert.Equal(ContextQueryPlanDiagnostic.SchemaInvalid, result.Diagnostic);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayingTheSamePlanAgainstTheSameCommitReproducesByteIdenticalDigests()
    {
        using GitTestRepository repository = await GitTestRepository.CreateAsync(TestContext.Current.CancellationToken);
        string commit = await repository.CommitFileAsync(
            "notes.md", "reproducible content", "add notes", TestContext.Current.CancellationToken);
        ContextQueryPlan plan = Plan(commit, ShowOperation("read-notes", "notes.md"));
        GitContextReader reader = new(new ProcessRunner());

        ContextQueryPlanResult first = await reader.ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);
        ContextQueryPlanResult second = await reader.ExecuteAsync(
            repository.Root, plan, GitShowCapability, TestContext.Current.CancellationToken);

        Assert.Equal(first.Bundle!.PlanDigest, second.Bundle!.PlanDigest);
        Assert.Equal(first.Bundle.Results[0].ContentDigest, second.Bundle.Results[0].ContentDigest);
    }

    private static ContextQueryPlan Plan(string commit, params ContextQueryOperation[] operations) =>
        new(ContextQueryPlan.ContractVersion, commit, operations);

    private static ContextQueryOperation ShowOperation(string id, string path, int? maxResultBytes = null) =>
        new(id, ContextQueryOperationKind.GitShow, Path: path, MaxResultBytes: maxResultBytes);

    private static ContextQueryOperation GrepOperation(string id, string pattern) =>
        new(id, ContextQueryOperationKind.GitGrep, Pattern: pattern);
}
