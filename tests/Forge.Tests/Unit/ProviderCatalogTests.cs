using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderCatalogTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ConstructionRejectsTwoProvidersRegisteredForTheSameId()
    {
        FakeLlmProvider first = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider second = new(new ProviderId("codex"), ProviderState.Ready, "2.0.0");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new ProviderCatalog([first, second]));
        Assert.Contains("codex", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProvidersPreservesCompositionOrder()
    {
        FakeLlmProvider first = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider second = new(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0");
        ProviderCatalog catalog = new([first, second]);

        Assert.Equal(["codex", "claude_code"], catalog.Providers.Select(provider => provider.Id.Value));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ContainsAndTryGetReflectRegisteredIds()
    {
        FakeLlmProvider codex = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        ProviderCatalog catalog = new([codex]);

        Assert.True(catalog.Contains(new ProviderId("codex")));
        Assert.False(catalog.Contains(new ProviderId("claude_code")));
        Assert.True(catalog.TryGet(new ProviderId("codex"), out ILlmProvider? found));
        Assert.Same(codex, found);
        Assert.False(catalog.TryGet(new ProviderId("claude_code"), out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveEnabledSelectsEveryRegisteredProviderWhenTheListIsOmitted()
    {
        FakeLlmProvider codex = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider claude = new(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0");
        ProviderCatalog catalog = new([codex, claude]);

        IReadOnlyList<ILlmProvider> resolved = catalog.ResolveEnabled(null);

        Assert.Equal(["codex", "claude_code"], resolved.Select(provider => provider.Id.Value));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveEnabledSelectsNoProviderWhenTheListIsExplicitlyEmpty()
    {
        FakeLlmProvider codex = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        ProviderCatalog catalog = new([codex]);

        Assert.Empty(catalog.ResolveEnabled([]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveEnabledOrdersByTheGivenListNotCompositionOrder()
    {
        FakeLlmProvider codex = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider claude = new(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0");
        ProviderCatalog catalog = new([codex, claude]);

        IReadOnlyList<ILlmProvider> resolved = catalog.ResolveEnabled(["claude_code", "codex"]);

        Assert.Equal(["claude_code", "codex"], resolved.Select(provider => provider.Id.Value));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveEnabledDropsAnIdWithNoMatchingRegistrationInsteadOfThrowing()
    {
        FakeLlmProvider codex = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        ProviderCatalog catalog = new([codex]);

        IReadOnlyList<ILlmProvider> resolved = catalog.ResolveEnabled(["codex", "discontinued-provider"]);

        Assert.Equal(["codex"], resolved.Select(provider => provider.Id.Value));
    }
}
