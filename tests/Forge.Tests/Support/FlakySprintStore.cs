using Forge.Application;
using Forge.Domain;

namespace Forge.Tests.Support;

/// <summary>
/// Wraps a real <see cref="ISprintStore"/> and can be told to fail specific
/// <see cref="AppendTransitionAsync"/> calls (1-indexed, across every aggregate) with a chosen
/// outcome instead of delegating — simulating a crash or a conflicting write landing mid compound
/// operation, without needing a real crash or real concurrency.
/// </summary>
internal sealed class FlakySprintStore(ISprintStore inner) : ISprintStore
{
    private int appendCount;

    /// <summary>Call indexes (1-based) at which the next <see cref="AppendTransitionAsync"/> call
    /// should fail, and the outcome to fail it with.</summary>
    public Dictionary<int, AppendOutcome> FailAt { get; } = [];

    /// <summary>Total <see cref="AppendTransitionAsync"/> calls observed so far, including ones this
    /// store failed itself — lets a test compute a call index relative to "right now" instead of
    /// counting every append a setup phase happened to make.</summary>
    public int AppendCount => appendCount;

    public Task<SprintWorkflowState?> LoadAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
        inner.LoadAsync(projectRoot, id, cancellationToken);

    public Task<IReadOnlyList<SprintId>> ListAsync(string projectRoot, CancellationToken cancellationToken) =>
        inner.ListAsync(projectRoot, cancellationToken);

    public Task MarkSprintCreatedAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
        inner.MarkSprintCreatedAsync(projectRoot, id, cancellationToken);

    public async Task<AppendOutcome> AppendTransitionAsync(
        string projectRoot,
        SprintId sprintId,
        AggregateKind aggregateKind,
        string aggregateId,
        string type,
        string messageKey,
        string toState,
        long expectedAggregateVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? extraArguments = null)
    {
        int callNumber = Interlocked.Increment(ref appendCount);
        if (FailAt.TryGetValue(callNumber, out AppendOutcome? outcome))
        {
            return outcome;
        }

        return await inner.AppendTransitionAsync(
            projectRoot, sprintId, aggregateKind, aggregateId, type, messageKey, toState, expectedAggregateVersion,
            idempotencyKey, cancellationToken, extraArguments).ConfigureAwait(false);
    }

    public Task SaveDefinitionAsync(string projectRoot, SprintDefinition definition, CancellationToken cancellationToken) =>
        inner.SaveDefinitionAsync(projectRoot, definition, cancellationToken);

    public Task<SprintDefinition?> LoadDefinitionAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
        inner.LoadDefinitionAsync(projectRoot, id, cancellationToken);

    public Task SaveNodeResultAsync(string projectRoot, NodeResult result, CancellationToken cancellationToken) =>
        inner.SaveNodeResultAsync(projectRoot, result, cancellationToken);

    public Task<IReadOnlyList<NodeResult>> GetNodeResultsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken);

    public Task SaveFindingAsync(string projectRoot, Finding finding, CancellationToken cancellationToken) =>
        inner.SaveFindingAsync(projectRoot, finding, cancellationToken);

    public Task<IReadOnlyList<Finding>> GetFindingsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetFindingsAsync(projectRoot, sprintId, cancellationToken);

    public Task SaveHandoffAsync(string projectRoot, Handoff handoff, CancellationToken cancellationToken) =>
        inner.SaveHandoffAsync(projectRoot, handoff, cancellationToken);

    public Task<IReadOnlyList<Handoff>> GetHandoffsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetHandoffsAsync(projectRoot, sprintId, cancellationToken);

    public Task AppendRouteDecisionAsync(
        string projectRoot, RouteDecision decision, CancellationToken cancellationToken) =>
        inner.AppendRouteDecisionAsync(projectRoot, decision, cancellationToken);

    public Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken);
}
