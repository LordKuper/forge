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

    public Task SaveConfirmationAsync(
        string projectRoot, ConfirmationArtifact confirmation, CancellationToken cancellationToken) =>
        inner.SaveConfirmationAsync(projectRoot, confirmation, cancellationToken);

    public Task<IReadOnlyList<ConfirmationArtifact>> GetConfirmationsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetConfirmationsAsync(projectRoot, sprintId, cancellationToken);

    public Task SaveTestWorkAsync(
        string projectRoot, TestWorkArtifact testWork, CancellationToken cancellationToken) =>
        inner.SaveTestWorkAsync(projectRoot, testWork, cancellationToken);

    public Task<IReadOnlyList<TestWorkArtifact>> GetTestWorkAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetTestWorkAsync(projectRoot, sprintId, cancellationToken);

    public Task SaveReviewIterationAsync(
        string projectRoot, ReviewIterationRecord record, CancellationToken cancellationToken) =>
        inner.SaveReviewIterationAsync(projectRoot, record, cancellationToken);

    public Task<IReadOnlyList<ReviewIterationRecord>> GetReviewIterationsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetReviewIterationsAsync(projectRoot, sprintId, cancellationToken);

    public Task SetReviewFloorPinnedAsync(
        string projectRoot, SprintId sprintId, string nodeId, ReviewDimension dimension,
        CancellationToken cancellationToken) =>
        inner.SetReviewFloorPinnedAsync(projectRoot, sprintId, nodeId, dimension, cancellationToken);

    public Task<bool> IsReviewFloorPinnedAsync(
        string projectRoot, SprintId sprintId, string nodeId, ReviewDimension dimension,
        CancellationToken cancellationToken) =>
        inner.IsReviewFloorPinnedAsync(projectRoot, sprintId, nodeId, dimension, cancellationToken);

    public Task AppendRouteDecisionAsync(
        string projectRoot, RouteDecision decision, CancellationToken cancellationToken) =>
        inner.AppendRouteDecisionAsync(projectRoot, decision, cancellationToken);

    public Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken);

    public Task AppendAttemptActivityAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, CancellationToken cancellationToken,
        AttemptActivityKind kind = AttemptActivityKind.Heartbeat) =>
        inner.AppendAttemptActivityAsync(projectRoot, sprintId, attemptId, cancellationToken, kind);

    public Task AppendAttemptSupersededAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, string instruction,
        CancellationToken cancellationToken) =>
        inner.AppendAttemptSupersededAsync(projectRoot, sprintId, attemptId, instruction, cancellationToken);

    /// <summary>Optional side effect run immediately before delegating an
    /// <see cref="AppendAttemptStopRequestedAsync"/> call to the wrapped store -- lets a test inject
    /// a concurrent mutation (e.g. a real <c>CompleteAttemptAsync</c>/<c>SupersedeAttemptAsync</c>
    /// call) into the exact unlocked window
    /// <see cref="Forge.Application.StopOperationCoordinator.RequestStopAsync"/> holds between its
    /// own validation and this append, deterministically, without a real race.</summary>
    public Func<CancellationToken, Task>? BeforeAppendAttemptStopRequested { get; set; }

    public async Task<AppendOutcome> AppendAttemptStopRequestedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, long expectedAttemptVersion,
        CancellationToken cancellationToken)
    {
        if (BeforeAppendAttemptStopRequested is { } hook)
        {
            await hook(cancellationToken).ConfigureAwait(false);
        }

        return await inner.AppendAttemptStopRequestedAsync(
            projectRoot, sprintId, attemptId, expectedAttemptVersion, cancellationToken).ConfigureAwait(false);
    }

    public Task AppendAttemptStopConvergedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, CancellationToken cancellationToken) =>
        inner.AppendAttemptStopConvergedAsync(projectRoot, sprintId, attemptId, cancellationToken);

    public Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        inner.GetEventsAsync(projectRoot, sprintId, cancellationToken);

    public Task<SprintWorkflowState?> TryGetIdempotentReplayAsync(
        string projectRoot, SprintId sprintId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        inner.TryGetIdempotentReplayAsync(projectRoot, sprintId, idempotencyKey, cancellationToken);

    public Task<AppendOutcome> AppendStageRevisionRecordedAsync(
        string projectRoot, SprintId sprintId, string targetStageId, string reason, StageRevision newRevision,
        long expectedSprintVersion, Guid idempotencyKey, CancellationToken cancellationToken) =>
        inner.AppendStageRevisionRecordedAsync(
            projectRoot, sprintId, targetStageId, reason, newRevision, expectedSprintVersion, idempotencyKey,
            cancellationToken);

    public Task MarkNodeResultSupersededAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, SupersededBy marker,
        CancellationToken cancellationToken) =>
        inner.MarkNodeResultSupersededAsync(projectRoot, sprintId, attemptId, marker, cancellationToken);

    public Task MarkHandoffSupersededAsync(
        string projectRoot, SprintId sprintId, Guid handoffId, SupersededBy marker,
        CancellationToken cancellationToken) =>
        inner.MarkHandoffSupersededAsync(projectRoot, sprintId, handoffId, marker, cancellationToken);

    public Task MarkConfirmationSupersededAsync(
        string projectRoot, SprintId sprintId, Guid confirmationId, SupersededBy marker,
        CancellationToken cancellationToken) =>
        inner.MarkConfirmationSupersededAsync(projectRoot, sprintId, confirmationId, marker, cancellationToken);

    public Task MarkTestWorkSupersededAsync(
        string projectRoot, SprintId sprintId, Guid testWorkId, SupersededBy marker,
        CancellationToken cancellationToken) =>
        inner.MarkTestWorkSupersededAsync(projectRoot, sprintId, testWorkId, marker, cancellationToken);

    public Task MarkFindingSupersededAsync(
        string projectRoot, SprintId sprintId, Guid findingId, SupersededBy marker,
        CancellationToken cancellationToken) =>
        inner.MarkFindingSupersededAsync(projectRoot, sprintId, findingId, marker, cancellationToken);
}
