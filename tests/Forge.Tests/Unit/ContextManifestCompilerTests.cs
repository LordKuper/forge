using Forge.Compiler;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class ContextManifestCompilerTests
{
    private static readonly Guid SprintId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    [Trait("Category", "Unit")]
    public void AnEmptyDocumentSetProducesAnEmptyManifest()
    {
        ContextManifest manifest = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", new([], []), tokenBudget: 1000);

        Assert.Equal(ContextManifest.ContractVersion, manifest.SchemaVersion);
        Assert.Empty(manifest.Layers.Rules);
        Assert.Empty(manifest.Layers.SprintSpecifications);
        Assert.Empty(manifest.Layers.Knowledge);
        Assert.Empty(manifest.Layers.Handoffs);
        Assert.Empty(manifest.Layers.QueryResults);
        Assert.Empty(manifest.Truncated);
        Assert.Equal(0, manifest.AllocatedTokens);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RulesAndAcceptedKnowledgeAdmitInDeterministicOrder()
    {
        ForgeDocumentSet documents = new(
            [
                Document("rules/z.md", ForgeDocumentKind.Rule, tokens: 10),
                Document("rules/a.md", ForgeDocumentKind.Rule, tokens: 10),
                Document("knowledge/adr.md", ForgeDocumentKind.Knowledge, tokens: 10, status: ForgeDocumentStatus.Accepted),
                Document("knowledge/plain.md", ForgeDocumentKind.Knowledge, tokens: 10, status: null),
            ],
            []);

        ContextManifest manifest = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 1000);

        Assert.Equal(["rules/a.md", "rules/z.md"], manifest.Layers.Rules.Select(item => item.RelativePath));
        Assert.Equal(
            ["knowledge/adr.md", "knowledge/plain.md"],
            manifest.Layers.Knowledge.Select(item => item.RelativePath));
        Assert.All(manifest.Layers.Rules, item => Assert.Equal("rule", item.Rationale));
        Assert.All(manifest.Layers.Knowledge, item => Assert.Equal("knowledge:accepted", item.Rationale));
        Assert.Equal(40, manifest.AllocatedTokens);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(ForgeDocumentStatus.Proposed)]
    [InlineData(ForgeDocumentStatus.Rejected)]
    [InlineData(ForgeDocumentStatus.Superseded)]
    public void KnowledgeWithoutAcceptedStatusIsExcludedNotTruncated(ForgeDocumentStatus status)
    {
        ForgeDocumentSet documents = new([Document("knowledge/adr.md", ForgeDocumentKind.Knowledge, tokens: 10, status: status)], []);

        ContextManifest manifest = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 1000);

        Assert.Empty(manifest.Layers.Knowledge);
        Assert.Empty(manifest.Truncated);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AnItemThatDoesNotFitTheRemainingBudgetIsTruncatedAndLaterSmallerItemsStillAdmit()
    {
        ForgeDocumentSet documents = new(
            [
                Document("rules/big.md", ForgeDocumentKind.Rule, tokens: 900),
                Document("rules/small.md", ForgeDocumentKind.Rule, tokens: 50),
            ],
            []);

        ContextManifest manifest = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 100);

        Assert.Equal(["rules/small.md"], manifest.Layers.Rules.Select(item => item.RelativePath));
        ContextManifestTruncatedItem truncated = Assert.Single(manifest.Truncated);
        Assert.Equal("rules/big.md", truncated.RelativePath);
        Assert.Equal("over_budget", truncated.Reason);
        Assert.Equal(50, manifest.AllocatedTokens);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IdenticalInputsAlwaysProduceTheSameManifestDigest()
    {
        ForgeDocumentSet documents = new([Document("rules/a.md", ForgeDocumentKind.Rule, tokens: 10)], []);

        ContextManifest first = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 1000);
        ContextManifest second = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 1000);

        Assert.Equal(first.ManifestDigest, second.ManifestDigest);
        Assert.StartsWith("sha256:", first.ManifestDigest, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AttachingQueryResultsAddsTheLayerAndChangesTheDigest()
    {
        ForgeDocumentSet documents = new([Document("rules/a.md", ForgeDocumentKind.Rule, tokens: 10)], []);
        ContextManifest manifest = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 1000);
        ContextResultBundle bundle = new(
            ContextResultBundle.ContractVersion,
            "sha256:" + new string('a', 64),
            SourceCommit,
            [new("op-1", ContextQueryOperationDiagnostic.None, "content", "sha256:" + new string('b', 64), 7, false)]);

        ContextManifest withResults = ContextManifestCompiler.WithQueryResults(manifest, bundle);

        ContextManifestItem item = Assert.Single(withResults.Layers.QueryResults);
        Assert.Equal("op-1", item.RelativePath);
        Assert.Equal(10 + 2, withResults.AllocatedTokens);
        Assert.NotEqual(manifest.ManifestDigest, withResults.ManifestDigest);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QueryResultsThatDoNotFitTheRemainingBudgetAreTruncatedNotSilentlyAdmitted()
    {
        ForgeDocumentSet documents = new([Document("rules/a.md", ForgeDocumentKind.Rule, tokens: 90)], []);
        ContextManifest manifest = ContextManifestCompiler.Compile(
            SprintId, SourceCommit, "implementation-critical", "1.0.0", documents, tokenBudget: 100);
        string oversizedContent = new('x', 1000);
        ContextResultBundle bundle = new(
            ContextResultBundle.ContractVersion,
            "sha256:" + new string('a', 64),
            SourceCommit,
            [new("op-1", ContextQueryOperationDiagnostic.None, oversizedContent, "sha256:" + new string('b', 64), 1000, false)]);

        ContextManifest withResults = ContextManifestCompiler.WithQueryResults(manifest, bundle);

        Assert.Empty(withResults.Layers.QueryResults);
        Assert.Equal(90, withResults.AllocatedTokens);
        ContextManifestTruncatedItem truncated = Assert.Single(withResults.Truncated);
        Assert.Equal("op-1", truncated.RelativePath);
        Assert.Equal("over_budget", truncated.Reason);
        Assert.True(withResults.AllocatedTokens <= withResults.TokenBudget);
    }

    private static ForgeDocument Document(
        string relativePath, ForgeDocumentKind kind, int tokens, ForgeDocumentStatus? status = null) =>
        new(
            Id: relativePath,
            Kind: kind,
            Scope: ForgeDocumentScope.Project,
            Title: relativePath,
            RelativePath: relativePath,
            Body: new string('x', tokens * 4),
            EstimatedTokens: tokens,
            ContextLimitTokens: 8000,
            References: [],
            Status: status);
}
