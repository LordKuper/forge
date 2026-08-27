using System.Text.Json;
using Forge.Application;
using Forge.Domain;
using Forge.Providers;
using Forge.Providers.Codex;
using Forge.Tests.Support;
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

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

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

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

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

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

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

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsUpdateAvailableWhenTheReleaseSourceReportsANewerVersion()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ => new(0, "0.146.0", string.Empty),
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 147, 0))));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverLeavesUpdateAvailableUnknownWhenTheReleaseCheckFails()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "0.146.0", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        // A release-check failure never blocks an otherwise-usable installed version.
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Null(status.UpdateAvailable);
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

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.True(ranInstaller);
        Assert.Equal(ProviderState.Ready, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateFailsWhenTheInstallerExitsNonZero()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, "install failed"));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateSkipsWorkWhenAlreadyOnTheLatestVersion()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        bool secondCall = false;
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ =>
            {
                Assert.False(secondCall, "No install/update process should run when already current.");
                secondCall = true;
                return new(0, "0.146.0", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 146, 0))));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsTheInstallerAgainWhenANewerVersionIsAvailable()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        int processCalls = 0;
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ =>
            {
                processCalls++;
                // 1: the initial local probe. 2: the re-probe taken right after the lock is
                // acquired (still OLD — no concurrent process updated it first). Both must report
                // the OLD version so a newer release actually looks newer. 3: the install script
                // rerun (exit code only matters). 4: the post-update recheck, reporting the new
                // version.
                return new(0, processCalls <= 2 ? "0.146.0" : "0.147.0", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 147, 0))));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(4, processCalls);
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsUsableWithoutMutatingWhenTheLockCannotBeAcquired()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        int processCalls = 0;
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ =>
            {
                processCalls++;
                return new(0, "0.146.0", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 147, 0))),
            installLock: new FakeInstallLock(acquires: false));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        // Exactly the local probe that established a newer release exists — never the actual
        // install/update command, since the lock could not be acquired.
        Assert.Equal(1, processCalls);
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
            () => provider.DiscoverAsync(false, cancelled.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheVersionProbeExceedsItsTimeout()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = new(
            paths,
            new HangingProcessRunner(),
            new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            new FakeInstallLock(),
            new FakeClock(),
            versionProbeTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsFailedWhenTheInstallerExceedsItsTimeout()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = new(
            paths,
            new HangingProcessRunner(),
            new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            new FakeInstallLock(),
            new FakeClock(),
            installTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CheckAuthenticationReflectsTheLoginStatusExitCode(int exitCode, bool expectedReady)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(["login", "status"], request.Arguments);
            return new(exitCode, string.Empty, string.Empty);
        });

        ProviderAuthenticationStatus status =
            await provider.CheckAuthenticationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            expectedReady ? ProviderHealthAuthentication.Ready : ProviderHealthAuthentication.Required,
            status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckAuthenticationReportsCheckFailedWithoutSpawningAProcessWhenNotInstalled()
    {
        bool spawned = false;
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ =>
        {
            spawned = true;
            return new(0, string.Empty, string.Empty);
        });

        ProviderAuthenticationStatus status =
            await provider.CheckAuthenticationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderHealthAuthentication.CheckFailed, status.State);
        Assert.False(spawned);
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
            Assert.Equal(["exec", "--json"], request.Arguments);
            Assert.Equal("list open bugs", request.StandardInput);
            return new(0, jsonl, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "list open bugs",
            "C:\\work",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Events.Count);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[0].Kind);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[1].Kind);
        Assert.Equal(ProviderEventKind.ToolUse, result.Events[2].Kind);
        Assert.Equal(ProviderEventKind.Result, result.Events[3].Kind);
        Assert.NotNull(result.TerminalResult);
    }

    /// <summary>ADR 0060, against the real thing: `codex-exec-json-tool-calls.jsonl` is a verbatim
    /// `codex exec --json` stream recorded from Codex CLI 0.149.1 driving a throwaway worktree
    /// (read a file, run a command, edit a file), with only the captured absolute path replaced by a
    /// placeholder. The entire mapping was built from this capture and nothing else, so this test is
    /// what actually pins it: the two `agent_message` items must be recognized and ignored (never
    /// counted as drift), both `command_execution` completions must produce a targetless command row
    /// carrying its exit code, and the `file_change` completion must produce an edit row whose
    /// absolute vendor path has been relativized against the attempt's working directory.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncCapturesToolCallsFromARealRecordedCodexStream()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string jsonl = ReadFixture("codex-exec-json-tool-calls.jsonl");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "append a line",
            CapturedWorktree,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        // Two agent_message items rode in the same stream; neither may be counted as drift, or a
        // perfectly healthy run would look broken.
        Assert.Equal(0, result.UnmappedItemCount);
        Assert.Equal(3, result.ToolCalls.Count);

        ProviderToolCall[] commands =
            [.. result.ToolCalls.Where(call => call.Kind == ProviderToolCallKinds.Command)];
        Assert.Equal(2, commands.Length);
        Assert.All(commands, call =>
        {
            Assert.Null(call.Target);
            Assert.Equal(0, call.ExitCode);
            Assert.True(call.Succeeded);
            // Paired against its own `item.started`, so a Forge-observed duration exists.
            Assert.NotNull(call.DurationMilliseconds);
        });

        ProviderToolCall edit = Assert.Single(
            result.ToolCalls, call => call.Kind == ProviderToolCallKinds.Edit);
        Assert.Equal("sample.txt", edit.Target);
        Assert.Null(edit.ExitCode);
        Assert.Null(edit.Succeeded);
    }

    /// <summary>ADR 0060's three-way split, stated as one table: a mapped tool-call subtype produces a
    /// row, a recognized-but-not-a-tool-call subtype produces nothing at all, and only a genuinely
    /// unrecognized shape increments the drift counter. The malformed cases must not throw out of the
    /// sink either -- tool-call capture is optional enrichment and fails open.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"agent_message","text":"hi"}}""", 0, 0)]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"reasoning","text":"hmm"}}""", 0, 0)]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"totally_unknown_thing"}}""", 0, 1)]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"web_search","query":"x"}}""", 0, 1)]
    [InlineData("""{"type":"item.completed","id":"i"}""", 0, 1)]
    [InlineData("""{"type":"item.completed","item":42}""", 0, 1)]
    [InlineData("""{"type":"item.command_execution","command":"echo hi"}""", 0, 1)]
    [InlineData(
        """{"type":"item.completed","item":{"id":"i","type":"file_change","changes":[]}}""", 0, 1)]
    [InlineData(
        """{"type":"item.completed","item":{"id":"i","type":"command_execution","exit_code":0,"status":"completed"}}""",
        1,
        0)]
    public async Task RunAsyncSeparatesMappedToolCallsFromNarrationAndFromGenuineDrift(
        string line, int expectedToolCalls, int expectedUnmapped)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            0, line + "\n" + """{"type":"turn.completed"}""", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedToolCalls, result.ToolCalls.Count);
        Assert.Equal(expectedUnmapped, result.UnmappedItemCount);
    }

    /// <summary>The invariant this whole slice hangs on (ADR 0006 via ADR 0060): a
    /// `command_execution` item carries the full command line and its full stdout, both of which
    /// routinely contain secrets. Neither field is ever read into anything durable. Asserted against
    /// the SERIALIZED journal line, not only the typed objects, so a field added to any layer between
    /// here and disk cannot reintroduce the leak unnoticed.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncNeverCarriesCommandTextOrCommandOutputIntoTheDurableRecord()
    {
        const string commandText = "curl -H 'Authorization: Bearer sk-live-fake123' https://example.test";
        const string outputText = "leaked-output-token-9f3c21";
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        // Assembled by concatenation rather than interpolation: the JSON's own trailing `}}` collides
        // with a raw interpolated literal's closing braces.
        string escapedCommand = commandText.Replace("\"", "\\\"", StringComparison.Ordinal);
        string stream = string.Join(
            '\n',
            "{\"type\":\"item.started\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\"," +
                "\"command\":\"" + escapedCommand + "\",\"aggregated_output\":\"\",\"exit_code\":null," +
                "\"status\":\"in_progress\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\"," +
                "\"command\":\"" + escapedCommand + "\",\"aggregated_output\":\"" + outputText + "\"," +
                "\"exit_code\":0,\"status\":\"completed\"}}",
            """{"type":"turn.completed"}""");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, stream, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, TestContext.Current.CancellationToken);

        ProviderToolCall call = Assert.Single(result.ToolCalls);
        Assert.Equal(ProviderToolCallKinds.Command, call.Kind);
        Assert.Null(call.Target);

        // All the way to the real journal file on disk, not merely to the typed record: everything
        // between the adapter and that line (payload mapping, codec, schema) is exercised, so a field
        // added at any of those layers cannot reintroduce the leak unnoticed.
        ToolUsePayload payload = Assert.IsType<ToolUsePayload>(ProviderToolUse.ToPayload(result));
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_created", "created", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendAttemptToolUseRecordedAsync(root.Path, sprintId, attemptId, payload, cancellationToken);

        string journal = string.Concat(
            Directory.EnumerateFiles(root.Path, "*.jsonl", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.Contains(WorkflowEvent.AttemptToolUseRecordedType, journal, StringComparison.Ordinal);
        foreach (string serialized in new[] { JsonSerializer.Serialize(call, StatusJson.Options), journal })
        {
            Assert.DoesNotContain("Authorization", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-live-fake123", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("curl", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(outputText, serialized, StringComparison.Ordinal);
        }
    }

    /// <summary>The placeholder worktree root the recorded fixture's absolute `file_change` path sits
    /// under, so relativizing it produces a real, in-worktree relative target.</summary>
    private const string CapturedWorktree = @"C:\Users\example\codex-worktree";

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncFailsClosedWhenNoTerminalResultEventIsEmitted()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            0, """{"type":"thread.started","thread_id":"t1"}""" + "\n", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", "C:\\work", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MissingTerminalResult, result.Failure);
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

    private static CodexLlmProvider CreateProvider(
        TestPaths paths,
        Func<ProcessRequest, ProcessResult> respond,
        IProviderReleaseSource? releaseSource = null,
        IProviderInstallLock? installLock = null) =>
        new(
            paths,
            new StubProcessRunner(respond),
            releaseSource ?? new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            installLock ?? new FakeInstallLock(),
            new FakeClock());

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

    /// <summary>
    /// Never completes on its own, mirroring how the real <c>ProcessRunner</c> behaves against a
    /// hung child process: it only ends when the caller's token is cancelled.
    /// </summary>
    private sealed class HangingProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable: Task.Delay(Infinite) only returns via cancellation.");
        }
    }

}
