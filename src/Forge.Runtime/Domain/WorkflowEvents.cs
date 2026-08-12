using System.Globalization;

namespace Forge.Domain;

/// <summary>Matches `aggregate.kind` in docs/contracts/v1/schemas/event.schema.json.</summary>
public enum AggregateKind
{
    Sprint,
    Node,
    Attempt,
}

public sealed record AggregateRef(AggregateKind Kind, string Id, long Version);

/// <summary>
/// One append-only, localization-safe transition record. Mirrors
/// docs/contracts/v1/schemas/event.schema.json; `Arguments["to_state"]` carries the resulting
/// state so a stream of these can be folded back into current sprint/node/attempt state without
/// any transcript or free-text dependency.
/// </summary>
public sealed record WorkflowEvent(
    Guid EventId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    AggregateRef Aggregate,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments,
    Guid? CorrelationId = null,
    Guid? CausationId = null)
{
    public const string ToStateArgument = "to_state";
    public const string RouteDecisionRecordedType = "RouteDecisionRecorded";

    /// <summary>Carried on a node's own transition events so retry policy needs no attempt lookup.</summary>
    public const string AttemptNumberArgument = "attempt_number";

    /// <summary>Carried on an attempt's creation event so its owning node is a durable fact, not
    /// something only the caller who happens to pair matching ids remembers.</summary>
    public const string NodeIdArgument = "node_id";

    /// <summary>Carried on the first transition an attempt makes away from `created`, so the
    /// outcome a compound operation committed to is a durable fact a retry must honor — never
    /// something a caller's later, possibly different argument can silently flip.</summary>
    public const string TargetOutcomeArgument = "target_outcome";

    /// <summary>Carried on a sprint's `blocked` transition so *why* it is blocked is a durable fact,
    /// not something re-derived from node state alone — a sprint can be `blocked` for reasons that
    /// look identical from `allSettledGood`/open-findings alone (a stuck node manually retried and
    /// skipped settles every node exactly as cleanly as a late finding does), and only a `blocked`
    /// sprint whose *actual* cause was an open finding may recover automatically once that finding
    /// resolves; every other cause requires the operator's explicit `resume_sprint` decision.</summary>
    public const string BlockedReasonArgument = "blocked_reason";
}

public sealed record SprintWorkflowState(
    SprintSnapshot Sprint,
    IReadOnlyDictionary<string, NodeSnapshot> Nodes,
    IReadOnlyDictionary<string, AttemptSnapshot> Attempts,
    long LastSequence);

/// <summary>Converts enum state names to/from the lower snake_case wire form used everywhere.</summary>
public static class WorkflowStateNames
{
    public static string ToSnakeCase<TState>(TState state) where TState : struct, Enum =>
        string.Concat(
            state.ToString().Select(
                (character, index) =>
                    char.IsUpper(character) && index > 0
                        ? $"_{char.ToLowerInvariant(character)}"
                        : char.ToLowerInvariant(character).ToString()));

    public static TState Parse<TState>(string value) where TState : struct, Enum
    {
        foreach (TState candidate in Enum.GetValues<TState>())
        {
            if (ToSnakeCase(candidate) == value)
            {
                return candidate;
            }
        }

        throw new FormatException($"'{value}' is not a known {typeof(TState).Name}.");
    }
}

/// <summary>
/// Pure reconstruction of current sprint/node/attempt state from the durable event stream. The
/// event log is the sole source of truth; nothing here reads or writes a file.
/// </summary>
public static class WorkflowFold
{
    public static SprintWorkflowState Apply(SprintId sprintId, IReadOnlyList<WorkflowEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        SprintSnapshot? sprint = null;
        Dictionary<string, NodeSnapshot> nodes = new(StringComparer.Ordinal);
        Dictionary<string, AttemptSnapshot> attempts = new(StringComparer.Ordinal);
        foreach (WorkflowEvent current in events)
        {
            if (!IsTransitionRecord(current))
            {
                continue;
            }

            string toState = current.Arguments[WorkflowEvent.ToStateArgument]!;
            switch (current.Aggregate.Kind)
            {
                case AggregateKind.Sprint:
                    // Meaningful only for this event. Finding recovery deliberately carries its
                    // reason across the intermediate `ready` state so a crash can resume safely;
                    // other transitions omit it and clear the prior reason.
                    string? blockedReason = current.Arguments.TryGetValue(
                        WorkflowEvent.BlockedReasonArgument,
                        out string? blockedReasonValue)
                        ? blockedReasonValue
                        : null;
                    sprint = new(
                        sprintId,
                        WorkflowStateNames.Parse<SprintState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        blockedReason);
                    break;
                case AggregateKind.Node:
                    int attemptCount = current.Arguments.TryGetValue(
                        WorkflowEvent.AttemptNumberArgument,
                        out string? countText) && countText is not null
                        ? int.Parse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture)
                        : nodes.TryGetValue(current.Aggregate.Id, out NodeSnapshot? previous)
                            ? previous.AttemptCount
                            : 0;
                    nodes[current.Aggregate.Id] = new(
                        new(current.Aggregate.Id),
                        WorkflowStateNames.Parse<NodeState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        attemptCount);
                    break;
                case AggregateKind.Attempt:
                    attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? previousAttempt);
                    string? nodeId = current.Arguments.TryGetValue(
                        WorkflowEvent.NodeIdArgument,
                        out string? nodeIdValue) && nodeIdValue is not null
                        ? nodeIdValue
                        : previousAttempt?.NodeId;
                    string? targetOutcome = current.Arguments.TryGetValue(
                        WorkflowEvent.TargetOutcomeArgument,
                        out string? targetOutcomeValue) && targetOutcomeValue is not null
                        ? targetOutcomeValue
                        : previousAttempt?.TargetOutcome;
                    attempts[current.Aggregate.Id] = new(
                        new(Guid.Parse(current.Aggregate.Id)),
                        WorkflowStateNames.Parse<AttemptState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        nodeId,
                        targetOutcome);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unknown aggregate kind '{current.Aggregate.Kind}'.");
            }
        }

        return new(
            sprint ?? throw new InvalidDataException("A sprint event stream must contain a sprint event."),
            nodes,
            attempts,
            events.Count == 0 ? -1 : events[^1].Sequence);
    }

    internal static bool IsTransitionRecord(WorkflowEvent current)
    {
        bool hasState = current.Arguments.TryGetValue(WorkflowEvent.ToStateArgument, out string? toState) &&
            toState is not null;
        if (current.Type != WorkflowEvent.RouteDecisionRecordedType)
        {
            return hasState
                ? true
                : throw new InvalidDataException(
                    $"Transition event '{current.EventId}' is missing '{WorkflowEvent.ToStateArgument}'.");
        }

        if (hasState || current.Aggregate.Kind != AggregateKind.Sprint || current.Aggregate.Version < 1 ||
            current.MessageKey != "routing.decision_recorded")
        {
            throw new InvalidDataException($"Routing event '{current.EventId}' has an invalid envelope.");
        }

        string Required(string key) => current.Arguments.GetValueOrDefault(key) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Routing event '{current.EventId}' is missing '{key}'.");
        _ = Guid.Parse(Required("attempt_id"));
        _ = Required("node_id");
        _ = Required("provider");
        _ = Required("model");
        _ = Required("surface");
        _ = WorkflowStateNames.Parse<RouteOutcome>(Required("outcome"));
        if (current.Arguments.GetValueOrDefault("failure_class") is { } failure)
        {
            _ = WorkflowStateNames.Parse<FailureClass>(failure);
        }

        return false;
    }
}
