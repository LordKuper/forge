using Forge.Application;
using Forge.Providers;
using Forge.Providers.Codex;
using Forge.UnitTests;

namespace Forge.ProviderAdapterTests;

public sealed class CodexLlmProviderTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsMissingWithoutTheVendorExecutable()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "0.146.0", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Missing, status.State);
        Assert.Equal(ProviderDiagnosticCodes.Missing, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsReadyWhenThePinnedExecutableRunsSuccessfully()
    {
        using TestPaths paths = new();
        string executable = WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(executable, request.FileName);
            Assert.Equal(["--version"], request.Arguments);
            return new(0, "codex-cli 0.146.0", string.Empty);
        });

        ProviderStatus status = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenThePinnedExecutableExitsNonZero()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, "boom"));

        ProviderStatus status = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheVersionOutputHasNoParsableVersion()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "not a version", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsTheNativeInstallerWhenMissing()
    {
        using TestPaths paths = new();
        bool ranInstaller = false;
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            if (!ranInstaller)
            {
                Assert.Equal("powershell.exe", Path.GetFileName(request.FileName));
                Assert.True(Path.IsPathFullyQualified(request.FileName), "PowerShell must be launched by full path.");
                Assert.Contains("chatgpt.com/codex/install.ps1", request.Arguments[^1], StringComparison.Ordinal);
                Assert.Contains("CODEX_NON_INTERACTIVE", request.Arguments[^1], StringComparison.Ordinal);
                ranInstaller = true;
                // The real installer would have created the executable; the stub does the same.
                WriteCodexExecutable(paths);
                return new(0, string.Empty, string.Empty);
            }

            return new(0, "0.146.0", string.Empty);
        });

        ProviderStatus status = await provider.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.True(ranInstaller);
        Assert.Equal(ProviderState.Ready, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateFailsWhenTheInstallerExitsNonZero()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, "install failed"));

        ProviderStatus status = await provider.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverPropagatesAnAlreadyCancelledTokenRatherThanReportingFailed()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "0.146.0", string.Empty));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.DiscoverAsync(cancelled.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheVersionProbeExceedsItsTimeout()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = new(paths, new HangingProcessRunner(), versionProbeTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsFailedWhenTheInstallerExceedsItsTimeout()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = new(paths, new HangingProcessRunner(), installTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await provider.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncParsesDocumentedEventTypesWithoutShellInvocation()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string jsonl = ReadFixture("codex-exec-json.jsonl");
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal("codex.exe", Path.GetFileName(request.FileName));
            Assert.Equal(["exec", "--json", "--", "list open bugs"], request.Arguments);
            return new(0, jsonl, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "list open bugs",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Events.Count);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[0].Kind);
        Assert.Equal(ProviderEventKind.ToolUse, result.Events[2].Kind);
        Assert.Equal(ProviderEventKind.Result, result.Events[3].Kind);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncReturnsMalformedOutputWhenAnEventLineIsNotJson()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "not-json", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncClassifiesANonZeroExitAsAuthenticationAndRedactsTheDetail()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            1,
            string.Empty,
            "Error: not logged in. api_key=sk-live-abcdef1234567890"));

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.Authentication, result.Failure);
        Assert.DoesNotContain("sk-live-abcdef1234567890", result.Detail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("HTTP 429 Too Many Requests", ProviderFailureKind.RateLimited)]
    [InlineData("You have exceeded your monthly quota", ProviderFailureKind.QuotaExceeded)]
    [InlineData("Request blocked by content filter policy", ProviderFailureKind.Policy)]
    [InlineData("connect ECONNRESET", ProviderFailureKind.Transient)]
    [InlineData("something unexpected happened", ProviderFailureKind.Unknown)]
    public async Task RunAsyncClassifiesKnownFailureTextIntoStableCategories(
        string stderr,
        ProviderFailureKind expected)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, stderr));

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncReturnsNotReadyWithoutSpawningAProcessWhenNotInstalled()
    {
        bool spawned = false;
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ =>
        {
            spawned = true;
            return new(0, string.Empty, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.NotReady, result.Failure);
        Assert.False(spawned);
    }

    private static string ReadFixture(string name) => File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "tests", "Forge.Tests", "Unit", "fixtures", "providers", name));

    private static CodexLlmProvider CreateProvider(TestPaths paths, Func<ProcessRequest, ProcessResult> respond) =>
        new(paths, new StubProcessRunner(respond));

    private static string WriteCodexExecutable(TestPaths paths)
    {
        string executable = Path.Combine(
            paths.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        return executable;
    }

    /// <summary>An isolated, self-cleaning provider root; adapters only read pinned local state.</summary>
    internal sealed class TestPaths : IEnvironmentPaths, IDisposable
    {
        public TestPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), $"forge-codex-provider-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string LocalApplicationData => Path.Combine(Root, "local");

        public string UserProfile => Path.Combine(Root, "userprofile");

        public string CurrentDirectory => Root;

        public string InstanceId => "forge-test";

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
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
}
