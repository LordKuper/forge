namespace Forge.Domain;

/// <summary>
/// A sprint's append-only stage-revision counter (plan section 8.4). Rewinding a sprint to an
/// earlier workflow stage starts a new revision instead of deleting or rewriting prior events,
/// results, findings, decisions, or artifacts; node identity stays stable across a rewind, while
/// each node's own execution state and evidence gain a revision. A query or prerequisite check
/// always selects the latest non-superseded revision.
///
/// This type is a plain value, not a coordinator: nothing in this slice increments a revision.
/// The idempotent rewind coordinator that owns incrementing it, reopening the target stage, and
/// marking downstream evidence superseded is introduced in a later slice (plan section 11, Slice
/// 3), mirroring how <see cref="WorkflowStateMachines"/> owns transitions rather than the state
/// enums themselves.
/// </summary>
public readonly record struct StageRevision(int Value)
{
    public static readonly StageRevision Initial = new(0);

    public StageRevision Next() => new(Value + 1);
}

/// <summary>
/// Marks one piece of stage evidence (a node result, finding, handoff, or other stage-scoped
/// artifact produced in a later slice) as excluded from prerequisite evaluation because a rewind
/// opened a later revision (plan section 8.4: "excludes superseded evidence from all future
/// prerequisite checks"). Evidence carrying this marker is never deleted or rewritten -- it
/// remains readable history, just no longer eligible to satisfy a prerequisite.
///
/// This is a plain contract type; no artifact or result record is wired to carry it yet, and no
/// code assigns it. Attaching it to the relevant persisted shapes (and their wire schemas) is
/// Slice 3's job (plan section 11: "Add stage revision to node state and relevant artifacts").
/// </summary>
public sealed record SupersededBy(StageRevision AtRevision, DateTimeOffset RecordedAt);
