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

    /// <summary>An attempt heartbeat: ADR 0006's "safe, throttled activity events" that bump
    /// <see cref="AttemptSnapshot.LastActivityAt"/> without persisting provider content or moving
    /// the attempt through its state machine. Never carries <see cref="ToStateArgument"/>;
    /// <see cref="OccurredAt"/> is itself the activity timestamp. May carry
    /// <see cref="AttemptActivityKindArgument"/> (Stage 11, P11.32-P11.40) — still never provider
    /// content, only a fixed, typed classification of what kind of activity occurred.</summary>
    public const string AttemptActivityRecordedType = "AttemptActivityRecorded";

    /// <summary>Carried on an <see cref="AttemptActivityRecordedType"/> event — see
    /// <see cref="AttemptActivityKind"/>. Optional: an event without it is a plain, untyped
    /// heartbeat, matching every activity event recorded before Stage 11 P11.32-P11.40.</summary>
    public const string AttemptActivityKindArgument = "activity_kind";

    /// <summary>Carried on a node's own transition events so retry policy needs no attempt lookup.</summary>
    public const string AttemptNumberArgument = "attempt_number";

    /// <summary>Carried on a node's `running` transition: the id of the attempt it was started
    /// with, so <see cref="NodeSnapshot.CurrentAttemptId"/> can answer "which attempt does this
    /// node's `running` state belong to" directly, without re-deriving an id from
    /// <see cref="NodeSnapshot.AttemptCount"/> and risking a mismatch once a replacement attempt
    /// (Stage 11, P11.48-P11.55) is involved.</summary>
    public const string CurrentAttemptIdArgument = "current_attempt_id";

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

    /// <summary>ADR 0006's human-only operator-steering command (Stage 11, P11.48-P11.55): "Forge
    /// ... records `AttemptSuperseded`." Appended on the superseded attempt's own aggregate,
    /// alongside (never instead of) its ordinary `AttemptChanged` transition to `cancelled` — this
    /// event carries the bounded operator instruction and is never a transition itself (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptActivityRecordedType"/>'s own
    /// non-transition shape.</summary>
    public const string AttemptSupersededType = "AttemptSuperseded";

    /// <summary>Carried on an <see cref="AttemptSupersededType"/> event — the bounded instruction
    /// artifact ADR 0006 requires ("never hides the original input and outcome": this augments the
    /// record, it never edits or removes it).</summary>
    public const string SupersessionInstructionArgument = "supersession_instruction";

    /// <summary>Carried on the *replacement* attempt's own creation event — ADR 0006's "linkage":
    /// a clean-replacement attempt durably names exactly which attempt it replaced. Absent on an
    /// ordinarily-started attempt.</summary>
    public const string SupersedesAttemptIdArgument = "supersedes_attempt_id";

    /// <summary>Carried on an attempt's creation event when its worktree's git base commit is
    /// already known at creation time — currently only true for a
    /// <see cref="SupersedesAttemptIdArgument"/> clean replacement, which reuses the superseded
    /// attempt's own recorded base rather than drifting to wherever integration currently sits
    /// (ADR 0006: "from the superseded attempt's recorded base"). Absent otherwise: nothing else
    /// today records what commit an attempt's worktree would be created at.</summary>
    public const string BaseCommitArgument = "base_commit";

    /// <summary>Plan section 7.3's durable stop intent: recorded once for the exact attempt a
    /// `StopCurrentOperation` request targets, before the stop coordinator relies on the in-memory
    /// <c>ActiveOperationRegistry</c> at all. Appended on the attempt's own aggregate, alongside
    /// (never instead of) its ordinary `AttemptChanged` transition to `cancelled` once the stop
    /// actually converges — this event itself is never a transition (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptSupersededType"/>'s own shape.
    /// Unlike that type, this one is folded into <see cref="AttemptSnapshot.StopRequestedAt"/>: an
    /// executor or restart-recovery pass must be able to ask "does this running attempt already
    /// carry a stop intent" directly, not merely read it back as audit trail.</summary>
    public const string AttemptStopRequestedType = "AttemptStopRequested";

    /// <summary>ADR 0047 addendum: the stop saga's own durable "fully converged" marker, appended
    /// once by <see cref="Forge.Application.StopOperationCoordinator.FinishStopAsync"/> as its last
    /// step, unconditionally, regardless of which of its earlier steps this exact call did or did not
    /// need to (re-)run. Recorded on the attempt's own aggregate, never a transition itself (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptStopRequestedType"/>'s own shape.
    /// Also folded (<see cref="AttemptSnapshot.StopConvergedAt"/>), for the same reason
    /// <see cref="AttemptStopRequestedType"/> is: every node-role executor must be able to ask "does
    /// this node's current attempt still need <c>FinishStopAsync</c>" directly, from durable state,
    /// independent of the node's own current state -- see that field's own remarks for why a check
    /// gated only on <see cref="AttemptStopRequestedType"/> having landed is not enough on its own.</summary>
    public const string AttemptStopConvergedType = "AttemptStopConverged";

    /// <summary>Plan section 8.4's committed-rewind marker (Slice 3): recorded once per committed
    /// <c>MoveSprintToStage</c> rewind, on the sprint's own aggregate. Never a transition itself (no
    /// <see cref="ToStateArgument"/>) -- a rewind's sprint-level effect is a revision bump, not
    /// necessarily a sprint-state change, matching <see cref="AttemptSupersededType"/>'s own
    /// non-transition shape. Folded into <see cref="SprintSnapshot.Revision"/> (never decremented,
    /// never skipped): every node-role executor and prerequisite check reads a sprint's *current*
    /// revision directly from this projection, the same way <see cref="AttemptStopRequestedType"/>
    /// is folded rather than left as audit-only.</summary>
    public const string StageRevisionRecordedType = "StageRevisionRecorded";

    /// <summary>Carried on a <see cref="StageRevisionRecordedType"/> event: the new revision value
    /// (<see cref="StageRevision.Value"/>, as a base-10 integer) this rewind commits to. Also carried
    /// on a node's own `succeeded -> ready`/`succeeded -> pending`/`failed -> pending`/
    /// `awaiting_human -> pending` transitions (plan section 8.4's reopen/invalidate edges) so
    /// <see cref="NodeSnapshot.Revision"/> tracks which revision that node's own execution state now
    /// belongs to -- node identity stays stable; only this argument's value changes.</summary>
    public const string RevisionArgument = "revision";

    /// <summary>Carried on a <see cref="StageRevisionRecordedType"/> event: the stage the rewind
    /// targeted, for the timeline's own actor-visible rendering.</summary>
    public const string TargetStageIdArgument = "target_stage_id";

    /// <summary>Carried on a <see cref="StageRevisionRecordedType"/> event: the operator's bounded,
    /// mandatory reason for the rewind (plan section 8.4 point 1) -- augments the durable record, the
    /// same "never hides the original input" discipline <see cref="SupersessionInstructionArgument"/>
    /// already follows for attempt supersession.</summary>
    public const string RewindReasonArgument = "rewind_reason";
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
            if (current.Type == WorkflowEvent.AttemptActivityRecordedType)
            {
                // Validated like every other envelope (throws loudly on corruption) but never a
                // transition: it must never gate on or advance a state-machine version. Applied only
                // while the attempt is still non-terminal — the authoritative, race-free half of
                // "never resurrects a settled attempt": a heartbeat that lands after a concurrent
                // completion (append-time only checks the attempt was non-terminal at read time) is
                // silently dropped here on replay instead of leaving a stray post-terminal timestamp.
                _ = IsTransitionRecord(current);
                if (attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? activeAttempt) &&
                    !WorkflowStateMachines.IsTerminal(activeAttempt.State))
                {
                    AttemptActivityKind? kind =
                        current.Arguments.TryGetValue(WorkflowEvent.AttemptActivityKindArgument, out string? kindText) &&
                            kindText is not null
                            ? WorkflowStateNames.Parse<AttemptActivityKind>(kindText)
                            : null;
                    attempts[current.Aggregate.Id] =
                        activeAttempt with { LastActivityAt = current.OccurredAt, LastActivityKind = kind };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.AttemptSupersededType)
            {
                // Validated (throws loudly on corruption) but, like an activity event, never a
                // transition and never projected into the folded snapshot itself: the bounded
                // instruction it carries is durable audit content, not workflow state.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.AttemptStopRequestedType)
            {
                // Validated like every other envelope, never a transition -- but unlike
                // AttemptSuperseded, this one IS projected: StopRequestedAt must be directly
                // queryable so an executor or restart-recovery pass can ask "does this attempt
                // already carry a stop intent" without re-scanning the raw journal.
                _ = IsTransitionRecord(current);
                if (attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? stoppingAttempt))
                {
                    attempts[current.Aggregate.Id] = stoppingAttempt with { StopRequestedAt = current.OccurredAt };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.AttemptStopConvergedType)
            {
                // Same treatment as AttemptStopRequestedType, for the same reason: projected into
                // AttemptSnapshot.StopConvergedAt so an executor can tell a fully-converged stop
                // apart from one still needing FinishStopAsync, directly from durable state.
                _ = IsTransitionRecord(current);
                if (attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? convergedAttempt))
                {
                    attempts[current.Aggregate.Id] = convergedAttempt with { StopConvergedAt = current.OccurredAt };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.StageRevisionRecordedType)
            {
                // Never a transition (no ToStateArgument) -- but projected, like
                // AttemptStopRequestedType: every prerequisite check and node-role executor must be
                // able to read a sprint's current stage revision directly from its snapshot, not by
                // re-scanning the raw journal.
                _ = IsTransitionRecord(current);
                if (sprint is not null)
                {
                    int revisionValue = int.Parse(
                        current.Arguments[WorkflowEvent.RevisionArgument]!, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    sprint = sprint with { Revision = new(revisionValue) };
                }

                continue;
            }

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
                    // Carried forward from whatever StageRevisionRecordedType last set (never
                    // produced by an ordinary sprint transition) -- an ordinary transition must never
                    // reset a sprint's own revision counter back to Initial.
                    sprint = new(
                        sprintId,
                        WorkflowStateNames.Parse<SprintState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        blockedReason,
                        sprint?.Revision ?? default);
                    break;
                case AggregateKind.Node:
                    nodes.TryGetValue(current.Aggregate.Id, out NodeSnapshot? previousNode);
                    int attemptCount = current.Arguments.TryGetValue(
                        WorkflowEvent.AttemptNumberArgument,
                        out string? countText) && countText is not null
                        ? int.Parse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture)
                        : previousNode?.AttemptCount ?? 0;
                    string? currentAttemptId = current.Arguments.TryGetValue(
                        WorkflowEvent.CurrentAttemptIdArgument,
                        out string? currentAttemptIdValue) && currentAttemptIdValue is not null
                        ? currentAttemptIdValue
                        : previousNode?.CurrentAttemptId;
                    // Carried only on the rewind coordinator's own reopen/invalidate transitions
                    // (`succeeded -> ready`/`succeeded -> pending`/`failed -> pending`/
                    // `awaiting_human -> pending`); every ordinary transition omits it and this node
                    // simply keeps whatever revision it already belonged to.
                    StageRevision nodeRevision = current.Arguments.TryGetValue(
                        WorkflowEvent.RevisionArgument,
                        out string? revisionText) && revisionText is not null
                        ? new(int.Parse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture))
                        : previousNode?.Revision ?? default;
                    nodes[current.Aggregate.Id] = new(
                        new(current.Aggregate.Id),
                        WorkflowStateNames.Parse<NodeState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        attemptCount,
                        currentAttemptId,
                        nodeRevision);
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
                    string? baseCommit = current.Arguments.TryGetValue(
                        WorkflowEvent.BaseCommitArgument,
                        out string? baseCommitValue) && baseCommitValue is not null
                        ? baseCommitValue
                        : previousAttempt?.BaseCommit;
                    AttemptId? supersedesAttemptId = current.Arguments.TryGetValue(
                        WorkflowEvent.SupersedesAttemptIdArgument,
                        out string? supersedesValue) && supersedesValue is not null
                        ? new AttemptId(Guid.Parse(supersedesValue))
                        : previousAttempt?.SupersedesAttemptId;
                    attempts[current.Aggregate.Id] = new(
                        new(Guid.Parse(current.Aggregate.Id)),
                        WorkflowStateNames.Parse<AttemptState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        nodeId,
                        targetOutcome,
                        previousAttempt?.LastActivityAt,
                        previousAttempt?.LastActivityKind,
                        baseCommit,
                        supersedesAttemptId,
                        previousAttempt?.StopRequestedAt,
                        previousAttempt?.StopConvergedAt);
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
        if (current.Type == WorkflowEvent.AttemptActivityRecordedType)
        {
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt
                ? throw new InvalidDataException($"Activity event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptSupersededType)
        {
            bool hasInstruction = current.Arguments.TryGetValue(
                WorkflowEvent.SupersessionInstructionArgument, out string? instruction) && instruction is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt || !hasInstruction
                ? throw new InvalidDataException($"Supersession event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptStopRequestedType)
        {
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt
                ? throw new InvalidDataException($"Stop-request event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptStopConvergedType)
        {
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt
                ? throw new InvalidDataException($"Stop-converged event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.StageRevisionRecordedType)
        {
            bool hasRevision = current.Arguments.TryGetValue(
                WorkflowEvent.RevisionArgument, out string? revision) && revision is not null;
            bool hasTarget = current.Arguments.TryGetValue(
                WorkflowEvent.TargetStageIdArgument, out string? target) && target is not null;
            bool hasReason = current.Arguments.TryGetValue(
                WorkflowEvent.RewindReasonArgument, out string? reason) && reason is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Sprint ||
                !hasRevision || !hasTarget || !hasReason
                ? throw new InvalidDataException($"Stage-revision event '{current.EventId}' has an invalid envelope.")
                : false;
        }

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
