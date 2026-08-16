using Forge.Application;
using Forge.Providers;
using Forge.Providers.Claude;
using Forge.Tests.Support;
using Forge.UnitTests;

namespace Forge.ProviderAdapterTests;

public sealed class ClaudeLlmProviderTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverRejectsAnInstallBelowTheDocumentedMinimumVersion()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        ClaudeLlmProvider provider = CreateProvider(paths, _ => new(0, "1.9.0 (Claude Code)", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.VersionUnsupported, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsUpdateDirectlyWhenANewerVersionIsAvailable()
    {
        using TestPaths paths = new();
        string executable = WriteClaudeExecutable(paths);
        int callCount = 0;
        ClaudeLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                callCount++;
                return callCount switch
                {
                    // 1: the initial local probe establishes the current version to compare.
                    1 => Probe("--version", request, () => new(0, "2.1.220 (Claude Code)", string.Empty)),
                    // 2: the re-probe taken right after the lock is acquired — still the old
                    // version, since no concurrent process updated it first.
                    2 => Probe("--version", request, () => new(0, "2.1.220 (Claude Code)", string.Empty)),
                    // 3: a newer release is available, so the vendor's own update command runs.
                    3 => Probe("update", request, () =>
                    {
                        Assert.Equal(executable, request.FileName);
                        return new(0, string.Empty, string.Empty);
                    }),
                    // 4: the post-update recheck is local-only — never another network release check.
                    _ => Probe("--version", request, () => new(0, "2.1.221 (Claude Code)", string.Empty)),
                };
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(2, 1, 221))));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(4, callCount);
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("2.1.221", status.Version);
        Assert.False(status.UpdateAvailable);

        static ProcessResult Probe(string expectedArgument, ProcessRequest request, Func<ProcessResult> respond)
        {
            Assert.Equal([expectedArgument], request.Arguments);
            return respond();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateSkipsWorkWhenAlreadyOnTheLatestVersion()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        int callCount = 0;
        ClaudeLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                callCount++;
                Assert.Equal(1, callCount);
                return new(0, "2.1.221 (Claude Code)", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(2, 1, 221))));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.False(status.UpdateAvailable);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"authenticated": true}""", ProviderHealthAuthentication.Ready)]
    [InlineData("""{"authenticated": false}""", ProviderHealthAuthentication.Required)]
    [InlineData("""{"isAuthenticated": true}""", ProviderHealthAuthentication.Ready)]
    [InlineData("""{"credentials": {"type": "oauth"}}""", ProviderHealthAuthentication.Ready)]
    [InlineData("""{"credentials": null}""", ProviderHealthAuthentication.CheckFailed)]
    [InlineData("""not json at all""", ProviderHealthAuthentication.CheckFailed)]
    [InlineData("""{"unrecognized": "shape"}""", ProviderHealthAuthentication.CheckFailed)]
    public async Task CheckAuthenticationParsesTheJsonBodyRatherThanTrustingExitCodeAlone(
        string standardOutput,
        ProviderHealthAuthentication expected)
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(["auth", "status", "--json"], request.Arguments);
            // Exit code intentionally disagrees with the body in some cases: the body must decide.
            return new(1, standardOutput, string.Empty);
        });

        ProviderAuthenticationStatus status =
            await provider.CheckAuthenticationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncDisablesTheBackgroundAutoUpdater()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        IReadOnlyDictionary<string, string>? capturedEnvironment = null;
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
        {
            capturedEnvironment = request.EnvironmentVariables;
            return new(0, string.Empty, string.Empty);
        });

        await provider.RunAsync("say hi", "C:\\work", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedEnvironment);
        Assert.Equal("1", capturedEnvironment!["DISABLE_AUTOUPDATER"]);
        Assert.False(capturedEnvironment.ContainsKey("DISABLE_UPDATES"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateDisablesTheAutoUpdaterForProbesButNeverForTheUpdateCommandItself()
    {
        using TestPaths paths = new();
        string executable = WriteClaudeExecutable(paths);
        List<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment)> calls = [];
        ClaudeLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                calls.Add((request.Arguments, request.EnvironmentVariables));
                return request.Arguments is ["--version"]
                    ? new(0, "2.1.221 (Claude Code)", string.Empty)
                    : new(0, string.Empty, string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(2, 1, 222))));

        await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        // Every local `--version` probe disables the vendor's own background updater (it can
        // otherwise fire on any invocation); the actual `update` command never does — ADR 0008:
        // "the variable is not set for an explicit update."
        foreach ((IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment) in calls)
        {
            if (arguments is ["--version"])
            {
                Assert.Equal("1", environment?["DISABLE_AUTOUPDATER"]);
            }
            else
            {
                Assert.Null(environment);
            }
        }

        Assert.Contains(calls, call => call.Arguments is ["update"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncExtractsAssistantTextFromMessageContentBlocksAndIgnoresToolUseBlocks()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        string jsonl = ReadFixture("claude-stream-json.jsonl");
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(
                ["-p", "--output-format", "stream-json", "--verbose", "--", "say hi"],
                request.Arguments);
            return new(0, jsonl, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "say hi",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Events.Count);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[0].Kind);
        Assert.Equal(ProviderEventKind.Message, result.Events[1].Kind);
        Assert.Equal("Hello world", result.Events[1].Text);
        Assert.Equal(ProviderEventKind.Result, result.Events[2].Kind);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncPassesAPromptStartingWithADoubleDashAfterTheEndOfOptionsMarker()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
        {
            int separatorIndex = request.Arguments.ToList().IndexOf("--");
            Assert.True(separatorIndex >= 0 && separatorIndex == request.Arguments.Count - 2);
            Assert.Equal("--dangerously-skip-permissions", request.Arguments[^1]);
            return new(0, string.Empty, string.Empty);
        });

        await provider.RunAsync(
            "--dangerously-skip-permissions",
            "C:\\work",
            TestContext.Current.CancellationToken);
    }

    private static string ReadFixture(string name) => File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "tests", "Forge.Tests", "Unit", "fixtures", "providers", name));

    private static ClaudeLlmProvider CreateProvider(
        TestPaths paths,
        Func<ProcessRequest, ProcessResult> respond,
        IProviderReleaseSource? releaseSource = null) =>
        new(
            paths,
            new StubProcessRunner(respond),
            releaseSource ?? new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            new FakeInstallLock(),
            new FakeClock());

    private static string WriteClaudeExecutable(TestPaths paths)
    {
        string executable = Path.Combine(paths.UserProfile, ".local", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        return executable;
    }

    /// <summary>An isolated, self-cleaning provider root; adapters only read pinned local state.</summary>
    internal sealed class TestPaths : IEnvironmentPaths, IDisposable
    {
        public TestPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), $"forge-claude-provider-tests-{Guid.NewGuid():N}");
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

}
