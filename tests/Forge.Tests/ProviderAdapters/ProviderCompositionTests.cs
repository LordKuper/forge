using Forge.Application;
using Forge.Bootstrap;
using Forge.Providers;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.ProviderAdapterTests;

/// <summary>
/// Every user-facing composition root (CLI, Desktop, Host) must call both
/// <c>AddCodexProvider()</c> and <c>AddClaudeProvider()</c> — a composition root that forgets one
/// silently ships with an empty <see cref="ProviderCatalog"/>, which makes every
/// <c>providers.enabled</c> write fail with "unknown provider id" for a real, shipped provider.
/// </summary>
public sealed class ProviderCompositionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ACompositionRootThatCallsBothProviderRegistrationsPopulatesTheCatalog()
    {
        using TestEnvironment environment = new();
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(environment);
        services.AddCodexProvider();
        services.AddClaudeProvider();
        using ServiceProvider provider = services.BuildServiceProvider();

        ProviderCatalog catalog = provider.GetRequiredService<ProviderCatalog>();

        Assert.True(catalog.Contains(new ProviderId("codex")));
        Assert.True(catalog.Contains(new ProviderId("claude_code")));
    }

    /// <summary>Slice S5: <see cref="ProviderQuotaAvailability.Unknown"/> is documented as the
    /// TERMINAL quota reading for both shipped providers, so a later surface can render "no limit
    /// data available" as a final answer rather than a placeholder. <c>ProviderQuotaProjectorTests</c>
    /// only proves that over <c>FakeLlmProvider</c>; this proves it over the REAL
    /// <c>Forge.Providers.Codex.Windows</c>/<c>Forge.Providers.Claude.Windows</c> adapters composed
    /// exactly as a shipping composition root builds them -- neither adapter contributes any quota
    /// signal, so no snapshot can carry a fabricated amount, unit, or reset time, and none can report
    /// an availability other than the terminal one (ADR 0052's finding, re-confirmed by ADR
    /// 0061).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void QuotaProjectsAsTerminallyUnknownForBothRealProviderAdapters()
    {
        using TestEnvironment environment = new();
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(environment);
        services.AddCodexProvider();
        services.AddClaudeProvider();
        using ServiceProvider provider = services.BuildServiceProvider();
        ProviderCatalog catalog = provider.GetRequiredService<ProviderCatalog>();
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.149.1"),
            ProviderStatus.Ready(new ProviderId("claude_code"), "2.1.233"),
        ]);

        IReadOnlyList<ProviderQuotaSnapshot> entries =
            ProviderQuotaProjector.Project(status, catalog, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(ProviderQuotaAvailability.Unknown, entry.Availability);
            Assert.Null(entry.RemainingAmount);
            Assert.Null(entry.Unit);
            Assert.Null(entry.ResetAt);
            Assert.Equal(ProviderDiagnosticCodes.QuotaUnknown, entry.DiagnosticCode);
        });
    }
}
