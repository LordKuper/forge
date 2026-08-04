using Forge.Application;
using Forge.Providers;

namespace Forge.UnitTests;

public sealed class ProviderExecutionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CodexAdapterParsesDocumentedEventTypesWithoutShellInvocation()
    {
        string jsonl = ReadFixture("codex-exec-json.jsonl");
        StubProcessRunner runner = new(request =>
        {
            Assert.Equal("codex.exe", Path.GetFileName(request.FileName));
            Assert.Equal(["exec", "--json", "list open bugs"], request.Arguments);
            return new(0, jsonl, string.Empty);
        });
        CodexProviderStrategy strategy = ReadyCodexStrategy();
        CodexProviderAdapter adapter = new(strategy, runner);

        ProviderRunResult result = await adapter.RunAsync(
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
    public async Task ClaudeAdapterExtractsAssistantTextFromMessageContentBlocks()
    {
        string jsonl = ReadFixture("claude-stream-json.jsonl");
        StubProcessRunner runner = new(request =>
        {
            Assert.Equal(["-p", "say hi", "--output-format", "stream-json"], request.Arguments);
            return new(0, jsonl, string.Empty);
        });
        ClaudeCodeProviderStrategy strategy = ReadyClaudeStrategy();
        ClaudeCodeProviderAdapter adapter = new(strategy, runner);

        ProviderRunResult result = await adapter.RunAsync(
            "say hi",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ProviderEventKind.Message, result.Events[0].Kind);
        Assert.Equal("Hello world", result.Events[0].Text);
        Assert.Equal(ProviderEventKind.ToolUse, result.Events[1].Kind);
        Assert.Equal(ProviderEventKind.Result, result.Events[2].Kind);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunReturnsMalformedOutputWhenAnEventLineIsNotJson()
    {
        StubProcessRunner runner = new(_ => new(0, "not-json", string.Empty));
        CodexProviderAdapter adapter = new(ReadyCodexStrategy(), runner);

        ProviderRunResult result = await adapter.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunClassifiesANonZeroExitAsAuthenticationAndRedactsTheDetail()
    {
        StubProcessRunner runner = new(_ => new(
            1,
            string.Empty,
            "Error: not logged in. api_key=sk-live-abcdef1234567890"));
        CodexProviderAdapter adapter = new(ReadyCodexStrategy(), runner);

        ProviderRunResult result = await adapter.RunAsync(
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
    public async Task RunClassifiesKnownFailureTextIntoStableCategories(string stderr, ProviderFailureKind expected)
    {
        StubProcessRunner runner = new(_ => new(1, string.Empty, stderr));
        CodexProviderAdapter adapter = new(ReadyCodexStrategy(), runner);

        ProviderRunResult result = await adapter.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunReturnsNotReadyWithoutSpawningAProcessWhenNoVersionIsInstalled()
    {
        bool spawned = false;
        StubProcessRunner runner = new(_ =>
        {
            spawned = true;
            return new(0, string.Empty, string.Empty);
        });
        CodexProviderStrategy strategy = new(
            new GitHubProviderInstaller(new NullReleaseClient(), new HttpClient(), new TempPaths()),
            new StubProcessRunner(_ => new(0, string.Empty, string.Empty)));
        CodexProviderAdapter adapter = new(strategy, runner);

        ProviderRunResult result = await adapter.RunAsync(
            "prompt",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.NotReady, result.Failure);
        Assert.False(spawned);
    }

    private static string ReadFixture(string name) => File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "tests", "Forge.Tests", "Unit", "fixtures", "providers", name));

    private static CodexProviderStrategy ReadyCodexStrategy()
    {
        TempPaths paths = new();
        WriteCurrentPointer(paths, "codex", "0.146.0", "codex.exe");
        return new(
            new GitHubProviderInstaller(new NullReleaseClient(), new HttpClient(), paths),
            new StubProcessRunner(_ => new(0, "0.146.0", string.Empty)));
    }

    private static ClaudeCodeProviderStrategy ReadyClaudeStrategy()
    {
        TempPaths paths = new();
        WriteCurrentPointer(paths, "claude-code", "2.1.221", "claude.exe");
        return new(
            new GitHubProviderInstaller(new NullReleaseClient(), new HttpClient(), paths),
            new StubProcessRunner(_ => new(0, "2.1.221", string.Empty)));
    }

    private static void WriteCurrentPointer(TempPaths paths, string directoryName, string version, string executableName)
    {
        string root = Path.Combine(paths.LocalApplicationData, "Forge", "providers", directoryName);
        string versionDirectory = Path.Combine(root, "versions", version);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(Path.Combine(versionDirectory, executableName), "stub");
        File.WriteAllText(Path.Combine(root, "current.json"), $$"""{"Version":"{{version}}"}""");
    }

    /// <summary>An isolated, self-cleaning provider root; adapters only read pinned local state.</summary>
    private sealed class TempPaths : IEnvironmentPaths, IDisposable
    {
        public TempPaths()
        {
            LocalApplicationData = Path.Combine(Path.GetTempPath(), $"forge-provider-exec-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(LocalApplicationData);
        }

        public string LocalApplicationData { get; }

        public string CurrentDirectory => LocalApplicationData;

        public void Dispose()
        {
            if (Directory.Exists(LocalApplicationData))
            {
                Directory.Delete(LocalApplicationData, true);
            }
        }
    }

    private sealed class StubProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class NullReleaseClient : IProviderReleaseClient
    {
        public Task<ProviderRelease?> GetLatestAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken) =>
            Task.FromResult<ProviderRelease?>(null);
    }
}
