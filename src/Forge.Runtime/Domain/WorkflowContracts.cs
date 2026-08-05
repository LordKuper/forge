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
    DateTimeOffset UpdatedAt);

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
    Abandoned,
    Cancelled,
}

public sealed record NodeId(Guid Value)
{
    public static NodeId New() => new(Guid.NewGuid());
}

public sealed record AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.NewGuid());
}

public sealed record NodeSnapshot(
    NodeId Id,
    NodeState State,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record AttemptSnapshot(
    AttemptId Id,
    AttemptState State,
    long Version,
    DateTimeOffset UpdatedAt);
