using System.Text.Json;
using Forge.Application;
using Forge.Providers;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.UnitTests;

public sealed class ProviderHealthProjectorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void EveryDiscoveredProviderProjectsAsRegisteredAndEnabledWithNoUpdateOrAuthenticationDetermined()
    {
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0"),
            new(new ProviderId("claude_code"), ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
        ]);

        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status, CatalogFor(status));

        ProviderHealthEntry codex = Assert.Single(entries, entry => entry.Id == "codex");
        Assert.True(codex.Registered);
        Assert.True(codex.Enabled);
        Assert.Equal(ProviderState.Ready, codex.State);
        Assert.Equal("0.146.0", codex.Version);
        Assert.Null(codex.UpdateAvailable);
        Assert.Null(codex.Authentication);
        Assert.Equal(ProviderDiagnosticCodes.None, codex.DiagnosticCode);

        ProviderHealthEntry claude = Assert.Single(entries, entry => entry.Id == "claude_code");
        Assert.Equal(ProviderState.Missing, claude.State);
        Assert.Equal(ProviderDiagnosticCodes.Missing, claude.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProjectsUpdateAvailabilityAndAuthenticationStateWhenBothAreDetermined()
    {
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0") with
            {
                UpdateAvailable = true,
                Authentication = ProviderAuthenticationStatus.Required,
            },
        ]);

        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status, CatalogFor(status));

        ProviderHealthEntry codex = Assert.Single(entries);
        Assert.True(codex.UpdateAvailable);
        Assert.Equal(ProviderHealthAuthentication.Required, codex.Authentication);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ARegisteredProviderAbsentFromTheStatusProjectsAsADisabledNeverProbedEntry()
    {
        ProviderToolchainStatus status = new([ProviderStatus.Ready(new ProviderId("codex"), "0.146.0")]);
        ProviderCatalog catalog = new(
        [
            new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
            new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
        ]);

        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status, catalog);

        // ADR 0008: a disabled provider "is never discovered, installed, updated, authenticated,
        // or executed" — it never reaches ProviderToolchainManager.CheckAsync's status at all, so
        // the projector must synthesize its entry purely from the catalog, never probing it.
        ProviderHealthEntry claude = Assert.Single(entries, entry => entry.Id == "claude_code");
        Assert.True(claude.Registered);
        Assert.False(claude.Enabled);
        Assert.Null(claude.State);
        Assert.Null(claude.Version);
        Assert.Null(claude.UpdateAvailable);
        Assert.Null(claude.Authentication);
        Assert.Equal(ProviderDiagnosticCodes.Disabled, claude.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnabledEntriesKeepTheStatusOrderAndDisabledEntriesAreAppendedInCatalogOrder()
    {
        // The user's enablement order ("claude_code" before "codex") differs from catalog
        // composition order — enabled rows must preserve it; only the trailing disabled rows
        // fall back to catalog order.
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("claude_code"), "2.1.221"),
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0"),
        ]);
        ProviderCatalog catalog = new(
        [
            new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
            new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
            new FakeLlmProvider(new ProviderId("third"), ProviderState.Ready, "1.0.0"),
        ]);

        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status, catalog);

        Assert.Equal(["claude_code", "codex", "third"], entries.Select(entry => entry.Id));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TheProjectedShapeSatisfiesTheVersionedProviderHealthContract()
    {
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0"),
            new(new ProviderId("claude_code"), ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
        ]);

        AssertSatisfiesContract(status, CatalogFor(status));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ADisabledEntrySatisfiesTheVersionedProviderHealthContract()
    {
        ProviderToolchainStatus status = new([]);
        ProviderCatalog catalog = new([new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0")]);

        AssertSatisfiesContract(status, catalog);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true, ProviderHealthAuthentication.Ready)]
    [InlineData(false, ProviderHealthAuthentication.Required)]
    [InlineData(null, ProviderHealthAuthentication.CheckFailed)]
    public void APopulatedUpdateAvailabilityAndEveryAuthenticationValueSatisfiesTheVersionedContract(
        bool? updateAvailable,
        ProviderHealthAuthentication authentication)
    {
        ProviderAuthenticationStatus authenticationStatus = authentication switch
        {
            ProviderHealthAuthentication.Ready => ProviderAuthenticationStatus.Ready,
            ProviderHealthAuthentication.Required => ProviderAuthenticationStatus.Required,
            _ => ProviderAuthenticationStatus.CheckFailed,
        };
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0") with
            {
                UpdateAvailable = updateAvailable,
                Authentication = authenticationStatus,
            },
        ]);

        AssertSatisfiesContract(status, CatalogFor(status));
    }

    /// <summary>A catalog containing exactly the providers already present in <paramref name="status"/>,
    /// so projecting against it synthesizes no additional disabled entries — the pre-P8.83-88
    /// baseline behavior these tests otherwise exercise unchanged.</summary>
    private static ProviderCatalog CatalogFor(ProviderToolchainStatus status) => new(
        [.. status.Providers.Select(provider => new FakeLlmProvider(provider.Id, provider.State, provider.Version))]);

    private static void AssertSatisfiesContract(ProviderToolchainStatus status, ProviderCatalog catalog)
    {
        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status, catalog);
        string json = StatusJson.Serialize(entries);
        using JsonDocument instance = JsonDocument.Parse(
            $$"""{"schema_version":"1.1.0","providers":{{json}}}""");

        EvaluationResults result = ContractSchemas.Load("provider-health").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }
}
