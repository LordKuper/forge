using Forge.Domain;

namespace Forge.Application;

/// <summary>The four attention kinds ADR 0005/0006 assign to Desktop/OS notifications.</summary>
public enum NotificationKind
{
    AwaitingHuman,
    Blocked,
    Failed,
    Completed,
}

/// <summary>One notification-worthy durable fact: a sprint reaching one of the four attention
/// states. Carries no prose, prompt, or provider content — a caller renders it with the project
/// label and localized text it already has. <see cref="EventId"/> is the durable dedup key ADR 0005
/// requires ("deduplicated by event id"): a caller that already delivered this id skips it, so a
/// re-read page (a reconnect, a cursor replay) never re-notifies.</summary>
public sealed record NotificationProjection(
    Guid EventId,
    Guid SprintId,
    NotificationKind Kind,
    DateTimeOffset OccurredAt);

/// <summary>
/// Projects durable sprint-state events onto the attention kinds Desktop/OS notifications mirror.
/// Pure and neutral: it reads exactly what `ReadControlEvents` already returns and adds no store,
/// timer, or OS dependency of its own — actual delivery (toast, tray, sound) is a platform-owned
/// concern for a later stage to add.
/// </summary>
public static class NotificationProjector
{
    private static readonly Dictionary<string, NotificationKind> Kinds =
        new Dictionary<string, NotificationKind>(StringComparer.Ordinal)
        {
            [WorkflowStateNames.ToSnakeCase(SprintState.AwaitingHuman)] = NotificationKind.AwaitingHuman,
            [WorkflowStateNames.ToSnakeCase(SprintState.Blocked)] = NotificationKind.Blocked,
            [WorkflowStateNames.ToSnakeCase(SprintState.Failed)] = NotificationKind.Failed,
            [WorkflowStateNames.ToSnakeCase(SprintState.Completed)] = NotificationKind.Completed,
        };

    public static IReadOnlyList<NotificationProjection> Project(IEnumerable<ControlEventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        List<NotificationProjection> projections = [];
        foreach (ControlEventRecord record in events)
        {
            WorkflowEvent item = record.Event;
            // A non-sprint aggregate (node/attempt) can never carry one of the four sprint-level
            // kinds; skipping it here also avoids running a node/attempt transition — or a routing
            // or activity record — through the sprint-only routing-envelope checks inside
            // `IsTransitionRecord`.
            if (item.Aggregate.Kind != AggregateKind.Sprint || !WorkflowFold.IsTransitionRecord(item))
            {
                continue;
            }

            if (item.Arguments.TryGetValue(WorkflowEvent.ToStateArgument, out string? toState) &&
                toState is not null && Kinds.TryGetValue(toState, out NotificationKind kind))
            {
                projections.Add(new(item.EventId, record.SprintId, kind, item.OccurredAt));
            }
        }

        return projections;
    }
}
