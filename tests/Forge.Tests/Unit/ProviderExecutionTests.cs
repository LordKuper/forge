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

    /// <summary>Fires many stdout and stderr sink calls genuinely concurrently via
    /// <see cref="Task.WhenAll(IEnumerable{Task})"/> — the same concurrency shape the real
    /// <c>ProcessRunner</c>'s two independent read loops produce — rather than the sequential
    /// per-line awaiting <see cref="StreamingProcessRunner"/> uses.</summary>
    private sealed class ConcurrentProcessRunner(int messageCount) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            if (outputSink is not null)
            {
                IEnumerable<Task> stdout = Enumerable.Range(0, messageCount)
                    .Select(_ => outputSink.OnStandardOutputLineAsync("""{"type":"message"}""", cancellationToken));
                IEnumerable<Task> stderr = Enumerable.Range(0, 50)
                    .Select(_ => outputSink.OnStandardErrorLineAsync("noise", cancellationToken));
                await Task.WhenAll(stdout.Concat(stderr)).ConfigureAwait(false);
                await outputSink.OnStandardOutputLineAsync("""{"type":"result"}""", cancellationToken)
                    .ConfigureAwait(false);
            }

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
