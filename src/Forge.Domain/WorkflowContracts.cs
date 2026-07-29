namespace Forge.Domain;

public enum SprintState
{
    Created,
    Ready,
    Running,
    AwaitingHuman,
    Completed,
    Failed,
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
