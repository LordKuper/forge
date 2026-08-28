using Forge.Domain;

namespace Forge.Application;

/// <summary>One sprint's full raw event stream, already validated and legacy-routing-migrated by
/// <see cref="ISprintStore.GetEventsAsync"/>.</summary>
public sealed record SprintJournalEntry(SprintId Id, IReadOnlyList<WorkflowEvent> Events)
{
    private static readonly string AttemptCreatedState =
        WorkflowStateNames.ToSnakeCase(WorkflowStateMachines.AttemptInitial);

    /// <summary>Every sprint's first durable record is its own creation transition (`draft`), so its
    /// <see cref="WorkflowEvent.OccurredAt"/> is a stable, durable creation timestamp — never
    /// re-derived from filesystem metadata, which a copy or restore can change.</summary>
    public DateTimeOffset CreatedAt => Events[0].OccurredAt;

    /// <summary>
    /// ADR 0069: the wall-clock anchor "how long has this sprint been working" is measured from —
    /// the moment this sprint's first attempt was started, or <see langword="null"/> for a sprint
    /// that has never started one (draft, or ready with nothing picked up yet). Derived from the
    /// journal rather than persisted as a new field, for exactly <see cref="CreatedAt"/>'s own
    /// reason: the anchor is already a durable event on a stream this type holds in memory.
    /// </summary>
    /// <remarks>
    /// Keyed on the attempt aggregate's own `created` transition — the one
    /// <c>SprintScheduler.StartAttemptAsync</c> appends — and deliberately NOT on its later
    /// `running` transition, which would look like the more literal reading of the decision this
    /// implements. No caller drives an attempt through `preparing`/`running` while it works:
    /// <c>CompleteAttemptAsync</c> walks the whole remaining path in one call at the end, so a
    /// `running` event's <see cref="WorkflowEvent.OccurredAt"/> is written retroactively and sits at
    /// the first attempt's *completion*, not its start. The `created` transition is the only durable
    /// record of when work actually began. Should an executor ever record `running` live, this is
    /// the one place that choice has to be revisited.
    /// </remarks>
    public DateTimeOffset? FirstAttemptStartedAt
    {
        get
        {
            foreach (WorkflowEvent item in Events)
            {
                if (item.Aggregate.Kind == AggregateKind.Attempt &&
                    item.Arguments.TryGetValue(WorkflowEvent.ToStateArgument, out string? state) &&
                    string.Equals(state, AttemptCreatedState, StringComparison.Ordinal))
                {
                    return item.OccurredAt;
                }
            }

            return null;
        }
    }

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
