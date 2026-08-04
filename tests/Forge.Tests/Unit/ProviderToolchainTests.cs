using Forge.Application;
using Forge.Cli;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderToolchainTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsMissingWithoutTheVendorExecutable()
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
        string executable = WriteCodexExecutable(environment);
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, request =>
        {
            Assert.Equal(executable, request.FileName);
            Assert.Equal(["--version"], request.Arguments);
            return new(0, "codex-cli 0.146.0", string.Empty);
        });

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenThePinnedExecutableExitsNonZero()
    {
        using TestEnvironment environment = new();
        WriteCodexExecutable(environment);
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(1, string.Empty, "boom"));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheVersionOutputHasNoParsableVersion()
    {
        using TestEnvironment environment = new();
        WriteCodexExecutable(environment);
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(0, "not a version", string.Empty));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverRejectsAClaudeInstallBelowTheDocumentedMinimumVersion()
    {
        using TestEnvironment environment = new();
        string executable = Path.Combine(environment.UserProfile, ".local", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        ClaudeCodeProviderStrategy strategy = new(
            environment,
            new StubProcessRunner(_ => new(0, "1.9.0 (Claude Code)", string.Empty)));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.VersionUnsupported, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsTheNativeInstallerWhenMissing()
    {
        using TestEnvironment environment = new();
        bool ranInstaller = false;
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, request =>
        {
            if (!ranInstaller)
            {
                Assert.Equal("powershell.exe", Path.GetFileName(request.FileName));
                Assert.True(Path.IsPathFullyQualified(request.FileName), "PowerShell must be launched by full path.");
                Assert.Contains("chatgpt.com/codex/install.ps1", request.Arguments[^1], StringComparison.Ordinal);
                Assert.Contains("CODEX_NON_INTERACTIVE", request.Arguments[^1], StringComparison.Ordinal);
                ranInstaller = true;
                // The real installer would have created the executable; the stub does the same.
                WriteCodexExecutable(environment);
                return new(0, string.Empty, string.Empty);
            }

            return new(0, "0.146.0", string.Empty);
        });

        ProviderStatus status = await strategy.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.True(ranInstaller);
        Assert.Equal(ProviderState.Ready, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsClaudeUpdateDirectlyWhenAlreadyInstalled()
    {
        using TestEnvironment environment = new();
        string executable = Path.Combine(environment.UserProfile, ".local", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        bool ranUpdate = false;
        ClaudeCodeProviderStrategy strategy = new(
            environment,
            new StubProcessRunner(request =>
            {
                Assert.Equal(executable, request.FileName);
                if (!ranUpdate)
                {
                    Assert.Equal(["update"], request.Arguments);
                    ranUpdate = true;
                    return new(0, string.Empty, string.Empty);
                }

                Assert.Equal(["--version"], request.Arguments);
                return new(0, "2.1.221 (Claude Code)", string.Empty);
            }));

        ProviderStatus status = await strategy.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.True(ranUpdate);
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("2.1.221", status.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateFailsWhenTheInstallerExitsNonZero()
    {
        using TestEnvironment environment = new();
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(1, string.Empty, "install failed"));

        ProviderStatus status = await strategy.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverPropagatesAnAlreadyCancelledTokenRatherThanReportingFailed()
    {
        using TestEnvironment environment = new();
        WriteCodexExecutable(environment);
        CodexProviderStrategy strategy = CreateCodexStrategy(environment, _ => new(0, "0.146.0", string.Empty));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => strategy.DiscoverAsync(cancelled.Token));
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
    public async Task DiscoverReportsFailedWhenTheVersionProbeExceedsItsTimeout()
    {
        using TestEnvironment environment = new();
        WriteCodexExecutable(environment);
        CodexProviderStrategy strategy = new(
            environment,
            new HangingProcessRunner(),
            versionProbeTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await strategy.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsFailedWhenTheInstallerExceedsItsTimeout()
    {
        using TestEnvironment environment = new();
        CodexProviderStrategy strategy = new(
            environment,
            new HangingProcessRunner(),
            installTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await strategy.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
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
        new(environment, new StubProcessRunner(respond));

    private static string WriteCodexExecutable(TestEnvironment environment)
    {
        string executable = Path.Combine(
            environment.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        return executable;
    }

    private sealed class StubProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// Never completes on its own, mirroring how the real <c>ProcessRunner</c> behaves against a
    /// hung child process: it only ends when the caller's token is cancelled.
    /// </summary>
    private sealed class HangingProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable: Task.Delay(Infinite) only returns via cancellation.");
        }
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
