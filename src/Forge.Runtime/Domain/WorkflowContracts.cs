namespace Forge.Domain;

public enum SprintState
{
    Draft,
    Ready,
    Running,

    /// <summary>Reached only from <see cref="Running"/> by the stop-current-operation coordinator
    /// (plan section 7, implemented in a later slice) after it cancels the sprint's exact active
    /// attempt without settling the sprint as failed or consuming automatic retry budget. No
    /// generic public API produces this value: like every other transition, only an
    /// <c>AppendTransitionAsync</c> call validated against <see cref="WorkflowStateMachines"/> can
    /// durably record it (see that type's own remarks).</summary>
    Paused,
    AwaitingHuman,
    Blocked,
    Failed,
    ReadyToFinalize,
    Completed,
    Cancelled,
}

public sealed record SprintId(Guid Value)
{
    public static SprintId New() => new(Guid.NewGuid());
}

/// <summary><paramref name="Revision"/> is the sprint's current stage revision (plan section
/// 8.4). Starts at <see cref="StageRevision.Initial"/> and is incremented only by the rewind
/// coordinator introduced in a later slice — nothing in this slice produces a value other than
/// the default.</summary>
public sealed record SprintSnapshot(
    SprintId Id,
    SprintState State,
    long Version,
    DateTimeOffset UpdatedAt,
    string? BlockedReason = null,
    StageRevision Revision = default);

public enum NodeState
{
    Pending,
    Ready,
    Running,
    AwaitingHuman,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public enum AttemptState
{
    Created,
    Preparing,
    Running,
    Validating,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// A fixed, typed classification of what an <see cref="WorkflowEvent.AttemptActivityRecordedType"/>
/// heartbeat is *about*, never provider content itself (Stage 11, P11.32-P11.40). A single fixed
/// enum, not a free-form vendor event type — ADR 0006's "provider prose... never determine
/// workflow state" applies just as much to activity classification as to terminal results.
/// </summary>
public enum AttemptActivityKind
{
    /// <summary>A plain keep-alive with no more specific classification — the only kind every
    /// activity event recorded before this enum existed durably had, by omission.</summary>
    Heartbeat,

    /// <summary>The provider reported using a tool (<c>ProviderEventKind.ToolUse</c>) — never which
    /// tool or with what arguments, only that activity of this kind occurred.</summary>
    ToolUse,
}

/// <summary>
/// A node's identity is the stable, workflow-assigned string a graph declares it with (e.g.
/// "spec", "adr") — not a random value — so the same workflow always produces the same node
/// identities across sprints and a node result can name it without a lookup.
/// </summary>
public sealed record NodeId(string Value);

public sealed record AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.NewGuid());
}

/// <summary><paramref name="CurrentAttemptId"/> is the id of the attempt this node was most
/// recently started with (set only by the node's own `running` transition, never cleared on a
/// later transition) — the durable, unambiguous answer to "which attempt does this node's
/// `running` state currently belong to", used instead of re-deriving it from
/// <paramref name="AttemptCount"/> and a deterministic-id guess, which cannot tell apart a
/// human-initiated replacement attempt (Stage 11, P11.48-P11.55) from an ordinary one.
/// <paramref name="Revision"/> is the stage revision this node's execution state belongs to
/// (plan section 8.4). Node identity stays stable across a rewind; only execution state gains a
/// revision, so a query can select the latest non-superseded one. Nothing in this slice produces
/// a value other than the default — the rewind coordinator that increments it lands in a later
/// slice.</summary>
public sealed record NodeSnapshot(
    NodeId Id,
    NodeState State,
    long Version,
    DateTimeOffset UpdatedAt,
    int AttemptCount = 0,
    string? CurrentAttemptId = null,
    StageRevision Revision = default);

/// <summary><paramref name="BaseCommit"/> and <paramref name="SupersedesAttemptId"/> are set only
/// on an attempt `SprintScheduler.SupersedeAttemptAsync` (Stage 11, P11.48-P11.55) created as a
/// human-initiated clean replacement — "linkage" back to the exact attempt and base it replaced.
/// An ordinarily-started attempt (automatic retry or a fresh node) carries neither: nothing today
/// records what git commit an attempt's worktree would be created at, matching every prior Stage
/// 11 item's "no node executor exists yet" gap.</summary>
public sealed record AttemptSnapshot(
    AttemptId Id,
    AttemptState State,
    long Version,
    DateTimeOffset UpdatedAt,
    string? NodeId = null,
    string? TargetOutcome = null,
    DateTimeOffset? LastActivityAt = null,
    AttemptActivityKind? LastActivityKind = null,
    string? BaseCommit = null,
    AttemptId? SupersedesAttemptId = null);
