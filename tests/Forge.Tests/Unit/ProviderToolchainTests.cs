using Forge.Application;
using Forge.Cli;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderToolchainTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsMissingWithoutAnyCurrentPointer()
    {
        using TestEnvironment environment = new();
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(0, "0.146.0", string.Empty));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Missing, status.State);
        Assert.Equal(ProviderDiagnosticCodes.Missing, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsReadyWhenThePinnedExecutableRunsSuccessfully()
    {
        using TestEnvironment environment = new();
        WriteCurrentPointer(environment, "codex", "0.146.0", "codex.exe");
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(0, "0.146.0", string.Empty));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenThePinnedExecutableExitsNonZero()
    {
        using TestEnvironment environment = new();
        WriteCurrentPointer(environment, "codex", "0.146.0", "codex.exe");
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(1, string.Empty, "boom"));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheCurrentPointerHasNoExecutable()
    {
        using TestEnvironment environment = new();
        string root = Path.Combine(environment.LocalApplicationData, "Forge", "providers", "codex");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "current.json"), """{"Version":"0.146.0"}""");
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(0, string.Empty, string.Empty));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ManagerOnlyInstallsProvidersThatAreNotReady()
    {
        FakeStrategy ready = new(ProviderKind.Codex, ProviderState.Ready, "1.0.0");
        FakeStrategy missing = new(ProviderKind.ClaudeCode, ProviderState.Missing, null);
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
        FakeStrategy missing = new(ProviderKind.Codex, ProviderState.Missing, null);
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
            new(ProviderKind.Codex, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed),
            ProviderStatus.Ready(ProviderKind.ClaudeCode, "2.1.221"),
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

    private static CodexProviderStrategy CreateCodexStrategy(
        TestEnvironment environment,
        Func<ProcessRequest, ProcessResult> respond) =>
        new(
            new GitHubProviderInstaller(new StubReleaseClient(null), new HttpClient(), environment),
            new StubProcessRunner(respond));

    private static void WriteCurrentPointer(
        TestEnvironment environment,
        string directoryName,
        string version,
        string executableName)
    {
        string root = Path.Combine(environment.LocalApplicationData, "Forge", "providers", directoryName);
        string versionDirectory = Path.Combine(root, "versions", version);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(Path.Combine(versionDirectory, executableName), "stub");
        File.WriteAllText(Path.Combine(root, "current.json"), $$"""{"Version":"{{version}}"}""");
    }

    private sealed class StubReleaseClient(ProviderRelease? release) : IProviderReleaseClient
    {
        public Task<ProviderRelease?> GetLatestAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken) =>
            Task.FromResult(release);
    }

    private sealed class StubProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeStrategy(ProviderKind kind, ProviderState state, string? version) : IProviderStrategy
    {
        public int InstallCalls { get; private set; }

        public ProviderKind Kind => kind;

        public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderStatus(kind, state, version, ProviderDiagnosticCodes.None));

        public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            return Task.FromResult(ProviderStatus.Ready(kind, "1.0.0"));
        }

        public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}
