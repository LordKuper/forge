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
        ProviderToolchainManager manager = new([ready, missing]);

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
        ProviderToolchainManager manager = new([missing]);

        ProviderToolchainStatus status = await manager.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, missing.InstallCalls);
        Assert.False(status.Ready);
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

    private sealed class FakeLlmProvider(ProviderId id, ProviderState state, string? version) : ILlmProvider
    {
        public int InstallCalls { get; private set; }

        public ProviderId Id => id;

        public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderStatus(id, state, version, ProviderDiagnosticCodes.None));

        public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            return Task.FromResult(ProviderStatus.Ready(id, "1.0.0"));
        }

        public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<ProviderRunResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only exercises discovery/install orchestration.");
    }
}
