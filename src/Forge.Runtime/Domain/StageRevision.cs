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
/// Marks one piece of stage evidence (a <c>Forge.Domain.NodeResult</c>, <c>Finding</c>,
/// <c>Handoff</c>, <c>ConfirmationArtifact</c>, or <c>TestWorkArtifact</c>) as excluded from
/// prerequisite evaluation because a rewind opened a later revision (plan section 8.4: "excludes
/// superseded evidence from all future prerequisite checks"). Evidence carrying this marker is
/// never deleted or rewritten beyond adding the marker itself -- it remains readable history, just
/// no longer eligible to satisfy a prerequisite. Attached by
/// <c>Forge.Application.StageTransitionCoordinator</c> (plan section 11, Slice 3).
/// </summary>
public sealed record SupersededBy(StageRevision AtRevision, DateTimeOffset RecordedAt);
