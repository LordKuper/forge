using System.Globalization;
using System.Resources;
using Forge.Domain;

namespace Forge.Localization;

/// <summary>
/// Resolves a sprint timeline item's durable `workflow.*`/`routing.*` journal message key (see
/// <see cref="WorkflowEvent.MessageKey"/>) to localized, human-readable text (plan section 12.3).
/// Lives in the neutral localization layer, not Desktop or CLI, so both surfaces render identical
/// text from the same rule set (plan section 12.6 parity) -- each surface still calls
/// <see cref="Format"/> itself from its own rendering code, matching every other
/// <see cref="SurfaceText.Resolve(string)"/> call site already used throughout this codebase.
/// </summary>
/// <remarks>
/// Most of the ~40 keys this resolves map to a single static sentence: the transition's own
/// to-state is already implicit in the key name (e.g. <c>workflow.node_succeeded</c>), so a static
/// sentence loses nothing, and the timeline's own "Details" toggle
/// (<c>WorkspaceShellPage.SprintWorkspace.RenderTimelineItems</c> / <c>CliApplication.WriteTimeline</c>)
/// already dumps every raw <see cref="WorkflowEvent.Arguments"/> entry regardless. A handful of keys
/// carry genuinely dynamic content an operator or agent actually authored, or a routing outcome with
/// no static phrasing -- those substitute the exact durable argument value into the resolved
/// <c>Messages.resx</c> template via the same positional <c>{0}</c>/<c>{1}</c> placeholder convention
/// already used for <see cref="MessageKeys.SidebarProvidersReadyStatus"/> and
/// <see cref="MessageKeys.TimelineUnreadLabel"/>. Every substituted argument here is durably
/// guaranteed present by <see cref="WorkflowFold.IsTransitionRecord"/> or by the one producing call
/// site (see each key's own remark below), so none of these substitutions can hit a missing value.
/// A machine-only code among those arguments (the blocked reason, the attempt's raw to-state, the
/// routing outcome) is itself mapped to a localized label before substitution (PR #107 review
/// findings 3/4/5) rather than interpolated verbatim -- <see cref="BlockedReasonLabel"/>,
/// <see cref="AttemptStateLabel"/>, <see cref="RoutingOutcomeLabel"/>.
/// </remarks>
public static class TimelineMessageFormatter
{
    /// <summary>Routing decision argument names -- <c>FileSprintEventLog</c>'s own literal
    /// dictionary keys for <c>routing.decision_recorded</c> (no <see cref="WorkflowEvent"/> constant
    /// exists for these; this mirrors the raw string literals already used at that one producing call
    /// site and in <see cref="WorkflowFold.IsTransitionRecord"/>'s own validation of the same event).
    /// </summary>
    private const string RoutingProviderArgument = "provider";

    private const string RoutingModelArgument = "model";

    private const string RoutingOutcomeArgument = "outcome";

    /// <summary>PR #107 review finding 1: an unmapped/unregistered <paramref name="messageKey"/> is
    /// genuinely reachable in production -- <c>ISprintStore.AppendTransitionAsync</c> accepts an
    /// arbitrary string with no closed-set validation, and <c>SprintTimelineRedaction.Apply</c>
    /// deliberately rewrites <c>MessageKey</c> through <c>SecretRedactor</c>
    /// right before it reaches this method, which is guaranteed not to resolve. The pre-this-feature
    /// behavior rendered the raw key verbatim and was crash-proof by construction; this method must
    /// stay crash-proof too, so a missing catalog entry degrades to the raw key instead of taking
    /// down the whole timeline page/command.</summary>
    public static string Format(SurfaceText text, string messageKey, IReadOnlyDictionary<string, string?> arguments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(arguments);
        if (string.IsNullOrWhiteSpace(messageKey))
        {
            // A malformed journal line's empty/blank key is just as unrenderable as an unregistered
            // one -- degrade the same way rather than throwing (see this method's own remark above).
            return messageKey ?? string.Empty;
        }

        string template;
        try
        {
            template = text.Resolve(messageKey);
        }
        catch (MissingManifestResourceException)
        {
            return messageKey;
        }

        object?[] values = messageKey switch
        {
            // Fixed, closed-form reason codes (never free text): "node"/"finding"/"gate"/
            // "confirmation"/"review_convergence"/"rewind" -- see SprintScheduler/
            // StageTransitionCoordinator's own BlockedBy* constants. Always present: every
            // `workflow.sprint_blocked` producing call site sets it.
            MessageKeys.WorkflowSprintBlocked =>
                [BlockedReasonLabel(text, Value(arguments, WorkflowEvent.BlockedReasonArgument))],

            // Always present: StageTransitionCoordinator.RewindNodeAsync sets it unconditionally on
            // both message keys.
            MessageKeys.WorkflowNodeReopened or MessageKeys.WorkflowNodeInvalidated =>
                [Value(arguments, WorkflowEvent.RevisionArgument)],

            // Always present: WorkflowEvent.ToStateArgument is set unconditionally by
            // FileSprintEventLog.AppendTransitionAsync for every transition event.
            MessageKeys.WorkflowAttemptTransitioned =>
                [AttemptStateLabel(text, Value(arguments, WorkflowEvent.ToStateArgument))],

            // Always present: required by WorkflowFold.IsTransitionRecord for this event type.
            MessageKeys.WorkflowAttemptSupersededInstruction =>
                [Value(arguments, WorkflowEvent.SupersessionInstructionArgument)],

            // Always present: required by WorkflowFold.IsTransitionRecord for this event type.
            MessageKeys.WorkflowUserMessagePosted =>
                [Value(arguments, WorkflowEvent.UserMessageTextArgument)],

            // Always present: required by WorkflowFold.IsTransitionRecord for this event type.
            MessageKeys.WorkflowAgentSummaryRecorded =>
                [Value(arguments, WorkflowEvent.AgentSummaryTextArgument)],

            // All three always present: required by WorkflowFold.IsTransitionRecord for this event
            // type.
            MessageKeys.WorkflowStageRevisionRecorded =>
                [
                    Value(arguments, WorkflowEvent.TargetStageIdArgument),
                    Value(arguments, WorkflowEvent.RewindReasonArgument),
                    Value(arguments, WorkflowEvent.RevisionArgument),
                ],

            // All three always present: FileSprintEventLog.AppendRoutingEventAsync sets them
            // unconditionally from the non-nullable RouteDecision fields they mirror.
            MessageKeys.RoutingDecisionRecorded =>
                [
                    Value(arguments, RoutingProviderArgument),
                    Value(arguments, RoutingModelArgument),
                    RoutingOutcomeLabel(text, Value(arguments, RoutingOutcomeArgument)),
                ],

            _ => [],
        };
        return values.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, values);
    }

    /// <summary>PR #107 review finding 3: <c>workflow.sprint_blocked</c>'s <c>{0}</c> was the raw
    /// snake_case <c>blocked_reason</c> code -- a machine-only token inside otherwise-localized
    /// prose. Maps the closed set of <c>BlockedBy*</c> codes (SprintScheduler/
    /// StageTransitionCoordinator) to a localized label; an unrecognized code (never expected today,
    /// but this method must stay crash-proof per finding 1) falls back to the raw code itself rather
    /// than throwing.</summary>
    private static string BlockedReasonLabel(SurfaceText text, string rawReason)
    {
        string? key = rawReason switch
        {
            "node" => MessageKeys.SprintBlockedReasonNode,
            "finding" => MessageKeys.SprintBlockedReasonFinding,
            "gate" => MessageKeys.SprintBlockedReasonGate,
            "confirmation" => MessageKeys.SprintBlockedReasonConfirmation,
            "review_convergence" => MessageKeys.SprintBlockedReasonReviewConvergence,
            "rewind" => MessageKeys.SprintBlockedReasonRewind,
            _ => null,
        };
        return key is null ? rawReason : text.Resolve(key);
    }

    /// <summary>PR #107 review finding 4: <c>workflow.attempt_transitioned</c>'s <c>{0}</c> was the
    /// raw snake_case <see cref="AttemptState"/> value -- the timeline's highest-frequency entry, so
    /// the most commonly seen untranslated fragment. Maps every <see cref="AttemptState"/> member to
    /// a localized label; a value that does not parse as a known member (never expected today, but
    /// this method must stay crash-proof per finding 1) falls back to the raw value itself.</summary>
    private static string AttemptStateLabel(SurfaceText text, string rawState)
    {
        AttemptState state;
        try
        {
            state = WorkflowStateNames.Parse<AttemptState>(rawState);
        }
        catch (FormatException)
        {
            return rawState;
        }

        string key = state switch
        {
            AttemptState.Created => MessageKeys.AttemptStateCreated,
            AttemptState.Preparing => MessageKeys.AttemptStatePreparing,
            AttemptState.Running => MessageKeys.AttemptStateRunning,
            AttemptState.Validating => MessageKeys.AttemptStateValidating,
            AttemptState.Succeeded => MessageKeys.AttemptStateSucceeded,
            AttemptState.Failed => MessageKeys.AttemptStateFailed,
            AttemptState.Cancelled => MessageKeys.AttemptStateCancelled,
            _ => null!,
        };
        return key is null ? rawState : text.Resolve(key);
    }

    /// <summary>PR #107 review finding 5: <c>routing.decision_recorded</c>'s <c>{2}</c> was the raw
    /// snake_case <see cref="RouteOutcome"/> value -- the only part of that sentence carrying the
    /// actual meaning for the reader, unlike <c>{0}</c>/<c>{1}</c> (provider/model ids, which must
    /// stay verbatim). Maps every <see cref="RouteOutcome"/> member to a localized label; a value
    /// that does not parse as a known member (never expected today, but this method must stay
    /// crash-proof per finding 1) falls back to the raw value itself.</summary>
    private static string RoutingOutcomeLabel(SurfaceText text, string rawOutcome)
    {
        RouteOutcome outcome;
        try
        {
            outcome = WorkflowStateNames.Parse<RouteOutcome>(rawOutcome);
        }
        catch (FormatException)
        {
            return rawOutcome;
        }

        string key = outcome switch
        {
            RouteOutcome.Routed => MessageKeys.RoutingOutcomeRouted,
            RouteOutcome.Succeeded => MessageKeys.RoutingOutcomeSucceeded,
            RouteOutcome.Failed => MessageKeys.RoutingOutcomeFailed,
            RouteOutcome.CircuitOpen => MessageKeys.RoutingOutcomeCircuitOpen,
            RouteOutcome.BudgetExhausted => MessageKeys.RoutingOutcomeBudgetExhausted,
            RouteOutcome.Excluded => MessageKeys.RoutingOutcomeExcluded,
            RouteOutcome.Deferred => MessageKeys.RoutingOutcomeDeferred,
            _ => null!,
        };
        return key is null ? rawOutcome : text.Resolve(key);
    }

    private static string Value(IReadOnlyDictionary<string, string?> arguments, string key) =>
        arguments.GetValueOrDefault(key) ?? string.Empty;
}
