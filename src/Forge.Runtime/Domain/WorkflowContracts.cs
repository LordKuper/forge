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
    /// <summary>Read compatibility for journals written before v0.11.0; no new transition reaches it.</summary>
    Abandoned,
    Cancelled,
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
    string? TargetOutcome = null);
