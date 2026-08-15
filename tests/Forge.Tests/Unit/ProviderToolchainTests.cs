using Forge.Application;
using Forge.Cli;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderToolchainTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ManagerOnlyInstallsProvidersThatAreNotReady()
    {
        FakeLlmProvider ready = new(new ProviderId("ready-provider"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider missing = new(new ProviderId("missing-provider"), ProviderState.Missing, null);
        ProviderToolchainManager manager = CreateManager([ready, missing]);

        ProviderToolchainStatus status = await manager.EnsureReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, ready.InstallCalls);
        Assert.Equal(1, missing.InstallCalls);
        Assert.True(status.Ready);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckNeverInstallsOrUpdates()
    {
        FakeLlmProvider missing = new(new ProviderId("missing-provider"), ProviderState.Missing, null);
        ProviderToolchainManager manager = CreateManager([missing]);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, missing.InstallCalls);
        Assert.False(status.Ready);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OmittedEnablementSelectsEveryRegisteredProviderInCompositionOrder()
    {
        FakeLlmProvider first = new(new ProviderId("first"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider second = new(new ProviderId("second"), ProviderState.Ready, "1.0.0");
        ProviderToolchainManager manager = CreateManager([first, second], enabledIds: null);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], status.Providers.Select(provider => provider.Id.Value));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExplicitEmptyEnablementBlocksEveryProviderWithoutProbingAny()
    {
        FakeLlmProvider provider = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        ProviderToolchainManager manager = CreateManager([provider], enabledIds: []);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(status.Providers);
        Assert.False(status.Ready);
        Assert.Equal(0, provider.DiscoverCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExplicitEnablementOrdersCandidatesByTheUsersFallbackPriorityNotCompositionOrder()
    {
        FakeLlmProvider codex = new(new ProviderId("codex"), ProviderState.Ready, "1.0.0");
        FakeLlmProvider claude = new(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0");
        ProviderToolchainManager manager = CreateManager([codex, claude], enabledIds: ["claude_code", "codex"]);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["claude_code", "codex"], status.Providers.Select(provider => provider.Id.Value));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADisabledProviderIsNeverDiscoveredOrInstalled()
    {
        FakeLlmProvider enabled = new(new ProviderId("codex"), ProviderState.Missing, null);
        FakeLlmProvider disabled = new(new ProviderId("claude_code"), ProviderState.Missing, null);
        ProviderToolchainManager manager = CreateManager([enabled, disabled], enabledIds: ["codex"]);

        ProviderToolchainStatus status =
            await manager.EnsureReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["codex"], status.Providers.Select(provider => provider.Id.Value));
        Assert.Equal(0, disabled.DiscoverCalls);
        Assert.Equal(0, disabled.InstallCalls);
        Assert.Equal(1, enabled.DiscoverCalls);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupBlocksSprintWorkUntilBothProvidersAreReady(bool ready)
    {
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(ready ? FakeProviderToolchainManager.Ready : null));

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        StartupCheck providersCheck = status.Checks.Single(check => check.Id == StartupCheckId.Providers);
        Assert.Equal(ready ? StartupCheckState.Passed : StartupCheckState.Blocked, providersCheck.State);
        Assert.Equal(
            ready ? DiagnosticCodes.None : DiagnosticCodes.ProviderPreflightPending,
            providersCheck.DiagnosticCode);
        if (!ready)
        {
            Assert.False(status.AllowsSprintWork);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartupReportsProviderUpdateFailedWhenRepairIsNeeded()
    {
        ProviderToolchainStatus failed = new([
            new(new ProviderId("codex"), ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed),
            ProviderStatus.Ready(new ProviderId("claude_code"), "2.1.221"),
        ]);
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(failed));

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            DiagnosticCodes.ProviderUpdateFailed,
            status.Checks.Single(check => check.Id == StartupCheckId.Providers).DiagnosticCode);
        Assert.Equal(ExitCodes.Provider, ExitCodes.For(DiagnosticCodes.ProviderUpdateFailed));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AReadyProviderThatRequiresAuthenticationBlocksToolchainReadiness()
    {
        FakeLlmProvider provider = new(
            new ProviderId("codex"),
            ProviderState.Ready,
            "1.0.0",
            authentication: ProviderAuthenticationStatus.Required);
        ProviderToolchainManager manager = CreateManager([provider]);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        // ADR 0008: "every enabled provider must report local authentication readiness" — an
        // otherwise-Ready install still blocks toolchain readiness while authentication is
        // missing, and both the plain and shared diagnostic codes must say so (not "none").
        Assert.False(status.Ready);
        Assert.Equal(ProviderDiagnosticCodes.AuthenticationRequired, status.DiagnosticCode);
        Assert.Equal(DiagnosticCodes.ProviderAuthenticationRequired, status.SharedDiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AReadyProviderWhoseAuthenticationCheckFailedBlocksToolchainReadiness()
    {
        FakeLlmProvider provider = new(
            new ProviderId("codex"),
            ProviderState.Ready,
            "1.0.0",
            authentication: ProviderAuthenticationStatus.CheckFailed);
        ProviderToolchainManager manager = CreateManager([provider]);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Ready);
        Assert.Equal(ProviderDiagnosticCodes.AuthenticationCheckFailed, status.DiagnosticCode);
        Assert.Equal(DiagnosticCodes.ProviderAuthenticationCheckFailed, status.SharedDiagnosticCode);
    }

    private static ProviderToolchainManager CreateManager(
        IEnumerable<ILlmProvider> providers,
        IReadOnlyList<string>? enabledIds = null) =>
        new(new ProviderCatalog(providers), new FakeProviderEnablementSource(enabledIds));
}
