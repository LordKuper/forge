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
            Assert.Equal(["exec", "--json", "--", "list open bugs"], request.Arguments);
            return new(0, jsonl, string.Empty);
        });
        CodexProviderStrategy strategy = ReadyCodexStrategy(out TestPaths paths);
        using (paths)
        {
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
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ClaudeAdapterExtractsAssistantTextFromMessageContentBlocksAndIgnoresToolUseBlocks()
    {
        string jsonl = ReadFixture("claude-stream-json.jsonl");
        StubProcessRunner runner = new(request =>
        {
            Assert.Equal(
                ["-p", "--output-format", "stream-json", "--verbose", "--", "say hi"],
                request.Arguments);
            return new(0, jsonl, string.Empty);
        });
        ClaudeCodeProviderStrategy strategy = ReadyClaudeStrategy(out TestPaths paths);
        using (paths)
        {
            ClaudeCodeProviderAdapter adapter = new(strategy, runner);

            ProviderRunResult result = await adapter.RunAsync(
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
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PromptStartingWithADoubleDashIsPassedAfterTheEndOfOptionsMarker()
    {
        StubProcessRunner runner = new(request =>
        {
            int separatorIndex = request.Arguments.ToList().IndexOf("--");
            Assert.True(separatorIndex >= 0 && separatorIndex == request.Arguments.Count - 2);
            Assert.Equal("--dangerously-skip-permissions", request.Arguments[^1]);
            return new(0, string.Empty, string.Empty);
        });
        ClaudeCodeProviderStrategy strategy = ReadyClaudeStrategy(out TestPaths paths);
        using (paths)
        {
            ClaudeCodeProviderAdapter adapter = new(strategy, runner);

            await adapter.RunAsync(
                "--dangerously-skip-permissions",
                "C:\\work",
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunReturnsMalformedOutputWhenAnEventLineIsNotJson()
    {
        StubProcessRunner runner = new(_ => new(0, "not-json", string.Empty));
        CodexProviderStrategy strategy = ReadyCodexStrategy(out TestPaths paths);
        using (paths)
        {
            CodexProviderAdapter adapter = new(strategy, runner);

            ProviderRunResult result = await adapter.RunAsync(
                "prompt",
                "C:\\work",
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunClassifiesANonZeroExitAsAuthenticationAndRedactsTheDetail()
    {
        StubProcessRunner runner = new(_ => new(
            1,
            string.Empty,
            "Error: not logged in. api_key=sk-live-abcdef1234567890"));
        CodexProviderStrategy strategy = ReadyCodexStrategy(out TestPaths paths);
        using (paths)
        {
            CodexProviderAdapter adapter = new(strategy, runner);

            ProviderRunResult result = await adapter.RunAsync(
                "prompt",
                "C:\\work",
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(ProviderFailureKind.Authentication, result.Failure);
            Assert.DoesNotContain("sk-live-abcdef1234567890", result.Detail, StringComparison.Ordinal);
            Assert.Contains("[REDACTED:", result.Detail, StringComparison.Ordinal);
        }
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
        CodexProviderStrategy strategy = ReadyCodexStrategy(out TestPaths paths);
        using (paths)
        {
            CodexProviderAdapter adapter = new(strategy, runner);

            ProviderRunResult result = await adapter.RunAsync(
                "prompt",
                "C:\\work",
                TestContext.Current.CancellationToken);

            Assert.Equal(expected, result.Failure);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunReturnsNotReadyWithoutSpawningAProcessWhenNotInstalled()
    {
        bool spawned = false;
        StubProcessRunner runner = new(_ =>
        {
            spawned = true;
            return new(0, string.Empty, string.Empty);
        });
        using TestPaths paths = new();
        CodexProviderStrategy strategy = new(paths, new StubProcessRunner(_ => new(0, string.Empty, string.Empty)));
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

    private static CodexProviderStrategy ReadyCodexStrategy(out TestPaths paths)
    {
        paths = new TestPaths();
        string executable = Path.Combine(
            paths.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        return new(paths, new StubProcessRunner(_ => new(0, "0.146.0", string.Empty)));
    }

    private static ClaudeCodeProviderStrategy ReadyClaudeStrategy(out TestPaths paths)
    {
        paths = new TestPaths();
        string executable = Path.Combine(paths.UserProfile, ".local", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        return new(paths, new StubProcessRunner(_ => new(0, "2.1.221", string.Empty)));
    }

    /// <summary>An isolated, self-cleaning provider root; adapters only read pinned local state.</summary>
    private sealed class TestPaths : IEnvironmentPaths, IDisposable
    {
        public TestPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), $"forge-provider-exec-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string LocalApplicationData => Path.Combine(Root, "local");

        public string UserProfile => Path.Combine(Root, "userprofile");

        public string CurrentDirectory => Root;

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
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
