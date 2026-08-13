using Forge.Domain;

namespace Forge.Application;

/// <summary>One sprint's full raw event stream, already validated and legacy-routing-migrated by
/// <see cref="ISprintStore.GetEventsAsync"/>.</summary>
public sealed record SprintJournalEntry(SprintId Id, IReadOnlyList<WorkflowEvent> Events)
{
    /// <summary>Every sprint's first durable record is its own creation transition (`draft`), so its
    /// <see cref="WorkflowEvent.OccurredAt"/> is a stable, durable creation timestamp — never
    /// re-derived from filesystem metadata, which a copy or restore can change.</summary>
    public DateTimeOffset CreatedAt => Events[0].OccurredAt;

    public SprintWorkflowState Fold() => WorkflowFold.Apply(Id, Events);
}

/// <summary>
/// Loads every known sprint's raw journal and ranks them in durable creation order — the same
/// order <see cref="StatusAdvisor"/> reports as `creation_sequence` and <see cref="ControlEventsReader"/>
/// uses as the cross-sprint event merge tiebreaker (ADR 0005: "occurrence time, sprint creation
/// order, event sequence, and event id"). A sprint is never assigned a stored sequence number of
/// its own (Stage 6/7 froze that write path); ranking by first-event time keeps this additive
/// instead of touching sprint creation.
/// </summary>
public static class SprintJournal
{
    public static async Task<IReadOnlyList<SprintJournalEntry>> LoadAllAsync(
        ISprintStore store,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<SprintId> ids = await store.ListAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        List<SprintJournalEntry> entries = new(ids.Count);
        foreach (SprintId id in ids)
        {
            IReadOnlyList<WorkflowEvent> events =
                await store.GetEventsAsync(projectRoot, id, cancellationToken).ConfigureAwait(false);
            if (events.Count > 0)
            {
                entries.Add(new(id, events));
            }
        }

        return [.. entries.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id.Value)];
    }
}
