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
