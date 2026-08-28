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

        await provider.RunAsync(
            "say hi", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

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
            Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], request.Arguments);
            Assert.Equal("say hi", request.StandardInput);
            return new(0, jsonl, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "say hi",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Events.Count);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[0].Kind);
        Assert.Equal(ProviderEventKind.Message, result.Events[1].Kind);
        Assert.Equal("Hello world", result.Events[1].Text);
        Assert.Equal(ProviderEventKind.Result, result.Events[2].Kind);
        Assert.NotNull(result.TerminalResult);
    }

    [Theory]
    [Trait("Category", "Unit")]
    // ADR 0062. The frozen profile's model reaches `--model` verbatim: Claude Code accepts both an
    // alias and a full model name there, and `sonnet` is exactly what this adapter declares as its
    // DefaultModel and therefore what neutral code freezes.
    [InlineData("sonnet", "high", new[] { "--model", "sonnet", "--effort", "high" })]
    [InlineData("claude-sonnet-5", "medium", new[] { "--model", "claude-sonnet-5", "--effort", "medium" })]
    // Claude offers no tier below `low`, so a profile frozen there clamps up rather than being
    // dropped or forwarded as a value the CLI would warn about and then ignore.
    [InlineData("sonnet", "minimal", new[] { "--model", "sonnet", "--effort", "low" })]
    // Either half is independently omittable; an absent value must produce no flag at all, never an
    // empty argument.
    [InlineData(null, "high", new[] { "--effort", "high" })]
    [InlineData("sonnet", null, new[] { "--model", "sonnet" })]
    [InlineData("sonnet", "aggressive", new[] { "--model", "sonnet" })]
    public async Task RunAsyncAppliesTheFrozenModelAndEffortToTheCommandLine(
        string? model, string? effort, string[] expectedTail)
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        IReadOnlyList<string>? capturedArguments = null;
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
        {
            capturedArguments = request.Arguments;
            return new(0, """{"type":"result"}""", string.Empty);
        });

        await provider.RunAsync("say hi", "C:\\work", model, effort, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", .. expectedTail], capturedArguments);
    }

    /// <summary>ADR 0066's asymmetry, from this side: Claude Code publishes no catalog command, so this
    /// adapter ships the exact alias set the vendor's own `claude --help` names for `--model` ("an
    /// alias for the latest model (e.g. 'fable', 'opus', or 'sonnet')"), in that order, rather than
    /// probing. Two properties are pinned because both are what make the fixed list safe to build a
    /// picker on: it never fails or empties (there is nothing to fail), and it contains this adapter's
    /// own <c>DefaultModel</c> — a default that is not itself selectable would make the picker's
    /// pre-selected entry unreachable, and would fail the very enumeration check a sprint's explicit
    /// request goes through.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListModelsReturnsTheVendorDocumentedAliasSetIncludingTheDefault()
    {
        using TestPaths paths = new();
        ClaudeLlmProvider provider = CreateProvider(paths, _ => throw new InvalidOperationException(
            "A fixed alias set must never spawn a vendor process."));

        IReadOnlyList<string> models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["fable", "opus", "sonnet"], models);
        Assert.Contains(provider.DefaultModel, models);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncPassesThePromptOnStandardInputNeverAsACommandLineArgument()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        ClaudeLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal("--dangerously-skip-permissions", request.StandardInput);
            Assert.DoesNotContain("--dangerously-skip-permissions", request.Arguments);
            return new(0, string.Empty, string.Empty);
        });

        await provider.RunAsync(
            "--dangerously-skip-permissions",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncFailsClosedWhenNoTerminalResultEventIsEmitted()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        ClaudeLlmProvider provider = CreateProvider(paths, _ => new(0, string.Empty, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "say hi", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MissingTerminalResult, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncFailsClosedWhenTwoTerminalResultEventsAreEmitted()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        string jsonl = """
            {"type":"result","subtype":"success","result":"first"}
            {"type":"result","subtype":"success","result":"second"}

            """;
        ClaudeLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "say hi", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.DuplicateTerminalResult, result.Failure);
    }

    /// <summary>ADR 0061, against the real thing: `claude-stream-json-usage.jsonl` is a verbatim
    /// `claude -p --output-format stream-json --verbose` stream recorded from Claude Code 2.1.233
    /// driving a throwaway worktree, with only captured paths and identifiers replaced by
    /// placeholders. The whole mapping was built from this capture and nothing else, so this is what
    /// pins it — including the context window, which Claude alone publishes and which only exists on
    /// the `modelUsage` entry named for the exact model that ran.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncCapturesTokenUsageFromARealRecordedClaudeStream()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        string jsonl = ReadFixture("claude-stream-json-usage.jsonl");
        ClaudeLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "read the file", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        ProviderUsage usage = Assert.IsType<ProviderUsage>(result.Usage);
        Assert.Equal(6, usage.InputTokens);
        Assert.Equal(265, usage.OutputTokens);
        Assert.Equal(75_666, usage.CacheReadTokens);
        Assert.Equal(38_581, usage.CacheCreationTokens);
        Assert.Equal(1_000_000, usage.ContextWindow);
    }

    /// <summary>ADR 0061's non-interference check. The same real capture carries a mid-stream
    /// `rate_limit_event`, which is provider-quota territory (parity review finding B7) and entirely
    /// unrelated to this slice. It must not be classified as terminal, must not be mistaken for the
    /// usage-bearing event, and must not stop the genuine `result` that follows it from being found —
    /// so a run over that stream still succeeds (exactly one terminal result) and still reports
    /// usage.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARateLimitEventInTheStreamIsNeitherTerminalNorMistakenForTheUsageEvent()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        string jsonl = ReadFixture("claude-stream-json-usage.jsonl");
        Assert.Contains("\"type\":\"rate_limit_event\"", jsonl, StringComparison.Ordinal);
        ClaudeLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "read the file", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        // Exactly one terminal result reached the uniqueness check: had the rate-limit line been
        // classified as one too, this would have failed as DuplicateTerminalResult instead.
        Assert.True(result.Succeeded);
        Assert.NotEqual(ProviderFailureKind.DuplicateTerminalResult, result.Failure);
        // The rate-limit line is ordinary unclassified content, never a Result.
        Assert.Single(result.Events, item => item.Kind == ProviderEventKind.Result);
        // And the real terminal event still supplied the usage, from after that line in the stream.
        Assert.Equal(265, result.Usage?.OutputTokens);
    }

    /// <summary>ADR 0061: the context window is read only when `modelUsage` holds exactly one entry —
    /// the only shape one attempt can produce, since an attempt runs one model. Zero entries, more
    /// than one, or no `modelUsage` at all yields null rather than a pick: choosing "the first" of
    /// several would be a guess presented as a measurement, and an absent denominator is honest while
    /// a wrong one silently corrupts every reading built on it. The token counts beside it are
    /// unaffected either way.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"m":{"contextWindow":200000}}""", 200_000)]
    [InlineData("""{}""", null)]
    [InlineData("""{"a":{"contextWindow":200000},"b":{"contextWindow":1000000}}""", null)]
    [InlineData("""{"m":{}}""", null)]
    [InlineData("""{"m":{"contextWindow":-1}}""", null)]
    [InlineData("""{"m":{"contextWindow":"200000"}}""", null)]
    [InlineData("null", null)]
    public async Task TheContextWindowIsReadOnlyFromAnUnambiguousSingleModelUsageEntry(
        string modelUsage, int? expected)
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        string jsonl = """{"type":"result","usage":{"input_tokens":11,"output_tokens":22},"modelUsage":""" +
            modelUsage + "}\n";
        ClaudeLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "say hi", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Usage?.ContextWindow);
        // Never collateral damage: an unusable denominator must not cost the numerators beside it.
        Assert.Equal(11, result.Usage?.InputTokens);
        Assert.Equal(22, result.Usage?.OutputTokens);
    }

    /// <summary>ADR 0061: absence is never zero. A terminal event with no `usage` object at all
    /// reports every count as not-reported, which <c>ProviderUsageReport.ToPayload</c> then declines to
    /// record — a durable all-null row would claim an observation that never happened, and cannot even
    /// be read as "spent nothing", since no provider reports a zero-token turn.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATerminalResultWithNoUsageObjectRecordsNothingRatherThanZeros()
    {
        using TestPaths paths = new();
        WriteClaudeExecutable(paths);
        ClaudeLlmProvider provider = CreateProvider(
            paths, _ => new(0, """{"type":"result","subtype":"success","result":"done"}""" + "\n", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "say hi", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Usage);
        Assert.False(result.Usage!.HasAnyValue);
        Assert.Null(ProviderUsageReport.ToPayload(result));
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

    /// <summary>Simulates the real <c>ProcessRunner</c>'s streaming contract: feeds the stubbed
    /// response's output to the output sink line by line, the way the actual process pipe reader
    /// does, so <c>ProviderExecution</c>'s sink-driven parsing sees the same events a real run
    /// would.</summary>
    private sealed class StubProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult result = respond(request);
            if (outputSink is not null)
            {
                foreach (string line in result.StandardOutput.Split('\n'))
                {
                    await outputSink.OnStandardOutputLineAsync(line, cancellationToken).ConfigureAwait(false);
                }

                foreach (string line in result.StandardError.Split('\n'))
                {
                    await outputSink.OnStandardErrorLineAsync(line, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
    }

}
