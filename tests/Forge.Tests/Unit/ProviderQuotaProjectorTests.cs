using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderQuotaProjectorTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public void EveryEnabledProviderProjectsAsUnknownWithNoFabricatedNumberOrResetTime()
    {
        // ADR 0052: no provider integration in this codebase exposes a verified quota signal, so
        // this is the only availability this projector ever produces today.
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0"),
            ProviderStatus.Ready(new ProviderId("claude_code"), "2.1.221"),
        ]);
        ProviderCatalog catalog = CatalogFor(status);

        IReadOnlyList<ProviderQuotaSnapshot> entries = ProviderQuotaProjector.Project(status, catalog, ObservedAt);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(ProviderQuotaAvailability.Unknown, entry.Availability);
            Assert.Null(entry.RemainingAmount);
            Assert.Null(entry.Unit);
            Assert.Null(entry.ResetAt);
            Assert.Equal(ObservedAt, entry.ObservedAt);
            Assert.Equal(ProviderDiagnosticCodes.QuotaUnknown, entry.DiagnosticCode);
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolvesEachProviderSDefaultModelFromTheCatalog()
    {
        ProviderToolchainStatus status = new([ProviderStatus.Ready(new ProviderId("codex"), "0.146.0")]);
        ProviderCatalog catalog = new([new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0")]);

        IReadOnlyList<ProviderQuotaSnapshot> entries = ProviderQuotaProjector.Project(status, catalog, ObservedAt);

        ProviderQuotaSnapshot codex = Assert.Single(entries);
        Assert.Equal("codex", codex.ProviderId);
        Assert.Equal(new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0").DefaultModel, codex.Model);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ARegisteredProviderAbsentFromTheStatusProjectsAsADisabledNeverProbedUnknownEntry()
    {
        ProviderToolchainStatus status = new([ProviderStatus.Ready(new ProviderId("codex"), "0.146.0")]);
        ProviderCatalog catalog = new(
        [
            new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
            new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
        ]);

        IReadOnlyList<ProviderQuotaSnapshot> entries = ProviderQuotaProjector.Project(status, catalog, ObservedAt);

        ProviderQuotaSnapshot claude = Assert.Single(entries, entry => entry.ProviderId == "claude_code");
        Assert.Equal(ProviderQuotaAvailability.Unknown, claude.Availability);
        Assert.Equal("claude_code", claude.ProviderId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProjectingAnEmptyToolchainAndCatalogProducesNoEntries()
    {
        ProviderToolchainStatus status = new([]);
        ProviderCatalog catalog = new([]);

        IReadOnlyList<ProviderQuotaSnapshot> entries = ProviderQuotaProjector.Project(status, catalog, ObservedAt);

        Assert.Empty(entries);
    }

    private static ProviderCatalog CatalogFor(ProviderToolchainStatus status) => new(
        [.. status.Providers.Select(provider => new FakeLlmProvider(provider.Id, provider.State, provider.Version))]);
}

public sealed class ProviderQuotaAggregationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void AnEmptyListIsWorstCaseUnknown()
    {
        Assert.Equal(ProviderQuotaAvailability.Unknown, ProviderQuotaAggregation.Worst([]));
        Assert.Equal(ProviderDiagnosticCodes.QuotaUnknown, ProviderQuotaAggregation.WorstDiagnosticCode([]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AnUnavailableProviderOutranksAReadyOneInTheAggregate()
    {
        ProviderQuotaSnapshot ready = new("codex", "gpt-5", ProviderQuotaAvailability.Ready, null, null, null, DateTimeOffset.UnixEpoch, "d1");
        ProviderQuotaSnapshot unavailable = new(
            "claude_code", "sonnet", ProviderQuotaAvailability.Unavailable, null, null, null, DateTimeOffset.UnixEpoch, "d2");

        ProviderQuotaAvailability worst = ProviderQuotaAggregation.Worst([ready, unavailable]);
        string diagnosticCode = ProviderQuotaAggregation.WorstDiagnosticCode([ready, unavailable]);

        Assert.Equal(ProviderQuotaAvailability.Unavailable, worst);
        Assert.Equal("d2", diagnosticCode);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(ProviderQuotaAvailability.Unavailable, ProviderQuotaAvailability.Limited, ProviderQuotaAvailability.Unavailable)]
    [InlineData(ProviderQuotaAvailability.Limited, ProviderQuotaAvailability.Stale, ProviderQuotaAvailability.Limited)]
    [InlineData(ProviderQuotaAvailability.Stale, ProviderQuotaAvailability.Unknown, ProviderQuotaAvailability.Stale)]
    [InlineData(ProviderQuotaAvailability.Unknown, ProviderQuotaAvailability.Ready, ProviderQuotaAvailability.Unknown)]
    public void SeverityOrderingIsUnavailableThenLimitedThenStaleThenUnknownThenReady(
        ProviderQuotaAvailability more, ProviderQuotaAvailability less, ProviderQuotaAvailability expectedWorst)
    {
        ProviderQuotaSnapshot a = new("codex", null, more, null, null, null, DateTimeOffset.UnixEpoch, "a");
        ProviderQuotaSnapshot b = new("claude_code", null, less, null, null, null, DateTimeOffset.UnixEpoch, "b");

        Assert.Equal(expectedWorst, ProviderQuotaAggregation.Worst([a, b]));
        Assert.Equal(expectedWorst, ProviderQuotaAggregation.Worst([b, a]));
    }
}
