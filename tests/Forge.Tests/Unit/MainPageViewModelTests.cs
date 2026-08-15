using System.Globalization;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class MainPageViewModelTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RefreshAsyncRendersEveryProviderIncludingARegisteredButDisabledOne()
    {
        ProviderToolchainStatus enabledOnly = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0") with
            {
                Authentication = ProviderAuthenticationStatus.Ready,
            },
        ]);
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(enabledOnly),
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
            ]);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        MainPageSnapshot snapshot = await viewModel.RefreshAsync(null, TestContext.Current.CancellationToken);

        Assert.Contains(
            "codex enabled ready 0.146.0 - ready none",
            snapshot.ProvidersText,
            StringComparison.Ordinal);
        Assert.Contains(
            "claude_code disabled - - - - provider_disabled",
            snapshot.ProvidersText,
            StringComparison.Ordinal);
    }

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), CultureInfo.CurrentUICulture);
}
