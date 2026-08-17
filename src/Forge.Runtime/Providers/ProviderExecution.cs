using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Domain;
using Forge.Infrastructure;

namespace Forge.Providers;

/// <summary>A normalized view over a provider-specific JSON/JSONL event; `Raw` keeps full fidelity.
/// `Text` is redacted before it reaches this record — see ADR 0006: "applies redaction before any
/// durable or presentation boundary."</summary>
public enum ProviderEventKind
{
    Message,
    ToolUse,
    Result,
    Unknown,
}

public sealed record ProviderEvent(ProviderEventKind Kind, string? Text, JsonElement Raw);

/// <summary>
/// The one schema-valid terminal result ADR 0006 requires for a successful run: "An attempt
/// succeeds only after Forge receives a schema-valid terminal result for the owned attempt."
/// Deliberately minimal — neither vendor publishes a stable success/failure field name for its
/// terminal event, so this does not guess one; overall success is decided by
/// <see cref="ProviderRunResult.Succeeded"/> from process exit code, stderr classification, and
/// terminal-result uniqueness together, not by any field inside this record.
/// </summary>
public sealed record ProviderTerminalResult(string? Summary);

/// <summary>
/// Neither vendor publishes an exhaustive error-code table, so this is a classification of the
/// process outcome, not a guarantee of vendor intent. `Unknown` is the safe default.
/// </summary>
public enum ProviderFailureKind
{
    None,
    NotReady,
    Authentication,
    QuotaExceeded,
    RateLimited,
    Policy,
    Transient,

    /// <summary>Also covers a bounded-stream limit violation (oversized frame, or the aggregate
    /// output/event-count bound) — ADR 0006: "Oversized or malformed frames fail closed."</summary>
    MalformedOutput,

    /// <summary>The provider exited zero without emitting a terminal-result event — ADR 0006: "A
    /// zero process exit code proves only that the provider transport ended normally... Exit
    /// without an explicit terminal result is a provider failure."</summary>
    MissingTerminalResult,

    /// <summary>More than one event classified as a terminal result — ADR 0006: "duplicate or
    /// contradictory terminal results fail closed," regardless of whether their content agrees.</summary>
    DuplicateTerminalResult,
    Unknown,
}

public sealed record ProviderRunResult(
    bool Succeeded,
    IReadOnlyList<ProviderEvent> Events,
    ProviderTerminalResult? TerminalResult,
    ProviderFailureKind Failure,
    string? Detail)
{
    public static ProviderRunResult Success(
        IReadOnlyList<ProviderEvent> events, ProviderTerminalResult terminalResult) =>
        new(true, events, terminalResult, ProviderFailureKind.None, null);

    /// <summary>`detail` may echo raw provider output, so it is redacted before it is stored.</summary>
    public static ProviderRunResult Failed(ProviderFailureKind failure, string detail) =>
        new(false, [], null, failure, SecretRedactor.Redact(detail));
}

/// <summary>
/// Shared execution and JSONL parsing every <see cref="ILlmProvider"/> adapter reuses — generic
/// execution policy the core owns per ADR 0008, independent of which vendor CLI is being run.
/// Every argument reaches the resolved, Forge-owned executable directly through `ArgumentList` —
/// never through a shell — so prompt text can never be reinterpreted as a shell operator. The
/// prompt itself travels on stdin, never a command-line argument (ADR 0006).
/// </summary>
public static class ProviderExecution
{
    /// <summary>A single JSONL line longer than this fails the run closed — ADR 0006's "frame" bound.</summary>
    public const int MaxLineLengthBytes = 1_048_576;

    /// <summary>Total parsed events retained for one run — ADR 0006's aggregate-output bound
    /// applied to event count, so a runaway provider process cannot grow memory or durable state
    /// without limit.</summary>
    public const int MaxEventCount = 20_000;

    /// <summary>Total stdout+stderr bytes read for one run before the adapter fails closed —
    /// ADR 0006's aggregate-output bound.</summary>
    public const long MaxAggregateBytes = 64L * 1024 * 1024;

    /// <summary>The retained, redacted tail of raw output attached to a bound-violation failure's
    /// detail — ADR 0006's "retained safe tail."</summary>
    public const int SafeTailBytes = 8192;

    public static async Task<ProviderRunResult> RunAsync(
        string? executablePath,
        IProcessRunner processRunner,
        IReadOnlyList<string> arguments,
        string prompt,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        Func<JsonElement, ProviderEventKind> classify,
        Func<JsonElement, string?> extractText,
        CancellationToken cancellationToken,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (executablePath is null)
        {
            return ProviderRunResult.Failed(ProviderFailureKind.NotReady, "The provider is not ready.");
        }

        BoundedOutputSink sink = new(classify, extractText, onActivity);
        ProcessResult result = await processRunner
            .RunAsync(
                new ProcessRequest(
                    executablePath,
                    arguments,
                    workingDirectory,
                    environmentVariables,
                    StandardInput: prompt,
                    ReplaceEnvironment: true),
                sink,
                cancellationToken)
            .ConfigureAwait(false);

        if (sink.Failure is { } boundFailure)
        {
            return ProviderRunResult.Failed(boundFailure.Kind, boundFailure.Detail);
        }

        if (result.ExitCode != 0)
        {
            return ProviderRunResult.Failed(ClassifyFailure(result.StandardError), result.StandardError);
        }

        return sink.TerminalCount switch
        {
            0 => ProviderRunResult.Failed(
                ProviderFailureKind.MissingTerminalResult,
                "The provider exited without emitting a terminal-result event."),
            > 1 => ProviderRunResult.Failed(
                ProviderFailureKind.DuplicateTerminalResult,
                $"The provider emitted {sink.TerminalCount} terminal-result events for one run."),
            _ => ProviderRunResult.Success(sink.Events, sink.TerminalResult!),
        };
    }

    /// <summary>
    /// Best-effort keyword match over the process's own error text; a miss classifies as
    /// `Unknown` rather than guessing, since exact vendor wording is not part of any published
    /// contract (see ADR 0002).
    /// </summary>
    private static ProviderFailureKind ClassifyFailure(string standardError)
    {
        string text = standardError.ToLowerInvariant();
        if (ContainsAny(text, "not logged in", "unauthorized", "authentication", "api key", "401"))
        {
            return ProviderFailureKind.Authentication;
        }

        if (ContainsAny(text, "rate limit", "429", "too many requests"))
        {
            return ProviderFailureKind.RateLimited;
        }

        if (ContainsAny(text, "quota", "usage limit", "billing"))
        {
            return ProviderFailureKind.QuotaExceeded;
        }

        if (ContainsAny(text, "policy", "blocked by", "content filter"))
        {
            return ProviderFailureKind.Policy;
        }

        if (ContainsAny(text, "timed out", "timeout", "econnreset", "network", "temporarily unavailable"))
        {
            return ProviderFailureKind.Transient;
        }

        return ProviderFailureKind.Unknown;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));

    /// <summary>
    /// Consumes stdout/stderr line-by-line as they arrive (ADR 0006: "consumed concurrently as
    /// bounded streams"), incrementally parsing documented JSONL from stdout while enforcing the
    /// frame/event-count/aggregate-size bounds. <paramref name="onActivity"/> is invoked,
    /// unthrottled, once per parsed stdout event; throttling the resulting attempt-activity writes
    /// is the caller's policy to apply, not this shared execution helper's.
    /// </summary>
    private sealed class BoundedOutputSink(
        Func<JsonElement, ProviderEventKind> classify,
        Func<JsonElement, string?> extractText,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity) : IProcessOutputSink
    {
        private readonly List<ProviderEvent> events = [];
        private readonly StringBuilder safeTail = new();
        private long aggregateBytes;

        public IReadOnlyList<ProviderEvent> Events => events;

        public int TerminalCount { get; private set; }

        public ProviderTerminalResult? TerminalResult { get; private set; }

        public (ProviderFailureKind Kind, string Detail)? Failure { get; private set; }

        public async Task OnStandardOutputLineAsync(string line, CancellationToken cancellationToken)
        {
            if (!Track(line))
            {
                return;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            if (events.Count >= MaxEventCount)
            {
                Fail(ProviderFailureKind.MalformedOutput, "The provider exceeded the maximum event count for one run.");
                return;
            }

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                Fail(ProviderFailureKind.MalformedOutput, $"The provider emitted a non-JSON output line: {trimmed}");
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail(
                    ProviderFailureKind.MalformedOutput,
                    "The provider emitted a JSON output line that was not an object.");
                return;
            }

            ProviderEventKind kind = classify(root);
            string? text = extractText(root);
            string? redactedText = text is null ? null : SecretRedactor.Redact(text);
            events.Add(new(kind, redactedText, root));

            if (kind == ProviderEventKind.Result)
            {
                TerminalCount++;
                TerminalResult ??= new ProviderTerminalResult(redactedText);
            }

            if (onActivity is not null)
            {
                AttemptActivityKind activityKind =
                    kind == ProviderEventKind.ToolUse ? AttemptActivityKind.ToolUse : AttemptActivityKind.Heartbeat;
                await onActivity(activityKind, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task OnStandardErrorLineAsync(string line, CancellationToken cancellationToken)
        {
            Track(line);
            return Task.CompletedTask;
        }

        /// <summary>Appends to the safe tail and enforces the frame/aggregate bounds shared by
        /// both streams. Returns <see langword="false"/> once a bound has failed (or already had)
        /// so the caller skips further, now-pointless work for this line.</summary>
        private bool Track(string line)
        {
            safeTail.Append(line).Append('\n');
            if (safeTail.Length > SafeTailBytes)
            {
                safeTail.Remove(0, safeTail.Length - SafeTailBytes);
            }

            if (Failure is not null)
            {
                return false;
            }

            aggregateBytes += Encoding.UTF8.GetByteCount(line);
            if (aggregateBytes > MaxAggregateBytes)
            {
                Fail(ProviderFailureKind.MalformedOutput, "The provider exceeded the maximum aggregate output size.");
                return false;
            }

            if (Encoding.UTF8.GetByteCount(line) > MaxLineLengthBytes)
            {
                Fail(ProviderFailureKind.MalformedOutput, "The provider emitted an oversized output line.");
                return false;
            }

            return true;
        }

        private void Fail(ProviderFailureKind kind, string detail)
        {
            if (Failure is not null)
            {
                return;
            }

            Failure = (kind, $"{detail} Safe tail: {SecretRedactor.Redact(safeTail.ToString())}");
        }
    }
}
