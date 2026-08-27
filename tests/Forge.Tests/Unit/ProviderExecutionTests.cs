using System.Text.Json;
using Forge.Application;
using Forge.Domain;
using Forge.Providers;

namespace Forge.UnitTests;

/// <summary>
/// Exercises <see cref="ProviderExecution"/> directly against a capturing/streaming
/// <see cref="IProcessRunner"/> stub, independent of either vendor adapter — the bounded-stream,
/// stdin-delivery, minimal-environment, redaction, and terminal-result-uniqueness behaviors ADR
/// 0006 requires are all owned by this one shared helper.
/// </summary>
public sealed class ProviderExecutionTests
{
    private static readonly IReadOnlyDictionary<string, string> Environment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["FORGE_TEST"] = "1" };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncSendsThePromptOnStandardInputAndReplacesTheChildEnvironment()
    {
        StreamingProcessRunner runner = new(_ => Result("""{"type":"result"}"""));

        await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            ["exec"],
            "the prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(runner.LastRequest);
        Assert.Equal("the prompt", runner.LastRequest!.StandardInput);
        Assert.True(runner.LastRequest.ReplaceEnvironment);
        Assert.Same(Environment, runner.LastRequest.EnvironmentVariables);
        Assert.DoesNotContain("the prompt", runner.LastRequest.Arguments);
    }

    /// <summary>The stub-driven tests below (via <c>StreamingProcessRunner</c>) exercise only the
    /// post-hoc <c>sink.Failure</c> check after a normal return -- never the
    /// <c>catch (OperationCanceledException) when (...)</c> branch that recognizes this helper's
    /// own bound-violation self-cancellation, since that stub ignores its cancellation token
    /// entirely. This test uses a runner that actually observes the token (as the real
    /// <c>ProcessRunner</c> does once <c>Fail</c> calls <c>requestCancellation</c>), so it throws
    /// <see cref="OperationCanceledException"/> the same way, exercising the catch branch for
    /// real.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncTranslatesItsOwnBoundViolationCancellationIntoTheClassifiedFailure()
    {
        CancelAwareProcessRunner runner = new();

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken);

        // The post-hoc `sink.Failure` check alone would produce this exact same result even if
        // `requestCancellation` were never called -- so the assertion that actually distinguishes
        // "the catch branch ran" from "only the post-hoc check ran" is `ObservedCancellation`,
        // recorded by the runner itself at the moment it checked its token.
        Assert.True(
            runner.ObservedCancellation,
            "ProviderExecution's bound-violation Fail() should have cancelled the linked token before the runner checked it.");
        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncFailsClosedOnAnOversizedLine()
    {
        string oversized = new('x', ProviderExecution.MaxLineLengthBytes + 1);
        StreamingProcessRunner runner = new(_ => Result($$"""{"type":"message","text":"{{oversized}}"}"""));

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncFailsClosedWhenAggregateOutputExceedsTheBound()
    {
        // Each line is well under the per-line bound on its own; repeating it past the aggregate
        // bound trips the aggregate check instead, using as few large lines as possible.
        string line = $$"""{"type":"message","text":"{{new string('y', 1_040_000)}}"}""" + "\n";
        int repeats = (int)(ProviderExecution.MaxAggregateBytes / line.Length) + 1;
        string output = string.Concat(Enumerable.Repeat(line, repeats));
        StreamingProcessRunner runner = new(_ => Result(output));

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncRedactsExtractedEventTextBeforeItReachesTheCaller()
    {
        StreamingProcessRunner runner = new(_ => Result(
            """{"type":"message","text":"see api_key=sk-live-abcdef1234567890"}""" + "\n" +
            """{"type":"result"}"""));

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            ExtractText,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        string? text = result.Events[0].Text;
        Assert.NotNull(text);
        Assert.DoesNotContain("sk-live-abcdef1234567890", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", text, StringComparison.Ordinal);
    }

    /// <summary>Regression test: the non-zero-exit failure path previously embedded the complete,
    /// unbounded stderr text (up to `MaxAggregateBytes`) into `Detail` — 8,000x past the
    /// `SafeTailCharacters` bound every other failure detail respects.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncTrimsAnOversizedStderrDetailToTheSafeTailBound()
    {
        string hugeStderr = new string('e', ProviderExecution.SafeTailCharacters * 4) + "final marker at the end";
        StreamingProcessRunner runner = new(_ => new ProcessResult(1, string.Empty, hugeStderr));

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Detail);
        Assert.True(
            result.Detail!.Length < hugeStderr.Length,
            "The stored detail should be trimmed well below the full stderr length.");
        Assert.Contains("final marker at the end", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncInvokesOnActivityOncePerParsedEventWithTheMappedKind()
    {
        StreamingProcessRunner runner = new(_ => Result(
            """{"type":"message"}""" + "\n" +
            """{"type":"tool_use"}""" + "\n" +
            """{"type":"result"}"""));
        List<AttemptActivityKind> observed = [];

        await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken,
            (kind, _) =>
            {
                observed.Add(kind);
                return Task.CompletedTask;
            });

        Assert.Equal(
            [AttemptActivityKind.Heartbeat, AttemptActivityKind.ToolUse, AttemptActivityKind.Heartbeat],
            observed);
    }

    /// <summary>ADR 0060's path rule, exercised through the real sink on whichever OS this runs:
    /// an in-worktree absolute path becomes a forward-slashed relative target; a path that escapes the
    /// worktree, or that no path API can even parse, is REJECTED rather than rewritten -- the call is
    /// still recorded, just with no target, so a reader never sees a path that is not really inside
    /// the attempt's own worktree.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncNormalizesAnInWorktreeToolCallTargetAndRejectsEveryOtherShape()
    {
        string worktree = Path.Combine(Path.GetTempPath(), "forge-tool-use-worktree");
        string inside = Path.Combine(worktree, "src", "a.cs");
        string outside = Path.Combine(Path.GetTempPath(), "forge-tool-use-elsewhere", "secret.env");
        string unparsable = Path.Combine(worktree, "bad\0name.cs");

        IReadOnlyList<ProviderToolCall> calls = await CaptureToolCallsAsync(
            worktree, [inside, outside, unparsable, "   "]);

        Assert.Equal(4, calls.Count);
        Assert.Equal("src/a.cs", calls[0].Target);
        // Relativizing this one yields a `..` segment, which RelativePathShape rejects outright.
        Assert.Null(calls[1].Target);
        Assert.Null(calls[2].Target);
        Assert.Null(calls[3].Target);
    }

    /// <summary>A credential-shaped path survives normalization only in redacted form, because
    /// redaction runs inside the sink (ADR 0057/0059's "redact before bounding") rather than being
    /// left to the timeline passes alone.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncRedactsAToolCallTargetBeforeItEverLeavesTheSink()
    {
        const string secret = "password=Sup3rSecretValue";
        string worktree = Path.Combine(Path.GetTempPath(), "forge-tool-use-worktree");

        IReadOnlyList<ProviderToolCall> calls =
            await CaptureToolCallsAsync(worktree, [Path.Combine(worktree, "config", $"{secret}.env")]);

        ProviderToolCall call = Assert.Single(calls);
        Assert.NotNull(call.Target);
        Assert.DoesNotContain(secret, call.Target, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:credential]", call.Target, StringComparison.Ordinal);
    }

    /// <summary>An adapter is untrusted input like any other: a kind outside the closed set is drift
    /// to be counted, never a row to be fabricated onto the durable envelope. Same for an extractor
    /// that throws outright -- tool-call capture is optional enrichment, so it fails open and the run
    /// still succeeds.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsyncCountsAnUnusableExtractorResultAsDriftInsteadOfFailingTheRun(bool throws)
    {
        StreamingProcessRunner runner = new(_ => Result(
            """{"type":"tool_use"}""" + "\n" + """{"type":"result"}"""));

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken,
            extractToolCall: throws
                ? _ => throw new InvalidOperationException("the vendor shape surprised this adapter")
                : _ => ProviderToolCallExtraction.Of(
                    [new ProviderToolCallCandidate("invented_kind", null, "c", true, null, null)]));

        Assert.True(result.Succeeded);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(1, result.UnmappedItemCount);
    }

    /// <summary>ADR 0060's cap and elision arithmetic: only the per-call rows are bounded. The three
    /// totals stay honest over every observed call, so a chatty attempt is never reported as a quiet
    /// one -- ADR 0059's own rule for the diff payload's per-file list.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ToPayloadCapsTheCallRowsWhileKeepingTheTotalsOverEveryObservedCall()
    {
        const int total = ProviderToolUseBudget.MaxCalls + 7;
        List<ProviderToolCall> calls =
        [
            .. Enumerable.Range(0, total).Select(index => new ProviderToolCall(
                index % 2 == 0 ? ProviderToolCallKinds.Command : ProviderToolCallKinds.Edit,
                index % 2 == 0 ? null : $"src/f{index}.cs",
                12,
                index % 2 == 0 ? 0 : null,
                index % 2 == 0 ? true : null)),
        ];

        ToolUsePayload payload = Assert.IsType<ToolUsePayload>(
            ProviderToolUse.ToPayload(ProviderRunResult.Success([], new ProviderTerminalResult(null), calls, 3)));

        Assert.Equal(ProviderToolUseBudget.MaxCalls, payload.Calls.Count);
        Assert.Equal(7, payload.ElidedCalls);
        Assert.Equal(total, payload.ToolCalls);
        Assert.Equal(calls.Count(call => call.Kind == ProviderToolCallKinds.Command), payload.Commands);
        Assert.Equal(calls.Count(call => call.Kind == ProviderToolCallKinds.Edit), payload.Edits);
        Assert.Equal(3, payload.UnmappedItems);
    }

    /// <summary>Nothing observed means nothing recorded -- otherwise every attempt run through a
    /// provider whose adapter does not extract tool calls at all (Claude today) would publish an
    /// all-zero record implying it made none. A non-zero drift count alone still produces a payload:
    /// that is itself the signal worth keeping.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void ToPayloadRecordsNothingForARunThatObservedNothingAtAll(int unmappedItems, bool expectPayload)
    {
        ToolUsePayload? payload = ProviderToolUse.ToPayload(
            ProviderRunResult.Success([], new ProviderTerminalResult(null), [], unmappedItems));

        Assert.Equal(expectPayload, payload is not null);
    }

    /// <summary>Drives the real <see cref="ProviderExecution"/> sink with one completion per supplied
    /// vendor path, so path handling is exercised end to end (through the lock, the extractor
    /// contract, and the normalization helper) rather than against the helper in isolation.</summary>
    private static async Task<IReadOnlyList<ProviderToolCall>> CaptureToolCallsAsync(
        string workingDirectory, IReadOnlyList<string> rawTargets)
    {
        StreamingProcessRunner runner = new(_ => Result(string.Join(
            '\n',
            [.. Enumerable.Repeat("""{"type":"tool_use"}""", rawTargets.Count), """{"type":"result"}"""])));
        int next = 0;

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            workingDirectory,
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken,
            extractToolCall: _ => ProviderToolCallExtraction.Of(
                [
                    new ProviderToolCallCandidate(
                        ProviderToolCallKinds.Edit, rawTargets[next++], null, true, null, null),
                ]));

        Assert.True(result.Succeeded);
        return result.ToolCalls;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildMinimalEnvironmentExcludesANestedSessionMarkerEvenWhenSetOnTheHostProcess()
    {
        System.Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        try
        {
            IReadOnlyDictionary<string, string> environment =
                ProviderEnvironmentPolicy.BuildMinimalEnvironment([]);

            Assert.False(environment.ContainsKey("CLAUDECODE"));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("CLAUDECODE", null);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildMinimalEnvironmentIncludesOnlyTheNamedAuthenticationVariablesAndAppliesOverridesLast()
    {
        System.Environment.SetEnvironmentVariable("FORGE_TEST_PROVIDER_TOKEN", "secret-value");
        try
        {
            IReadOnlyDictionary<string, string> environment = ProviderEnvironmentPolicy.BuildMinimalEnvironment(
                ["FORGE_TEST_PROVIDER_TOKEN"],
                new Dictionary<string, string> { ["FORGE_TEST_PROVIDER_TOKEN"] = "overridden" });

            Assert.Equal("overridden", environment["FORGE_TEST_PROVIDER_TOKEN"]);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("FORGE_TEST_PROVIDER_TOKEN", null);
        }
    }

    /// <summary>Regression test: an earlier version of <c>BoundedOutputSink</c> mutated its event
    /// list, safe tail, and counters with no synchronization, even though the real
    /// <c>ProcessRunner</c> genuinely calls a sink's stdout/stderr callbacks concurrently from two
    /// independent read loops (not merely interleaved by `await`). Firing many concurrent calls
    /// against one sink instance reliably reproduces the corruption (a lost event or a thrown
    /// exception from the underlying non-thread-safe list) without the lock in place.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncToleratesTrulyConcurrentStdoutAndStderrDeliveryWithoutLosingEvents()
    {
        const int messageCount = 200;
        ConcurrentProcessRunner runner = new(messageCount);

        ProviderRunResult result = await ProviderExecution.RunAsync(
            "provider.exe",
            runner,
            [],
            "prompt",
            "C:\\work",
            Environment,
            Classify,
            _ => null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(messageCount + 1, result.Events.Count);
    }

    private static ProcessResult Result(string standardOutput) => new(0, standardOutput, string.Empty);

    private static ProviderEventKind Classify(JsonElement root) =>
        root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() switch
            {
                "message" => ProviderEventKind.Message,
                "tool_use" => ProviderEventKind.ToolUse,
                "result" => ProviderEventKind.Result,
                _ => ProviderEventKind.Unknown,
            }
            : ProviderEventKind.Unknown;

    private static string? ExtractText(JsonElement root) =>
        root.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

    /// <summary>Fires many stdout and stderr sink calls genuinely concurrently — each on its own
    /// thread-pool thread via <see cref="Task.Run(Func{Task})"/>, the same concurrency shape the
    /// real <c>ProcessRunner</c>'s two independent read loops produce. Without a callback
    /// registered, <c>BoundedOutputSink.OnStandardOutputLineAsync</c> never actually awaits
    /// anything (it only awaits when <c>onActivity</c> is non-null), so merely projecting each call
    /// through <c>Task.WhenAll(IEnumerable{Task})</c> would run every call synchronously, one at a
    /// time, on the single thread that enumerates the source sequence -- not concurrently at all,
    /// and so unable to reproduce the pre-fix race this test regresses against.</summary>
    private sealed class ConcurrentProcessRunner(int messageCount) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            if (outputSink is not null)
            {
                IEnumerable<Task> stdout = Enumerable.Range(0, messageCount)
                    .Select(_ => Task.Run(
                        () => outputSink.OnStandardOutputLineAsync("""{"type":"message"}""", cancellationToken),
                        cancellationToken));
                IEnumerable<Task> stderr = Enumerable.Range(0, 50)
                    .Select(_ => Task.Run(
                        () => outputSink.OnStandardErrorLineAsync("noise", cancellationToken), cancellationToken));
                await Task.WhenAll(stdout.Concat(stderr)).ConfigureAwait(false);
                await outputSink.OnStandardOutputLineAsync("""{"type":"result"}""", cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>Actually observes its own <see cref="CancellationToken"/> after delivering an
    /// oversized line to the sink, throwing <see cref="OperationCanceledException"/> the same way
    /// the real <c>ProcessRunner</c> would once <c>BoundedOutputSink.Fail</c> cancels the linked
    /// token it was given -- unlike <see cref="StreamingProcessRunner"/>, which ignores
    /// cancellation entirely and always returns normally. Records whether the token was actually
    /// observed as cancelled, since a caller that only checks the final <c>ProviderRunResult</c>
    /// cannot otherwise tell "the catch branch ran" apart from "only the post-hoc check ran" --
    /// both produce the identical classified failure.</summary>
    private sealed class CancelAwareProcessRunner : IProcessRunner
    {
        public bool ObservedCancellation { get; private set; }

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            string oversized = new('x', ProviderExecution.MaxLineLengthBytes + 1);
            await outputSink!.OnStandardOutputLineAsync(oversized, cancellationToken).ConfigureAwait(false);
            ObservedCancellation = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>Simulates the real <c>ProcessRunner</c>'s streaming contract (feeds the stubbed
    /// response to the output sink line by line) and captures the exact request it received, so
    /// tests can assert on stdin delivery and environment replacement.</summary>
    private sealed class StreamingProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            LastRequest = request;
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
