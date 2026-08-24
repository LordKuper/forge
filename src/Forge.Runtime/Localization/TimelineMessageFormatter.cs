using System.Globalization;
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

    public static string Format(SurfaceText text, string messageKey, IReadOnlyDictionary<string, string?> arguments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        ArgumentNullException.ThrowIfNull(arguments);
        string template = text.Resolve(messageKey);
        object?[] values = messageKey switch
        {
            // Fixed, closed-form reason codes (never free text): "node"/"finding"/"gate"/
            // "confirmation"/"review_convergence"/"rewind" -- see SprintScheduler/
            // StageTransitionCoordinator's own BlockedBy* constants. Always present: every
            // `workflow.sprint_blocked` producing call site sets it.
            MessageKeys.WorkflowSprintBlocked =>
                [Value(arguments, WorkflowEvent.BlockedReasonArgument)],

            // Always present: StageTransitionCoordinator.RewindNodeAsync sets it unconditionally on
            // both message keys.
            MessageKeys.WorkflowNodeReopened or MessageKeys.WorkflowNodeInvalidated =>
                [Value(arguments, WorkflowEvent.RevisionArgument)],

            // Always present: WorkflowEvent.ToStateArgument is set unconditionally by
            // FileSprintEventLog.AppendTransitionAsync for every transition event.
            MessageKeys.WorkflowAttemptTransitioned =>
                [Value(arguments, WorkflowEvent.ToStateArgument)],

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
                    Value(arguments, RoutingOutcomeArgument),
                ],

            _ => [],
        };
        return values.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, values);
    }

    private static string Value(IReadOnlyDictionary<string, string?> arguments, string key) =>
        arguments.GetValueOrDefault(key) ?? string.Empty;
}
