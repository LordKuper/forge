using Forge.Application;
using Forge.Compiler;
using Forge.Localization;
using Forge.Providers;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Tests.Support;

namespace Forge.ProviderAdapterTests;

public sealed class IntegrationGenerationTests
{
    private static readonly ResourceLocalizationCatalog Catalog = new();
    private static readonly IProviderIntegrationGenerator[] Generators =
        [new ClaudeIntegrationGenerator(), new CodexIntegrationGenerator()];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerationProducesOneArtifactPerEnabledProviderWithSharedHashes()
    {
        using TempForgeProject project = new();
        project.WriteRule("testing.md", Rule("testing-invariant", "Testing invariant", "Implement first."));
        project.WriteKnowledge("adr-0006.md", Rule("adr-0006", "ADR 0006 summary", "Review converges."));
        IntegrationGenerationService service = new(Generators, Catalog);

        IntegrationGenerationResult result = await service.GenerateAsync(
            project.Root,
            [CodexLlmProvider.Codex, ClaudeLlmProvider.ClaudeCode],
            "en",
            "en",
            "0.31.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationGenerationDiagnostic.None, result.Diagnostic);
        Assert.Empty(result.DocumentErrors);
        Assert.Equal(2, result.Artifacts.Count);
        GeneratedArtifact codex = Assert.Single(result.Artifacts, a => a.ProviderId == CodexLlmProvider.Codex);
        GeneratedArtifact claude = Assert.Single(result.Artifacts, a => a.ProviderId == ClaudeLlmProvider.ClaudeCode);

        Assert.Equal("AGENTS.md", codex.RelativePath);
        Assert.Equal("CLAUDE.md", claude.RelativePath);
        Assert.Equal("agent_facing", codex.Audience);
        Assert.Equal("agent_facing", claude.Audience);
        Assert.Equal("en", codex.Language);
        Assert.Equal("en", claude.Language);
        Assert.Equal("text/markdown", codex.MediaType);
        Assert.Equal("0.31.0", codex.GeneratorVersion);

        // Both artifacts describe the same canonical generation pass.
        Assert.Equal(codex.SourceDigest, claude.SourceDigest);
        Assert.Equal(codex.PolicySnapshotHash, claude.PolicySnapshotHash);

        // Codex carries the full merged body; Claude stays thin and imports it.
        Assert.Contains("Testing invariant", codex.Content, StringComparison.Ordinal);
        Assert.Contains("ADR 0006 summary", codex.Content, StringComparison.Ordinal);
        Assert.Contains("@AGENTS.md", claude.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Testing invariant", claude.Content, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OnlyEnabledProvidersProduceArtifacts()
    {
        using TempForgeProject project = new();
        IntegrationGenerationService service = new(Generators, Catalog);

        IntegrationGenerationResult result = await service.GenerateAsync(
            project.Root,
            [CodexLlmProvider.Codex],
            "en",
            "en",
            "0.31.0",
            TestContext.Current.CancellationToken);

        GeneratedArtifact artifact = Assert.Single(result.Artifacts);
        Assert.Equal(CodexLlmProvider.Codex, artifact.ProviderId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IdenticalCanonicalContentAndPolicyProduceAnIdenticalDigestAcrossIndependentRuns()
    {
        using TempForgeProject firstProject = new();
        using TempForgeProject secondProject = new();
        firstProject.WriteRule("testing.md", Rule("testing-invariant", "Testing invariant", "Implement first."));
        secondProject.WriteRule("testing.md", Rule("testing-invariant", "Testing invariant", "Implement first."));
        IntegrationGenerationService service = new(Generators, Catalog);

        IntegrationGenerationResult first = await service.GenerateAsync(
            firstProject.Root, [CodexLlmProvider.Codex], "en", "en", "0.31.0",
            TestContext.Current.CancellationToken);
        IntegrationGenerationResult second = await service.GenerateAsync(
            secondProject.Root, [CodexLlmProvider.Codex], "en", "en", "0.31.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Artifacts.Single().SourceDigest, second.Artifacts.Single().SourceDigest);
        Assert.Equal(first.Artifacts.Single().Content, second.Artifacts.Single().Content);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ChangingCanonicalContentChangesTheDigestAndReportsDrift()
    {
        using TempForgeProject project = new();
        project.WriteRule("testing.md", Rule("testing-invariant", "Testing invariant", "Version one."));
        IntegrationGenerationService service = new(Generators, Catalog);
        IntegrationGenerationResult before = await service.GenerateAsync(
            project.Root, [CodexLlmProvider.Codex], "en", "en", "0.31.0",
            TestContext.Current.CancellationToken);
        string previousDigest = before.Artifacts.Single().SourceDigest;

        project.WriteRule("testing.md", Rule("testing-invariant", "Testing invariant", "Version two."));
        IntegrationGenerationResult after = await service.GenerateAsync(
            project.Root, [CodexLlmProvider.Codex], "en", "en", "0.31.0",
            TestContext.Current.CancellationToken);

        GeneratedArtifact artifact = after.Artifacts.Single();
        Assert.NotEqual(previousDigest, artifact.SourceDigest);
        Assert.True(artifact.HasDrifted(previousDigest));
        Assert.False(artifact.HasDrifted(artifact.SourceDigest));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnsupportedAgentFacingLanguageBlocksGenerationWithoutThrowing()
    {
        using TempForgeProject project = new();
        IntegrationGenerationService service = new(Generators, Catalog);

        IntegrationGenerationResult result = await service.GenerateAsync(
            project.Root, [CodexLlmProvider.Codex], "en", "fr", "0.31.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationGenerationDiagnostic.LanguageUnsupported, result.Diagnostic);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MalformedDocumentIsReportedButDoesNotBlockGenerationFromValidOnes()
    {
        using TempForgeProject project = new();
        project.WriteRule("broken.md", "No frontmatter here.");
        project.WriteRule("ok.md", Rule("ok-rule", "OK rule", "Fine."));
        IntegrationGenerationService service = new(Generators, Catalog);

        IntegrationGenerationResult result = await service.GenerateAsync(
            project.Root, [CodexLlmProvider.Codex], "en", "en", "0.31.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationGenerationDiagnostic.None, result.Diagnostic);
        ForgeDocumentError error = Assert.Single(result.DocumentErrors);
        Assert.Equal("rules/broken.md", error.RelativePath);
        GeneratedArtifact artifact = Assert.Single(result.Artifacts);
        Assert.Contains("OK rule", artifact.Content, StringComparison.Ordinal);
    }

    private static string Rule(string id, string title, string body) =>
        $"---\nschema_version: \"1.0.0\"\nid: {id}\ntitle: {title}\nscope: project\n---\n{body}";
}
