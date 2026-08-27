using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Compiler;
using Forge.Domain;
using Forge.Infrastructure;

namespace Forge.Providers;

/// <summary>A normalized view over a provider-specific JSON/JSONL event. `Text` is redacted before
/// it reaches this record — see ADR 0006: "applies redaction before any durable or presentation
/// boundary." There is deliberately no raw-JSON field: nothing in the repository consumes one
/// today, and an unredacted copy of the event would defeat that same redaction guarantee the
/// moment a future caller reads it.</summary>
public enum ProviderEventKind
{
    Message,
    ToolUse,
    Result,
    Unknown,
}

public sealed record ProviderEvent(ProviderEventKind Kind, string? Text);

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

/// <summary>The closed set <see cref="ProviderToolCall.Kind"/> may take, mirroring
/// `payload.tool_use.calls.items.kind` in docs/contracts/v1/schemas/event.schema.json. Plain strings
/// rather than an enum, following <see cref="Forge.Domain.DiffChangeKinds"/>'s own precedent (ADR
/// 0059): this value crosses the durable JSON envelope verbatim. Deliberately only the two kinds a
/// real recorded `codex exec --json` stream actually produced (ADR 0060) — no `read`, `search`, or
/// generic `tool` member exists, because no capture has ever demonstrated one.</summary>
public static class ProviderToolCallKinds
{
    /// <summary>A shell command the provider ran. Never carries a target: the only thing that would
    /// identify *which* command is the command text itself, which is never persisted (ADR 0006).</summary>
    public const string Command = "command";

    /// <summary>A file the provider created, modified, or deleted.</summary>
    public const string Edit = "edit";

    public static bool IsKnown(string? kind) => kind is Command or Edit;
}

/// <summary>What an adapter's own tool-call extractor concluded about one `ToolUse`-classified line.
/// Three outcomes, not two: a vendor stream interleaves genuine tool calls with ordinary agent
/// narration (`agent_message`, `reasoning`), and counting that narration as drift would make
/// <see cref="ProviderRunResult.UnmappedItemCount"/> non-zero on every healthy run (ADR 0060).</summary>
public enum ProviderToolCallOutcome
{
    /// <summary>Recognized provider content that is simply not a tool call. Produces no row and is
    /// never counted as drift.</summary>
    Ignored,

    /// <summary>At least one tool-call candidate was recognized.</summary>
    Extracted,

    /// <summary>The line's shape was not recognized at all — a real drift signal worth recording,
    /// since it means the vendor emitted something this adapter's mapping does not cover.</summary>
    Unmapped,
}

/// <summary>One tool-call observation exactly as the adapter read it, before any core-owned
/// normalization. <paramref name="RawTarget"/> is verbatim vendor text (an absolute path, or
/// <see langword="null"/>): relativizing, safety-checking, and redacting it is the core's job, not
/// the adapter's (ADR 0008's adapter/core split). <paramref name="CorrelationId"/> is used in memory
/// only, to pair a start with its completion and measure a duration; it is never persisted.
/// <paramref name="ExitCode"/>/<paramref name="Succeeded"/> are meaningful only when
/// <paramref name="IsCompletion"/> is <see langword="true"/>.</summary>
public sealed record ProviderToolCallCandidate(
    string Kind,
    string? RawTarget,
    string? CorrelationId,
    bool IsCompletion,
    int? ExitCode,
    bool? Succeeded);

/// <summary>One extractor call's verdict. A single vendor line may legitimately describe more than
/// one tool call (Codex's `file_change` carries a `changes` array), so this returns a list rather
/// than a single candidate — an entry beyond the first is never silently dropped.
///
/// <paramref name="CorrelationId"/> matters only for
/// <see cref="ProviderToolCallOutcome.Unmapped"/>, and only because one logical vendor item spans two
/// stream lines (Codex emits `item.started` then `item.completed` for the same `item.id`). The drift
/// counter it feeds is named — and documented — in ITEMS, so an adapter that can identify the
/// unrecognized item should say which one it was; the core then counts that item exactly once no
/// matter how many lines carried it. Used in memory only, exactly like
/// <see cref="ProviderToolCallCandidate.CorrelationId"/>, and never persisted. It is
/// <see langword="null"/> when the line was so unrecognizable that no id could be read from it, in
/// which case there is nothing to deduplicate on and every such line counts on its own.</summary>
public sealed record ProviderToolCallExtraction(
    ProviderToolCallOutcome Outcome,
    IReadOnlyList<ProviderToolCallCandidate> Candidates,
    string? CorrelationId = null)
{
    public static readonly ProviderToolCallExtraction Ignored = new(ProviderToolCallOutcome.Ignored, []);

    /// <summary>An unrecognized shape carrying no usable correlation id. Prefer
    /// <see cref="UnmappedItem"/> whenever the adapter did manage to read one.</summary>
    public static readonly ProviderToolCallExtraction Unmapped = new(ProviderToolCallOutcome.Unmapped, []);

    /// <summary>An unrecognized shape the adapter could still name, so the core can count the ITEM
    /// once rather than once per stream line describing it.</summary>
    public static ProviderToolCallExtraction UnmappedItem(string? correlationId) =>
        correlationId is { Length: > 0 }
            ? new(ProviderToolCallOutcome.Unmapped, [], correlationId)
            : Unmapped;

    /// <summary>An empty candidate list is <see cref="ProviderToolCallOutcome.Unmapped"/>, never
    /// <see cref="ProviderToolCallOutcome.Extracted"/>: an adapter that recognized the subtype but
    /// found nothing inside it saw a shape its mapping does not actually cover — and that unmapped
    /// verdict keeps <paramref name="correlationId"/>, since it is still one identifiable item.</summary>
    public static ProviderToolCallExtraction Of(
        IReadOnlyList<ProviderToolCallCandidate> candidates, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Count == 0
            ? UnmappedItem(correlationId)
            : new(ProviderToolCallOutcome.Extracted, candidates);
    }
}

/// <summary>A normalized, safety-checked, redacted tool call — what actually reaches durable state.
/// <paramref name="Target"/> is worktree-relative and forward-slashed, or <see langword="null"/>
/// when the vendor supplied none (every <see cref="ProviderToolCallKinds.Command"/>) or supplied one
/// that failed the syntactic safety check (rejected, never rewritten — ADR 0059's rule for diff
/// paths, reused). <paramref name="DurationMilliseconds"/> is Forge-observed wall time between the
/// vendor's start and completion lines arriving on stdout, never a vendor-reported timing field
/// (neither vendor publishes one); it is <see langword="null"/> when no matching start was seen.
/// </summary>
public sealed record ProviderToolCall(
    string Kind,
    string? Target,
    int? DurationMilliseconds,
    int? ExitCode,
    bool? Succeeded);

/// <summary>How many tool calls a run OBSERVED, which is deliberately not the same as how many rows
/// <see cref="ProviderRunResult.ToolCalls"/> retained: the sink stops retaining rows at
/// <see cref="ProviderExecution.MaxRetainedToolCalls"/>, so one stream line whose candidate list fans
/// out (Codex's `file_change` `changes` array is an array of arbitrary length) cannot grow that list
/// without limit. Past the cap a call survives only as these counters, which is what keeps ADR
/// 0059/0060's "honest totals plus an explicit elision count" rule true:
/// <see cref="ProviderToolUse.ToPayload"/> derives every total from here and never from the retained
/// list. <see cref="ProviderExecution.MaxAggregateBytes"/> bounds them far below
/// <see cref="int.MaxValue"/>, since every counted call had to occupy bytes on the wire.</summary>
public sealed record ProviderToolCallTotals(int Calls, int Commands, int Edits)
{
    public static readonly ProviderToolCallTotals None = new(0, 0, 0);

    /// <summary>The totals of a list that was never capped — the right answer for every producer that
    /// builds its rows in memory (a test double, any adapter that does not run through the sink).</summary>
    public static ProviderToolCallTotals Of(IReadOnlyList<ProviderToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return new(
            calls.Count,
            calls.Count(call => call.Kind == ProviderToolCallKinds.Command),
            calls.Count(call => call.Kind == ProviderToolCallKinds.Edit));
    }
}

/// <summary>ADR 0061: the token accounting one provider CLI reported for one attempt, read from the
/// same terminal event ADR 0006's uniqueness rule already identifies. Deliberately FLAT, with no
/// per-model dictionary: Claude's CLI keys its `modelUsage` by the exact model string that ran
/// (`"claude-opus-5[1m]"`), which is a vendor implementation detail — exactly one model runs per
/// attempt, so Forge's contract carries the one set of numbers rather than a map nothing would ever
/// have more than one entry in.
///
/// Every member is nullable because every member is genuinely optional: a vendor may report none,
/// some, or all of them, and this contract never guesses a value it was not told.
/// <paramref name="ContextWindow"/> in particular is the model's own context-window size, which only
/// Claude reports — Codex's `turn.completed.usage` has no such field at all, so a Codex attempt leaves
/// it <see langword="null"/> rather than being assigned a hardcoded per-model guess.</summary>
public sealed record ProviderUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheCreationTokens,
    int? ContextWindow)
{
    /// <summary>Whether this observation carries anything at all. An all-null usage record is
    /// indistinguishable from having observed nothing, so the write path skips it (ADR 0061) rather
    /// than recording a durable row that claims nothing.</summary>
    public bool HasAnyValue =>
        InputTokens is not null || OutputTokens is not null || CacheReadTokens is not null ||
        CacheCreationTokens is not null || ContextWindow is not null;
}

public sealed record ProviderRunResult(
    bool Succeeded,
    IReadOnlyList<ProviderEvent> Events,
    ProviderTerminalResult? TerminalResult,
    ProviderFailureKind Failure,
    string? Detail,
    IReadOnlyList<ProviderToolCall> ToolCalls,
    int UnmappedItemCount,
    ProviderToolCallTotals ToolCallTotals,
    ProviderUsage? Usage)
{
    /// <summary>ADR 0060's three trailing arguments and ADR 0061's fourth are optional so the many
    /// call sites that have no such data to report (every adapter that extracts neither, and every
    /// test double) stay unchanged; the record's own members are required, so no producer can forget
    /// them by accident. An omitted <paramref name="toolCallTotals"/> is derived from
    /// <paramref name="toolCalls"/>, which is correct for exactly the producers that never capped that
    /// list — only the sink, which does, passes its own. <paramref name="usage"/> has no equivalent
    /// derivation: there is at most one observation per attempt and it is either present or it is
    /// not.</summary>
    public static ProviderRunResult Success(
        IReadOnlyList<ProviderEvent> events,
        ProviderTerminalResult terminalResult,
        IReadOnlyList<ProviderToolCall>? toolCalls = null,
        int unmappedItemCount = 0,
        ProviderToolCallTotals? toolCallTotals = null,
        ProviderUsage? usage = null) =>
        new(
            true,
            events,
            terminalResult,
            ProviderFailureKind.None,
            null,
            toolCalls ?? [],
            unmappedItemCount,
            toolCallTotals ?? ProviderToolCallTotals.Of(toolCalls ?? []),
            usage);

    /// <summary>`detail` may echo raw provider output, so it is redacted before it is stored.
    /// Tool-call data is deliberately discarded on the failure path (ADR 0060), and token usage with
    /// it (ADR 0061): the attempt's work never reaches the integration branch, exactly as ADR 0059
    /// already decided for diff statistics.</summary>
    public static ProviderRunResult Failed(ProviderFailureKind failure, string detail) =>
        new(false, [], null, failure, SecretRedactor.Redact(detail), [], 0, ProviderToolCallTotals.None, null);
}

/// <summary>Maps a completed run's in-memory tool-call observations onto the durable
/// <see cref="ToolUsePayload"/> — the one place the per-attempt cap and its elision arithmetic live,
/// so the totals a reader sees and the rows actually written can never drift apart.</summary>
public static class ProviderToolUse
{
    /// <summary>Returns <see langword="null"/> when there is genuinely nothing to record (no tool
    /// calls and no unmapped items). A non-zero unmapped count alone still produces a payload: that
    /// is a drift signal worth being durable, even with no mapped call beside it.
    ///
    /// Every total comes from <see cref="ProviderRunResult.ToolCallTotals"/> rather than from the row
    /// list, because that list is itself capped in memory
    /// (<see cref="ProviderExecution.MaxRetainedToolCalls"/>): counting the rows would silently report
    /// a run that fanned out past the cap as a quieter one than it was, and would under-report the
    /// elision count that exists precisely to say so.</summary>
    public static ToolUsePayload? ToPayload(ProviderRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ProviderToolCallTotals totals = result.ToolCallTotals;
        if (totals.Calls == 0 && result.UnmappedItemCount == 0)
        {
            return null;
        }

        return new(
            totals.Calls,
            totals.Commands,
            totals.Edits,
            [
                .. result.ToolCalls
                    .Take(ProviderToolUseBudget.MaxCalls)
                    .Select(call => new ToolCallStat(
                        call.Kind, call.Target, call.DurationMilliseconds, call.ExitCode, call.Succeeded)),
            ],
            Math.Max(0, totals.Calls - ProviderToolUseBudget.MaxCalls),
            result.UnmappedItemCount);
    }
}

/// <summary>Maps a completed run's single token-usage observation onto the durable
/// <see cref="UsagePayload"/> — the counterpart of <see cref="ProviderToolUse"/>, and far simpler:
/// there is no cap, no elision arithmetic, and no list, because exactly one terminal event per attempt
/// carries usage at all.</summary>
public static class ProviderUsageReport
{
    /// <summary>Returns <see langword="null"/> when nothing usable was observed — either no terminal
    /// usage object at all, or one whose every field was absent. Unlike ADR 0059's diff record, an
    /// all-null usage row says nothing a reader could act on (it is not "this attempt used zero
    /// tokens", which no provider ever reports), so recording it would be a durable claim about
    /// something Forge never saw.</summary>
    public static UsagePayload? ToPayload(ProviderRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Usage is { HasAnyValue: true } usage
            ? new(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheCreationTokens,
                usage.ContextWindow)
            : null;
    }
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

    /// <summary>Tool-call rows one run keeps in memory. A separate bound from
    /// <see cref="MaxEventCount"/> because one retained event does NOT produce at most one row: a
    /// single line's extraction may carry an arbitrarily long candidate list (Codex's `file_change`
    /// `changes` is an array whose entries each become a row), so without this the list would be
    /// bounded only by how many array entries fit inside the 1 MiB frame bound, times every line.
    /// Sized at four times the durable per-record cap
    /// (<see cref="Forge.Application.ProviderToolUseBudget.MaxCalls"/>) so it can never discard a row
    /// the durable payload would have written, while still leaving room for the retained set to be
    /// inspected beyond it; a call past the cap is not lost, it is counted into
    /// <see cref="ProviderToolCallTotals"/> and reported as an elision.</summary>
    public const int MaxRetainedToolCalls = ProviderToolUseBudget.MaxCalls * 4;

    /// <summary>Total stdout+stderr bytes read for one run before the adapter fails closed —
    /// ADR 0006's aggregate-output bound.</summary>
    public const long MaxAggregateBytes = 64L * 1024 * 1024;

    /// <summary>The retained, redacted tail of raw output attached to a bound-violation failure's
    /// detail — ADR 0006's "retained safe tail," measured in UTF-16 characters (this is an
    /// in-memory diagnostic cap, not a wire-size guarantee).</summary>
    public const int SafeTailCharacters = 8192;

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
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null,
        Func<JsonElement, ProviderToolCallExtraction>? extractToolCall = null,
        Func<JsonElement, ProviderUsage?>? extractUsage = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (executablePath is null)
        {
            return ProviderRunResult.Failed(ProviderFailureKind.NotReady, "The provider is not ready.");
        }

        // Linked, not the caller's token directly: a bound violation must terminate the child
        // promptly (ADR 0006 "fails closed", not "keeps running to natural exit"), which this
        // helper triggers itself by cancelling its own token — never the caller's.
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        BoundedOutputSink sink = new(
            classify,
            extractText,
            onActivity,
            linkedCancellation.Cancel,
            extractToolCall,
            workingDirectory,
            extractUsage);

        ProcessResult result;
        try
        {
            result = await processRunner
                .RunAsync(
                    new ProcessRequest(
                        executablePath,
                        arguments,
                        workingDirectory,
                        environmentVariables,
                        StandardInput: prompt,
                        ReplaceEnvironment: true),
                    sink,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sink.Failure is not null && !cancellationToken.IsCancellationRequested)
        {
            // This helper's own bound-violation cancellation raced (or preceded) the process's
            // natural exit -- a self-inflicted, already-classified failure, not a real
            // caller-requested cancellation to propagate.
            (ProviderFailureKind Kind, string Detail) selfCancelled = sink.Failure.Value;
            return ProviderRunResult.Failed(selfCancelled.Kind, selfCancelled.Detail);
        }

        if (sink.Failure is { } boundFailure)
        {
            return ProviderRunResult.Failed(boundFailure.Kind, boundFailure.Detail);
        }

        if (result.ExitCode != 0)
        {
            // Classification scans the complete text (an error keyword could appear anywhere in
            // a large blob), but the detail actually stored is trimmed to the same
            // SafeTailCharacters bound the bounded-stream failures above already carry -- stderr
            // is otherwise unbounded (up to MaxAggregateBytes) and this is a durable/presentation
            // boundary the same as any other failure detail (ADR 0006).
            return ProviderRunResult.Failed(
                ClassifyFailure(result.StandardError), TrimToSafeTail(result.StandardError));
        }

        return sink.TerminalCount switch
        {
            0 => ProviderRunResult.Failed(
                ProviderFailureKind.MissingTerminalResult,
                "The provider exited without emitting a terminal-result event."),
            > 1 => ProviderRunResult.Failed(
                ProviderFailureKind.DuplicateTerminalResult,
                $"The provider emitted {sink.TerminalCount} terminal-result events for one run."),
            _ => ProviderRunResult.Success(
                sink.Events,
                sink.TerminalResult!,
                sink.ToolCalls,
                sink.UnmappedItemCount,
                sink.ToolCallTotals,
                sink.Usage),
        };
    }

    /// <summary>ADR 0060's core-owned path normalization: an adapter hands back whatever the vendor
    /// wrote (Codex reports an absolute, OS-native path), and this decides what — if anything — may
    /// be recorded for it. Lives here rather than in an adapter because
    /// <see cref="RelativePathShape"/> is `Forge.Runtime`-internal and because path safety is core
    /// policy, not vendor translation (ADR 0008).
    ///
    /// A path that escapes <paramref name="workingDirectory"/>, keeps a separator the current OS does
    /// not own, or is otherwise syntactically unsafe is REJECTED (null), never rewritten — ADR 0059's
    /// rule for diff paths, reused verbatim: there is no safe interpretation of such an entry, and the
    /// call itself is still recorded, just without a target. The surviving relative path is redacted
    /// BEFORE any bounding (ADR 0057/0059), since a redaction placeholder can be longer than the text
    /// it replaces.
    ///
    /// The whole body is fail-open, for the same reason <see cref="BoundedOutputSink.RecordToolCall"/>
    /// is (ADR 0060: "a vendor-shape surprise must never fail an attempt"). This runs on the stdout
    /// pump, so anything that escapes here faults that pump and fails an otherwise-successful attempt —
    /// and <see cref="Path.GetRelativePath"/> genuinely throws on hostile input beyond the obvious
    /// <see cref="ArgumentException"/> for an embedded null character: a rooted path at or past
    /// ~32,767 characters raises <see cref="PathTooLongException"/> on Windows, which the 1 MiB
    /// <see cref="MaxLineLengthBytes"/> frame bound leaves ample room to deliver. Enumerating every
    /// exception a path API may raise across three operating systems is exactly the guess this catch
    /// refuses to make; a genuine cancellation is still allowed through, so a real Host shutdown is
    /// never swallowed.</summary>
    internal static string? NormalizeToolCallTarget(string? rawTarget, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return null;
        }

        try
        {
            string candidate = Path.IsPathRooted(rawTarget)
                ? Path.GetRelativePath(workingDirectory, rawTarget)
                : rawTarget;
            candidate = candidate.Replace(Path.DirectorySeparatorChar, '/');
            return RelativePathShape.IsSyntacticallySafe(candidate) ? SecretRedactor.Redact(candidate) : null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
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

    /// <summary>Keeps only the last <see cref="SafeTailCharacters"/> characters, never splitting a
    /// surrogate pair -- the same bound and safety rule <see cref="BoundedOutputSink"/>'s own safe
    /// tail applies, reused here for the one-shot (not incrementally built) non-zero-exit path.</summary>
    private static string TrimToSafeTail(string text)
    {
        if (text.Length <= SafeTailCharacters)
        {
            return text;
        }

        int start = text.Length - SafeTailCharacters;
        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    /// <summary>
    /// Consumes stdout/stderr as they arrive (ADR 0006: "consumed concurrently as bounded
    /// streams") — genuinely concurrently: <see cref="Forge.Infrastructure.ProcessRunner"/> runs
    /// both read loops side by side against this one sink instance, so every state mutation below
    /// runs under <see cref="gate"/>. Incrementally parses documented JSONL from stdout while
    /// enforcing the frame/event-count/aggregate-size bounds; a violation both stops further
    /// parsing and requests the process be cancelled (<paramref name="requestCancellation"/>) so
    /// the run fails closed by actually terminating the child, not merely by refusing to retain
    /// any more of its output. <paramref name="onActivity"/> is invoked, unthrottled, once per
    /// parsed stdout event; throttling the resulting attempt-activity writes is the caller's
    /// policy to apply, not this shared execution helper's.
    /// </summary>
    private sealed class BoundedOutputSink(
        Func<JsonElement, ProviderEventKind> classify,
        Func<JsonElement, string?> extractText,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity,
        Action requestCancellation,
        Func<JsonElement, ProviderToolCallExtraction>? extractToolCall,
        string workingDirectory,
        Func<JsonElement, ProviderUsage?>? extractUsage) : IProcessOutputSink
    {
        private readonly object gate = new();
        private readonly List<ProviderEvent> events = [];
        private readonly StringBuilder safeTail = new();
        private readonly List<ProviderToolCall> toolCalls = [];

        /// <summary>Correlation id -> <see cref="Stopwatch.GetTimestamp"/> at the moment that tool
        /// call's `started` line arrived, so a completion can report how long Forge actually observed
        /// it running. In-memory only: the vendor's raw item id is never persisted. Explicitly capped
        /// at <see cref="MaxEventCount"/> entries rather than merely assumed to be bounded by it: one
        /// line's extraction may carry many candidates, so entry count does not follow event count.
        /// A start past the cap is dropped, which costs its completion nothing but the duration — a
        /// field <see cref="ProviderToolCall"/> already declares nullable — while an id already
        /// pending is always re-stamped, full or not.</summary>
        private readonly Dictionary<string, long> toolCallStarts = new(StringComparer.Ordinal);

        /// <summary>Correlation ids already counted in <see cref="UnmappedItemCount"/>. One logical
        /// vendor item spans two stream lines (Codex emits `item.started` then `item.completed` for the
        /// same `item.id`), and that counter is a durable, versioned field documented in ITEMS — so an
        /// unrecognized item counts once, on whichever of its lines arrives first, not once per line.
        /// A line carrying no usable id is not recorded here at all and always counts on its own:
        /// there is nothing to deduplicate on, and under-counting real drift would be worse than the
        /// double-count this set exists to prevent. In-memory only, and capped at
        /// <see cref="MaxEventCount"/> entries exactly like <see cref="toolCallStarts"/> and for the
        /// same reason. An id already in the set still deduplicates once the set is full; only a NEW
        /// id stops being remembered, so a full set can over-count drift and never under-count it —
        /// the same direction the id-less case already accepts.</summary>
        private readonly HashSet<string> unmappedItemIds = new(StringComparer.Ordinal);

        private long aggregateBytes;

        private int observedToolCalls;

        private int observedCommands;

        private int observedEdits;

        /// <summary>Read only after both stream tasks have completed (ADR 0006 stream consumption
        /// has finished by the time a caller inspects this), so no lock is needed here.</summary>
        public IReadOnlyList<ProviderEvent> Events => events;

        /// <summary>ADR 0060. Same read-after-completion rule as <see cref="Events"/>. Capped at
        /// <see cref="MaxRetainedToolCalls"/> rows: an entry here does NOT correspond one-to-one to a
        /// retained <see cref="ProviderEvent"/>, because one line's extraction may carry an
        /// arbitrarily long candidate list, so <see cref="MaxEventCount"/> alone would leave this list
        /// bounded only by how many candidates fit inside the frame bound. Capping the rows costs the
        /// durable payload nothing, because a call past the cap is still counted in
        /// <see cref="ToolCallTotals"/> — which is where <see cref="ProviderToolUse.ToPayload"/> reads
        /// every total and the elision count from.</summary>
        public IReadOnlyList<ProviderToolCall> ToolCalls => toolCalls;

        /// <summary>Totals over every observed call, retained or not. Same read-after-completion rule
        /// as <see cref="Events"/>.</summary>
        public ProviderToolCallTotals ToolCallTotals => new(observedToolCalls, observedCommands, observedEdits);

        /// <summary>ADR 0060's drift counter: `ToolUse`-classified lines whose subtype this adapter's
        /// mapping does not cover at all. Never incremented for recognized non-tool-call content
        /// (agent narration), which would make it non-zero on every healthy run.</summary>
        public int UnmappedItemCount { get; private set; }

        public int TerminalCount { get; private set; }

        public ProviderTerminalResult? TerminalResult { get; private set; }

        /// <summary>ADR 0061's single per-attempt token-usage observation, read from the FIRST event
        /// classified <see cref="ProviderEventKind.Result"/> — the same event, chosen by the same
        /// existing uniqueness logic, that <see cref="TerminalResult"/> itself is taken from, rather
        /// than a second scan of its own. A run that emits more than one terminal result fails closed
        /// before this is ever read (<see cref="ProviderFailureKind.DuplicateTerminalResult"/>), so
        /// "first" and "only" are the same event on every path that reaches a caller. Same
        /// read-after-completion rule as <see cref="Events"/>; <see langword="null"/> when no extractor
        /// was supplied, when the terminal event carried no usage object, or when the extractor
        /// threw.</summary>
        public ProviderUsage? Usage { get; private set; }

        public (ProviderFailureKind Kind, string Detail)? Failure { get; private set; }

        public async Task OnStandardOutputLineAsync(string line, CancellationToken cancellationToken)
        {
            ProviderEventKind? activityKind;
            bool failed;
            lock (gate)
            {
                activityKind = ProcessOutputLine(line, out failed);
            }

            if (failed)
            {
                requestCancellation();
            }

            if (activityKind is { } kind && onActivity is not null)
            {
                AttemptActivityKind activity =
                    kind == ProviderEventKind.ToolUse ? AttemptActivityKind.ToolUse : AttemptActivityKind.Heartbeat;
                await onActivity(activity, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task OnStandardErrorLineAsync(string line, CancellationToken cancellationToken)
        {
            bool failed;
            lock (gate)
            {
                Track(line, out failed);
            }

            if (failed)
            {
                requestCancellation();
            }

            return Task.CompletedTask;
        }

        /// <summary>Must run under <see cref="gate"/>. Returns the classified event kind when one
        /// was parsed and an activity callback is registered for it, or <see langword="null"/>
        /// otherwise (blank line, bound violation, or no callback).</summary>
        private ProviderEventKind? ProcessOutputLine(string line, out bool failed)
        {
            if (!Track(line, out failed))
            {
                return null;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (events.Count >= MaxEventCount)
            {
                Fail(ProviderFailureKind.MalformedOutput, "The provider exceeded the maximum event count for one run.");
                failed = true;
                return null;
            }

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Never interpolate the offending line into the message itself: `trimmed` can be
                // up to `MaxLineLengthBytes` (1 MiB), far past what the safe tail bounds. The
                // already-appended (redacted, `SafeTailCharacters`-bounded) safe tail carries this
                // same content for diagnostics without defeating that bound.
                Fail(ProviderFailureKind.MalformedOutput, "The provider emitted a non-JSON output line.");
                failed = true;
                return null;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail(
                    ProviderFailureKind.MalformedOutput,
                    "The provider emitted a JSON output line that was not an object.");
                failed = true;
                return null;
            }

            ProviderEventKind kind = classify(root);
            string? text = extractText(root);
            string? redactedText = text is null ? null : SecretRedactor.Redact(text);
            events.Add(new(kind, redactedText));

            if (kind == ProviderEventKind.Result)
            {
                TerminalCount++;
                TerminalResult ??= new ProviderTerminalResult(redactedText);
                if (extractUsage is not null && Usage is null)
                {
                    RecordUsage(root);
                }
            }

            if (kind == ProviderEventKind.ToolUse && extractToolCall is not null)
            {
                RecordToolCall(root);
            }

            return onActivity is not null ? kind : null;
        }

        /// <summary>Must run under <see cref="gate"/>. Fail-open exactly like
        /// <see cref="RecordToolCall"/>, and for the identical reason (ADR 0059/0060/0061): token usage
        /// is optional audit content read on the stdout pump, so an adapter extractor that throws on a
        /// vendor-shape surprise must cost the usage record and nothing else. Unlike tool-call
        /// extraction there is no drift counter to increment — a usage object is a leaf of one known
        /// event, not a stream of items whose shapes could silently stop being recognized — so a
        /// failure simply leaves <see cref="Usage"/> null. A genuine cancellation is still allowed
        /// through, so a real Host shutdown is never swallowed.
        ///
        /// An extractor that returns an all-null <see cref="ProviderUsage"/> is stored as-is rather
        /// than discarded here; <see cref="ProviderUsageReport.ToPayload"/> owns the "nothing worth
        /// recording" decision, in one place, for every producer.</summary>
        private void RecordUsage(JsonElement root)
        {
            try
            {
                Usage = extractUsage!(root);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Usage = null;
            }
        }

        /// <summary>Must run under <see cref="gate"/>. Fail-open by construction (ADR 0059's rule for
        /// this whole class of optional enrichment data, restated by ADR 0060): tool-call capture is
        /// audit content, so a vendor-shape surprise — including an outright throw from an adapter's
        /// own extractor — is counted as drift and never allowed to fail the attempt. A genuine
        /// cancellation is still allowed through; nothing here is expected to raise one, but
        /// swallowing one would hide a real Host shutdown.</summary>
        private void RecordToolCall(JsonElement root)
        {
            ProviderToolCallExtraction extraction;
            try
            {
                extraction = extractToolCall!(root);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // No verdict at all, so no id to deduplicate on: this line is drift on its own.
                CountUnmappedItem(null);
                return;
            }

            if (extraction is null || extraction.Outcome == ProviderToolCallOutcome.Unmapped)
            {
                CountUnmappedItem(extraction?.CorrelationId);
                return;
            }

            if (extraction.Outcome == ProviderToolCallOutcome.Ignored)
            {
                return;
            }

            IReadOnlyDictionary<string, int> durations = ResolveCompletionDurations(extraction.Candidates);
            foreach (ProviderToolCallCandidate candidate in extraction.Candidates)
            {
                RecordToolCallCandidate(candidate, durations);
            }
        }

        /// <summary>Must run under <see cref="gate"/>. Consumes each completed correlation id's pending
        /// start ONCE and returns the duration every row from it shares. ADR 0060: the entries of one
        /// `file_change` completion "become their own row sharing the item's correlation id (and
        /// therefore its duration)" — they are one logical operation that started and completed
        /// together. Resolving the duration inside the per-candidate loop instead would give the first
        /// entry the real value and every sibling <see langword="null"/>, because the first lookup
        /// removes the pending start the siblings still need. Only known kinds consume a start: an
        /// out-of-set kind is drift that produces no row, so it must not silently eat the pairing an
        /// adapter's real candidate would otherwise use.</summary>
        private Dictionary<string, int> ResolveCompletionDurations(
            IReadOnlyList<ProviderToolCallCandidate> candidates)
        {
            Dictionary<string, int> durations = new(StringComparer.Ordinal);
            foreach (ProviderToolCallCandidate candidate in candidates)
            {
                if (candidate is { IsCompletion: true, CorrelationId: { Length: > 0 } completionId } &&
                    ProviderToolCallKinds.IsKnown(candidate.Kind) &&
                    !durations.ContainsKey(completionId) &&
                    toolCallStarts.Remove(completionId, out long startedAt))
                {
                    durations[completionId] = (int)Math.Clamp(
                        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 0, int.MaxValue);
                }
            }

            return durations;
        }

        /// <summary>Must run under <see cref="gate"/>.</summary>
        private void RecordToolCallCandidate(
            ProviderToolCallCandidate candidate, IReadOnlyDictionary<string, int> durations)
        {
            // An adapter is untrusted input like any other: a kind outside the closed set is drift to
            // be counted, never a row to be fabricated on the durable envelope.
            if (!ProviderToolCallKinds.IsKnown(candidate.Kind))
            {
                CountUnmappedItem(candidate.CorrelationId);
                return;
            }

            if (!candidate.IsCompletion)
            {
                if (candidate.CorrelationId is { Length: > 0 } startId &&
                    (toolCallStarts.Count < MaxEventCount || toolCallStarts.ContainsKey(startId)))
                {
                    toolCallStarts[startId] = Stopwatch.GetTimestamp();
                }

                return;
            }

            // Counted before the retention check, never after it: the totals must cover every observed
            // call even when the row itself is elided from the retained list.
            observedToolCalls++;
            if (candidate.Kind == ProviderToolCallKinds.Command)
            {
                observedCommands++;
            }
            else if (candidate.Kind == ProviderToolCallKinds.Edit)
            {
                observedEdits++;
            }

            if (toolCalls.Count >= MaxRetainedToolCalls)
            {
                return;
            }

            int? duration = candidate.CorrelationId is { Length: > 0 } completionId &&
                durations.TryGetValue(completionId, out int resolved)
                ? resolved
                : null;

            toolCalls.Add(new(
                candidate.Kind,
                NormalizeToolCallTarget(candidate.RawTarget, workingDirectory),
                duration,
                candidate.ExitCode,
                candidate.Succeeded));
        }

        /// <summary>Must run under <see cref="gate"/>. See <see cref="unmappedItemIds"/>: counts one
        /// unrecognized ITEM, not one unrecognized stream line.</summary>
        private void CountUnmappedItem(string? correlationId)
        {
            if (correlationId is { Length: > 0 } id)
            {
                if (unmappedItemIds.Contains(id))
                {
                    return;
                }

                if (unmappedItemIds.Count < MaxEventCount)
                {
                    unmappedItemIds.Add(id);
                }
            }

            UnmappedItemCount++;
        }

        /// <summary>Must run under <see cref="gate"/>. Appends to the safe tail and enforces the
        /// per-line/aggregate bounds shared by both streams. Returns <see langword="false"/> (and
        /// sets <paramref name="failed"/>) once a bound has failed, whether just now or already,
        /// so the caller skips further, now-pointless work for this line.</summary>
        private bool Track(string line, out bool failed)
        {
            safeTail.Append(line).Append('\n');
            int excess = safeTail.Length - SafeTailCharacters;
            if (excess > 0)
            {
                // Never split a surrogate pair: cutting between a high and low surrogate would
                // leave an unpaired low surrogate at the start of the retained tail.
                if (char.IsLowSurrogate(safeTail[excess]))
                {
                    excess++;
                }

                safeTail.Remove(0, excess);
            }

            if (Failure is not null)
            {
                failed = true;
                return false;
            }

            long lineBytes = Encoding.UTF8.GetByteCount(line);
            aggregateBytes += lineBytes;
            if (aggregateBytes > MaxAggregateBytes)
            {
                Fail(ProviderFailureKind.MalformedOutput, "The provider exceeded the maximum aggregate output size.");
                failed = true;
                return false;
            }

            if (lineBytes > MaxLineLengthBytes)
            {
                Fail(ProviderFailureKind.MalformedOutput, "The provider emitted an oversized output line.");
                failed = true;
                return false;
            }

            failed = false;
            return true;
        }

        /// <summary>Must run under <see cref="gate"/>.</summary>
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
