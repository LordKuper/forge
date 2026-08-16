using System.Globalization;
using Forge.Application;
using Forge.Configuration;
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverAsyncRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.RecoverAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.RecoverStartupCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverAsyncResolvesMutationsUsingTheSuppliedProjectRoot()
    {
        // Regression coverage for the same class of bug PR #37 fixed on the CLI side: the
        // resolver must see the exact root this call was given, not one fixed elsewhere.
        using TestEnvironment environment = new();
        string otherRoot = Path.Combine(Path.GetTempPath(), $"forge-other-{Guid.NewGuid():N}");
        FakeForgeMutations mutations = new();
        string? capturedRoot = "unset";
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (root, _) =>
            {
                capturedRoot = root;
                return Task.FromResult<IForgeMutations>(mutations);
            });

        await viewModel.RecoverAsync(otherRoot, true, TestContext.Current.CancellationToken);

        Assert.Equal(otherRoot, capturedRoot);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetConfigurationAsyncRoutesProjectScopeThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.user_facing",
            "ru",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.SetConfigurationCalls);
        Assert.Equal(ConfigurationScope.Project, mutations.LastScope);
        // Never actually written locally — the fake never touches durable state, and a real
        // ForgeApplication call would have overwritten it to "ru" (proving the write really left
        // this view model instead of landing here).
        ConfigurationView project = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken);
        EffectiveConfigurationValue value = Assert.Single(
            project.Values,
            item => item.Key == "artifacts.language.user_facing");
        Assert.Equal("\"en\"", value.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetConfigurationAsyncNeverRoutesUserScopeThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "interaction.confirm_destructive",
            "false",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SetConfigurationCalls);
        ConfigurationView user = await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            user.Values,
            value => value.Key == "interaction.confirm_destructive" && value.Value.GetBoolean() == false);
    }

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), CultureInfo.CurrentUICulture);
}
