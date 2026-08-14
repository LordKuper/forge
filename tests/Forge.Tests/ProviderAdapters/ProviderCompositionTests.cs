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
}
