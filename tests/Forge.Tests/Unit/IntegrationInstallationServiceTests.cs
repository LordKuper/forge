using Forge.Application;
using Forge.Compiler;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class IntegrationInstallationServiceTests
{
    private const string ArtifactName = "TEST_INTEGRATION.md";
    private static readonly ResourceLocalizationCatalog Catalog = new();
    private static readonly ProviderId TestProviderId = new("test-provider");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallWritesAMissingArtifact()
    {
        using TempForgeProject project = new();
        project.WriteRule("rule.md", Rule("rule", "Rule", "Body."));
        IntegrationInstallationService service = CreateService();

        IntegrationWriteResult result = await service.InstallAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        IntegrationArtifactResult artifact = Assert.Single(result.Artifacts);
        Assert.Equal(IntegrationArtifactOutcome.Written, artifact.Outcome);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
        Assert.True(File.Exists(Path.Combine(project.Root, ArtifactName)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallIsIdempotentForACurrentArtifact()
    {
        using TempForgeProject project = new();
        project.WriteRule("rule.md", Rule("rule", "Rule", "Body."));
        IntegrationInstallationService service = CreateService();
        await service.InstallAsync(project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);
        string firstWrite = await File.ReadAllTextAsync(
            Path.Combine(project.Root, ArtifactName), TestContext.Current.CancellationToken);

        IntegrationWriteResult result = await service.InstallAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationArtifactOutcome.Unchanged, Assert.Single(result.Artifacts).Outcome);
        Assert.Equal(
            firstWrite,
            await File.ReadAllTextAsync(Path.Combine(project.Root, ArtifactName), TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallRegeneratesAStaleArtifact()
    {
        using TempForgeProject project = new();
        project.WriteRule("rule.md", Rule("rule", "Rule", "Version one."));
        IntegrationInstallationService service = CreateService();
        await service.InstallAsync(project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);
        project.WriteRule("rule.md", Rule("rule", "Rule", "Version two."));

        IntegrationInspectionResult inspection = await service.InspectAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);
        Assert.Equal(IntegrationArtifactState.Stale, Assert.Single(inspection.Artifacts).State);

        IntegrationWriteResult result = await service.InstallAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);
        Assert.Equal(IntegrationArtifactOutcome.Written, Assert.Single(result.Artifacts).Outcome);
        Assert.Contains(
            "Version two.",
            await File.ReadAllTextAsync(Path.Combine(project.Root, ArtifactName), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallRefusesAForeignFileAndReportsPartialRefusal()
    {
        using TempForgeProject project = new();
        string targetPath = Path.Combine(project.Root, ArtifactName);
        await File.WriteAllTextAsync(targetPath, "Hand-written, not Forge's.", TestContext.Current.CancellationToken);
        IntegrationInstallationService service = CreateService();

        IntegrationWriteResult result = await service.InstallAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationArtifactOutcome.Refused, Assert.Single(result.Artifacts).Outcome);
        Assert.Equal(DiagnosticCodes.IntegrationPartiallyRefused, result.DiagnosticCode);
        Assert.Equal("Hand-written, not Forge's.", await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveDeletesAForgeOwnedArtifact()
    {
        using TempForgeProject project = new();
        project.WriteRule("rule.md", Rule("rule", "Rule", "Body."));
        IntegrationInstallationService service = CreateService();
        await service.InstallAsync(project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        IntegrationWriteResult result = await service.RemoveAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationArtifactOutcome.Removed, Assert.Single(result.Artifacts).Outcome);
        Assert.False(File.Exists(Path.Combine(project.Root, ArtifactName)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveIsIdempotentForAMissingArtifact()
    {
        using TempForgeProject project = new();
        project.WriteRule("rule.md", Rule("rule", "Rule", "Body."));
        IntegrationInstallationService service = CreateService();

        IntegrationWriteResult result = await service.RemoveAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationArtifactOutcome.Unchanged, Assert.Single(result.Artifacts).Outcome);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveRefusesAForeignFileWithoutDeletingIt()
    {
        using TempForgeProject project = new();
        string targetPath = Path.Combine(project.Root, ArtifactName);
        await File.WriteAllTextAsync(targetPath, "Hand-written, not Forge's.", TestContext.Current.CancellationToken);
        IntegrationInstallationService service = CreateService();

        IntegrationWriteResult result = await service.RemoveAsync(
            project.Root, [TestProviderId], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationArtifactOutcome.Refused, Assert.Single(result.Artifacts).Outcome);
        Assert.Equal(DiagnosticCodes.IntegrationPartiallyRefused, result.DiagnosticCode);
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InspectPropagatesAnUnsupportedLanguageWithoutWritingAnything()
    {
        using TempForgeProject project = new();
        IntegrationInstallationService service = CreateService();

        IntegrationInspectionResult result = await service.InspectAsync(
            project.Root, [TestProviderId], "en", "fr", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Empty(result.Artifacts);
        Assert.Equal(DiagnosticCodes.IntegrationLanguageUnsupported, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoEnabledProviderProducesNoArtifacts()
    {
        using TempForgeProject project = new();
        IntegrationInstallationService service = CreateService();

        IntegrationInspectionResult result = await service.InspectAsync(
            project.Root, [], "en", "en", "0.31.0", TestContext.Current.CancellationToken);

        Assert.Empty(result.Artifacts);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    private static IntegrationInstallationService CreateService() =>
        new(new IntegrationGenerationService([new FakeIntegrationGenerator()], Catalog));

    private static string Rule(string id, string title, string body) =>
        $"---\nschema_version: \"1.0.0\"\nid: {id}\ntitle: {title}\nscope: project\n---\n{body}";

    /// <summary>A minimal single-provider generator so this test file stays in the portable
    /// `Unit/` group instead of depending on the real, Windows-only Claude/Codex adapters.</summary>
    private sealed class FakeIntegrationGenerator : IProviderIntegrationGenerator
    {
        public ProviderId ProviderId { get; } = TestProviderId;

        public GeneratedArtifact Generate(CanonicalIntegrationSource source) =>
            new(
                ProviderId,
                ArtifactName,
                source.Content,
                "text/markdown",
                "agent_facing",
                source.Language,
                source.SourceDigest,
                source.PolicySnapshotHash,
                source.GeneratorVersion);
    }
}
