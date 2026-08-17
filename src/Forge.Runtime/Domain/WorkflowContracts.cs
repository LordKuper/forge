namespace Forge.Domain;

public enum SprintState
{
    Draft,
    Ready,
    Running,
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

public sealed record SprintSnapshot(
    SprintId Id,
    SprintState State,
    long Version,
    DateTimeOffset UpdatedAt,
    string? BlockedReason = null);

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

public sealed record NodeSnapshot(
    NodeId Id,
    NodeState State,
    long Version,
    DateTimeOffset UpdatedAt,
    int AttemptCount = 0);

public sealed record AttemptSnapshot(
    AttemptId Id,
    AttemptState State,
    long Version,
    DateTimeOffset UpdatedAt,
    string? NodeId = null,
    string? TargetOutcome = null,
    DateTimeOffset? LastActivityAt = null,
    AttemptActivityKind? LastActivityKind = null);
