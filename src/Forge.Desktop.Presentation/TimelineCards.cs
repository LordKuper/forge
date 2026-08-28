using System.Globalization;
using Forge.Application;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop.Presentation;

/// <summary>How a <see cref="TimelineStat"/> or <see cref="TimelineDetailRow"/> should read, in
/// meaning rather than in pixels: the view maps each member onto one of App.xaml's existing
/// <c>ColorStatus*</c>/neutral tokens. Kept out of the view so the whole projection stays testable
/// without a MAUI control (ADR 0050: none can be instantiated headlessly in this suite).</summary>
public enum TimelineStatTone
{
    Neutral,
    Positive,
    Negative,
    Caution,
}

/// <summary>One counter chip on a timeline payload card, already localized and already formatted.
/// <para><see cref="RestatesSummary"/> is <see langword="true"/> when this exact number is already
/// spoken by the item's own localized summary sentence (<see cref="TimelineItemView.MessageText"/>),
/// which the view renders as a described label. Such a chip is visual reinforcement only and is
/// excluded from the accessible tree by the view; a chip carrying something the sentence does not say
/// -- an unreported token counter's absence, the tool-call drift count -- keeps
/// <see langword="false"/> and stays a real, described stop.</para></summary>
public sealed record TimelineStat(string Label, string Value, TimelineStatTone Tone, bool RestatesSummary);

/// <summary>One per-file or per-call row inside a card's collapsed detail section.
/// <see cref="SecondaryText"/> is <see langword="null"/> when the payload reported nothing beyond
/// <see cref="PrimaryText"/> -- never an empty placeholder standing in for an absent value.</summary>
public sealed record TimelineDetailRow(string PrimaryText, string? SecondaryText, TimelineStatTone Tone);

/// <summary>Everything a timeline item's structured payload contributes to its row: an always-visible
/// chip strip, plus detail rows and an elision note the view builds lazily on first expansion.
/// </summary>
public sealed record TimelineCardContent(
    IReadOnlyList<TimelineStat> Stats,
    IReadOnlyList<TimelineDetailRow> DetailRows,
    string? ElidedText);

/// <summary>
/// Turns ADR 0059/0060/0061's structured <see cref="WorkflowEventPayload"/> into the localized,
/// pre-formatted content the sprint workspace renders (desktop design-parity review finding D1). All
/// text and every localization decision live here; the view owns only color and layout, so the whole
/// projection is unit-testable in a project with no MAUI reference at all.
/// </summary>
/// <remarks>
/// Three rules this type exists to hold, each of which the underlying data makes load-bearing:
/// <list type="bullet">
/// <item>A usage counter the provider did not report produces no chip whatsoever. Every
/// <see cref="UsagePayload"/> field is independently nullable and <see langword="null"/> means "not
/// reported", which is a different fact from a reported zero (ADR 0061). The localized summary
/// sentence substitutes 0 for an unreported counter, so this card is the only surface on which that
/// absence is visible -- rendering it as "0" here would erase the one thing the card adds.</item>
/// <item>No ratio is ever computed. <see cref="UsagePayload.ContextWindow"/> is Claude-only (Codex
/// publishes no context-window field and is never given a guessed one), so a "used / window" reading
/// would exist for some attempts and not others, and deciding which counters belong over that
/// denominator is a judgement no layer of this codebase has made.</item>
/// <item>Every closed-set machine value is localized before it is rendered --
/// <see cref="DiffChangeKinds"/> and <see cref="ProviderToolCallKinds"/> -- exactly as
/// <see cref="TimelineMessageFormatter"/> already localizes blocked reasons and attempt states rather
/// than interpolating the raw snake_case code into otherwise-translated text.</item>
/// </list>
/// Nothing here renders diff hunk text, raw command lines, or command output: none of it is ever
/// persisted (ADR 0006/0059/0060), so there is nothing to project.
/// </remarks>
public static class TimelineCardProjector
{
    /// <summary>Returns <see langword="null"/> for an item carrying no structured payload -- the
    /// overwhelming majority of timeline items -- so the view can skip the whole card path with one
    /// null check per row.</summary>
    public static TimelineCardContent? Build(TimelineItemView item, SurfaceText text)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(text);
        if (item.Payload is not { } payload)
        {
            return null;
        }

        List<TimelineStat> stats = [];
        List<TimelineDetailRow> rows = [];
        string? elided = null;
        if (payload.Diff is { } diff)
        {
            AddDiff(diff, text, stats, rows, ref elided);
        }

        if (payload.ToolUse is { } toolUse)
        {
            AddToolUse(toolUse, text, stats, rows, ref elided);
        }

        if (payload.Usage is { } usage)
        {
            AddUsage(usage, text, stats);
        }

        return stats.Count == 0 && rows.Count == 0 && elided is null
            ? null
            : new(stats, rows, elided);
    }

    private static void AddDiff(
        DiffPayload diff, SurfaceText text, List<TimelineStat> stats, List<TimelineDetailRow> rows, ref string? elided)
    {
        // All three restate `workflow.attempt_diff_recorded`'s own sentence verbatim ("Changed {0}
        // file(s): +{1}/-{2} lines."), so they are reinforcement for a sighted reader and noise for a
        // screen reader -- see TimelineStat.RestatesSummary.
        stats.Add(new(
            text.Resolve(MessageKeys.TimelineCardDiffFilesLabel), Number(diff.FilesChanged),
            TimelineStatTone.Neutral, RestatesSummary: true));
        stats.Add(new(
            text.Resolve(MessageKeys.TimelineCardDiffAddedLabel),
            string.Create(CultureInfo.InvariantCulture, $"+{diff.Insertions}"),
            TimelineStatTone.Positive, RestatesSummary: true));
        stats.Add(new(
            text.Resolve(MessageKeys.TimelineCardDiffDeletedLabel),
            string.Create(CultureInfo.InvariantCulture, $"-{diff.Deletions}"),
            TimelineStatTone.Negative, RestatesSummary: true));
        foreach (DiffFileStat file in diff.Files)
        {
            string kind = ChangeKindLabel(text, file.ChangeKind);
            // A binary file is recorded with zero counts because "how many lines changed" has no
            // answer for it (ADR 0059), so it shows its kind alone -- "+0 -0" would be a claim that
            // nothing changed in it.
            string secondary = string.Equals(file.ChangeKind, DiffChangeKinds.Binary, StringComparison.Ordinal)
                ? kind
                : string.Create(CultureInfo.InvariantCulture, $"+{file.Added} -{file.Deleted} {kind}");
            rows.Add(new(file.Path, secondary, TimelineStatTone.Neutral));
        }

        if (diff.ElidedFiles > 0)
        {
            elided = string.Format(
                CultureInfo.InvariantCulture, text.Resolve(MessageKeys.TimelineCardDiffElidedNote), diff.ElidedFiles);
        }
    }

    private static void AddToolUse(
        ToolUsePayload toolUse, SurfaceText text, List<TimelineStat> stats, List<TimelineDetailRow> rows,
        ref string? elided)
    {
        // Restate `workflow.attempt_tool_use_recorded` verbatim, like the diff counts above.
        stats.Add(new(
            text.Resolve(MessageKeys.TimelineCardToolCallsLabel), Number(toolUse.ToolCalls),
            TimelineStatTone.Neutral, RestatesSummary: true));
        stats.Add(new(
            text.Resolve(MessageKeys.TimelineCardToolCommandsLabel), Number(toolUse.Commands),
            TimelineStatTone.Neutral, RestatesSummary: true));
        stats.Add(new(
            text.Resolve(MessageKeys.TimelineCardToolEditsLabel), Number(toolUse.Edits),
            TimelineStatTone.Neutral, RestatesSummary: true));
        if (toolUse.UnmappedItems > 0)
        {
            // ADR 0060's drift counter: stream items the adapter's mapping did not recognize. The
            // summary sentence says nothing about it, so unlike every chip above this one carries new
            // information and stays in the accessible tree.
            stats.Add(new(
                text.Resolve(MessageKeys.TimelineCardToolUnmappedLabel), Number(toolUse.UnmappedItems),
                TimelineStatTone.Caution, RestatesSummary: false));
        }

        foreach (ToolCallStat call in toolUse.Calls)
        {
            rows.Add(new(ToolCallPrimaryText(text, call), ToolCallSecondaryText(text, call), ToolCallTone(call)));
        }

        if (toolUse.ElidedCalls > 0)
        {
            elided = string.Format(
                CultureInfo.InvariantCulture, text.Resolve(MessageKeys.TimelineCardToolElidedNote), toolUse.ElidedCalls);
        }
    }

    /// <summary>A command never carries a target -- the only text that would identify which command
    /// is the command line itself, which is never persisted (ADR 0060) -- so only an edit ever
    /// appends one, and an edit whose path was rejected as unsafe appends nothing rather than a
    /// placeholder.</summary>
    private static string ToolCallPrimaryText(SurfaceText text, ToolCallStat call)
    {
        string kind = ToolCallKindLabel(text, call.Kind);
        return string.Equals(call.Kind, ProviderToolCallKinds.Edit, StringComparison.Ordinal) &&
                call.Target is { Length: > 0 } target
            ? string.Create(CultureInfo.InvariantCulture, $"{kind} {target}")
            : kind;
    }

    /// <summary>Every part is omitted outright when the payload reports it as <see langword="null"/>:
    /// a call with no observed duration and no exit code has no secondary text at all, never an
    /// "0 ms"/"exit code 0" fabrication.</summary>
    private static string? ToolCallSecondaryText(SurfaceText text, ToolCallStat call)
    {
        List<string> parts = [];
        if (call.DurationMilliseconds is { } duration)
        {
            parts.Add(string.Format(
                CultureInfo.InvariantCulture, text.Resolve(MessageKeys.TimelineCardToolDurationText), duration));
        }

        if (call.ExitCode is { } exitCode)
        {
            parts.Add(string.Format(
                CultureInfo.InvariantCulture, text.Resolve(MessageKeys.TimelineCardToolExitCodeText), exitCode));
        }

        if (call.Succeeded is { } succeeded)
        {
            parts.Add(text.Resolve(succeeded
                ? MessageKeys.TimelineCardToolSucceededLabel
                : MessageKeys.TimelineCardToolFailedLabel));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static TimelineStatTone ToolCallTone(ToolCallStat call) => call.Succeeded switch
    {
        true => TimelineStatTone.Positive,
        false => TimelineStatTone.Negative,
        // Unknown, not neutral-by-default: ADR 0060 leaves `succeeded` null whenever the stream did
        // not establish an outcome, and claiming either one would be an invention.
        _ => TimelineStatTone.Neutral,
    };

    /// <summary>The one place this whole card family exists to protect: exactly one chip per
    /// REPORTED counter. A <see langword="null"/> counter is omitted entirely -- see this type's own
    /// remarks -- and no chip is ever a ratio.</summary>
    private static void AddUsage(UsagePayload usage, SurfaceText text, List<TimelineStat> stats)
    {
        AddReportedCounter(stats, text, MessageKeys.TimelineCardUsageInputLabel, usage.InputTokens);
        AddReportedCounter(stats, text, MessageKeys.TimelineCardUsageOutputLabel, usage.OutputTokens);
        AddReportedCounter(stats, text, MessageKeys.TimelineCardUsageCacheReadLabel, usage.CacheReadTokens);
        AddReportedCounter(stats, text, MessageKeys.TimelineCardUsageCacheCreationLabel, usage.CacheCreationTokens);
        AddReportedCounter(stats, text, MessageKeys.TimelineCardUsageContextWindowLabel, usage.ContextWindow);
    }

    private static void AddReportedCounter(List<TimelineStat> stats, SurfaceText text, string labelKey, int? value)
    {
        if (value is not { } reported)
        {
            return;
        }

        // RestatesSummary stays false for every usage chip: `workflow.attempt_usage_recorded`
        // substitutes 0 for an unreported counter, so which counters exist here is genuinely new
        // information that the sentence cannot convey.
        stats.Add(new(text.Resolve(labelKey), Number(reported), TimelineStatTone.Neutral, RestatesSummary: false));
    }

    /// <summary>Falls back to the raw code for a value outside the closed set rather than throwing,
    /// matching <see cref="TimelineMessageFormatter"/>'s own crash-proof posture for a durable
    /// envelope this process did not write.</summary>
    private static string ChangeKindLabel(SurfaceText text, string changeKind) => changeKind switch
    {
        DiffChangeKinds.Added => text.Resolve(MessageKeys.DiffChangeKindAdded),
        DiffChangeKinds.Deleted => text.Resolve(MessageKeys.DiffChangeKindDeleted),
        DiffChangeKinds.Modified => text.Resolve(MessageKeys.DiffChangeKindModified),
        DiffChangeKinds.Renamed => text.Resolve(MessageKeys.DiffChangeKindRenamed),
        DiffChangeKinds.Binary => text.Resolve(MessageKeys.DiffChangeKindBinary),
        _ => changeKind,
    };

    /// <summary>Same fallback rule as <see cref="ChangeKindLabel"/>.</summary>
    private static string ToolCallKindLabel(SurfaceText text, string kind) => kind switch
    {
        ProviderToolCallKinds.Command => text.Resolve(MessageKeys.ToolCallKindCommand),
        ProviderToolCallKinds.Edit => text.Resolve(MessageKeys.ToolCallKindEdit),
        _ => kind,
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One human gate whose decision can be rendered beside the very timeline item that asked
/// for it: the requesting event's <see cref="TimelineItemView.Sequence"/>, the gate node it belongs
/// to, and the two node-scoped <see cref="AvailableAction"/> rows the Host projected for it (ADR
/// 0058).</summary>
public sealed record TimelineGateLink(long Sequence, string NodeId, AvailableAction Approve, AvailableAction Reject);

/// <summary>
/// Pairs ADR 0058's <c>approve_gate:</c>/<c>reject_gate:</c> action rows with the timeline page a
/// surface already holds, so the decision can be rendered inline (desktop design-parity review
/// finding D2's rendering half). Pure and MAUI-free for the same reason
/// <see cref="TimelineCardProjector"/> is.
/// </summary>
public static class TimelineGateLinks
{
    /// <summary>
    /// Emits a link only when all four conditions hold: both the approve and the reject row exist for
    /// the same node, the node id is known, the Host named a requesting
    /// <see cref="AvailableActionTarget.TimelineSequence"/>, and an item with exactly that sequence is
    /// in <paramref name="items"/>.
    /// <para>The last condition is deliberate, not defensive. A gate whose requesting event has
    /// scrolled past the loaded page has nowhere inline to be rendered, and inventing an anchor would
    /// attach the decision to an unrelated item -- the bottom action panel remains the fallback
    /// surface for exactly that case, which is one reason this slice only adds an inline affordance
    /// rather than replacing that panel.</para>
    /// </summary>
    public static IReadOnlyList<TimelineGateLink> Resolve(
        IReadOnlyList<AvailableAction> actions, IReadOnlyList<TimelineItemView> items)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(items);
        Dictionary<string, AvailableAction> approvals = ByNodeId(actions, AvailableActionProjector.ApproveGateActionPrefix);
        if (approvals.Count == 0)
        {
            return [];
        }

        Dictionary<string, AvailableAction> rejections = ByNodeId(actions, AvailableActionProjector.RejectGateActionPrefix);
        HashSet<long> loadedSequences = [.. items.Select(item => item.Sequence)];
        List<TimelineGateLink> links = [];
        foreach ((string nodeId, AvailableAction approve) in approvals)
        {
            if (!rejections.TryGetValue(nodeId, out AvailableAction? reject) ||
                approve.Target.TimelineSequence is not { } sequence ||
                !loadedSequences.Contains(sequence))
            {
                continue;
            }

            links.Add(new(sequence, nodeId, approve, reject));
        }

        // Ordered so a rebuild of the same page always produces the same arrangement; two concurrently
        // pending gates each anchor to their own event, never to each other's (ADR 0058 node-scoped
        // the action ids for exactly that case).
        links.Sort((left, right) => left.Sequence != right.Sequence
            ? left.Sequence.CompareTo(right.Sequence)
            : string.CompareOrdinal(left.NodeId, right.NodeId));
        return links;
    }

    private static Dictionary<string, AvailableAction> ByNodeId(IReadOnlyList<AvailableAction> actions, string prefix)
    {
        Dictionary<string, AvailableAction> byNodeId = new(StringComparer.Ordinal);
        foreach (AvailableAction action in actions)
        {
            if (action.ActionId.StartsWith(prefix, StringComparison.Ordinal) &&
                action.Target.NodeId is { Length: > 0 } nodeId)
            {
                // First wins: the Host projects exactly one row per prefix per node, so a duplicate
                // could only be malformed input, and picking the first matches
                // SprintActionsViewModel.Find's own long-standing rule.
                _ = byNodeId.TryAdd(nodeId, action);
            }
        }

        return byNodeId;
    }
}
