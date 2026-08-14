using Forge.Application;
using Forge.Providers;
using Forge.Providers.Claude;
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

        ProviderStatus status = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.VersionUnsupported, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsUpdateDirectlyWhenAlreadyInstalled()
    {
        using TestPaths paths = new();
        string executable = WriteClaudeExecutable(paths);
        bool ranUpdate = false;
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
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
        });

        ProviderStatus status = await provider.InstallOrUpdateAsync(TestContext.Current.CancellationToken);

        Assert.True(ranUpdate);
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("2.1.221", status.Version);
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

    private static ClaudeLlmProvider CreateProvider(TestPaths paths, Func<ProcessRequest, ProcessResult> respond) =>
        new(paths, new StubProcessRunner(respond));

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
