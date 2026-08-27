using Forge.Application;
using Forge.Domain;

namespace Forge.Tests.Support;

/// <summary>
/// Wraps a real <see cref="ISprintStore"/> and lets a test pause exactly one future
/// <see cref="GetEventsAsync"/> call mid-flight, then release it on demand -- deterministically
/// reproducing the interleaving PR #99 round-2 review found reachable between the sprint workspace's
/// 15s timeline poll, its "load more" button, its post-mutation refresh, and sprint navigation
/// (<c>SprintTimelineViewModel.InitializeAsync</c>): all four call the same shared, long-lived
/// <c>SprintTimelineViewModel</c> instance's fetch step, so two of them can genuinely be in flight at
/// once. <see cref="ArmNextCall"/> must be called before triggering the call meant to pause; only the
/// next <see cref="GetEventsAsync"/> call after arming pauses -- every other call (including ones
/// already in flight when armed) passes straight through, so a test can let a second, "winning" call
/// run to completion while the first stays parked.
/// </summary>
internal sealed class GatedSprintStore(ISprintStore inner) : ISprintStore
{
    // `entered`/`release` deliberately stay set once armed (never nulled out) -- ArmNextCall/
    // WaitUntilNextCallEnteredAsync/ReleaseNextCall always refer to the pair from the most recent
    // ArmNextCall call. `claimed` alone decides whether a given GetEventsAsync call actually pauses:
    // the first call to observe it still unset (via the atomic exchange below) is the one and only
    // call that pauses for this arming, so a *second* call issued after arming (the "winning" call in
    // every test that uses this type) passes straight through even though the fields are still set.
    private TaskCompletionSource? entered;
    private TaskCompletionSource? release;
    private int claimed = 1;

    /// <summary>Arms exactly one future <see cref="GetEventsAsync"/> call to pause before it delegates
    /// to the wrapped store. Must be re-armed for each call a test wants to pause.</summary>
    public void ArmNextCall()
    {
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref claimed, 0);
    }

    /// <summary>Completes once the armed call has actually entered <see cref="GetEventsAsync"/> and is
    /// parked waiting for <see cref="ReleaseNextCall"/> -- so a test can deterministically order a
    /// second call after the first is known to be in flight, without a race-prone delay.</summary>
    public Task WaitUntilNextCallEnteredAsync() => (entered ?? throw new InvalidOperationException(
        $"{nameof(ArmNextCall)} was never called.")).Task;

    /// <summary>Unparks the armed call so it proceeds to actually fetch from the wrapped store.
    /// </summary>
    public void ReleaseNextCall() => (release ?? throw new InvalidOperationException(
        $"{nameof(ArmNextCall)} was never called.")).SetResult();

    public async Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref claimed, 1) == 0)
        {
            entered!.SetResult();
            await release!.Task.ConfigureAwait(false);
        }

        return await inner.GetEventsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
    }

    public Task<SprintWorkflowState?> LoadAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
        inner.LoadAsync(projectRoot, id, cancellationToken);

    public Task<IReadOnlyList<SprintId>> ListAsync(string projectRoot, CancellationToken cancellationToken) =>
        inner.ListAsync(projectRoot, cancellationToken);

    public Task MarkSprintCreatedAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
        inner.MarkSprintCreatedAsync(projectRoot, id, cancellationToken);

    public Task<AppendOutcome> AppendTransitionAsync(
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
        IReadOnlyDictionary<string, string?>? extraArguments = null) =>
        inner.AppendTransitionAsync(
            projectRoot, sprintId, aggregateKind, aggregateId, type, messageKey, toState, expectedAggregateVersion,
            idempotencyKey, cancellationToken, extraArguments);

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

    public Task AppendUserMessageAsync(
        string projectRoot, SprintId sprintId, Guid messageId, string text, CancellationToken cancellationToken) =>
        inner.AppendUserMessageAsync(projectRoot, sprintId, messageId, text, cancellationToken);

    public Task AppendAttemptDiffRecordedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, DiffPayload diff,
        CancellationToken cancellationToken) =>
        inner.AppendAttemptDiffRecordedAsync(projectRoot, sprintId, attemptId, diff, cancellationToken);

    public Task AppendAgentSummaryRecordedAsync(
        string projectRoot, SprintId sprintId, string nodeId, Guid handoffId, string summaryText,
        CancellationToken cancellationToken) =>
        inner.AppendAgentSummaryRecordedAsync(projectRoot, sprintId, nodeId, handoffId, summaryText, cancellationToken);

    public Task<AppendOutcome> AppendAttemptStopRequestedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, long expectedAttemptVersion,
        CancellationToken cancellationToken) =>
        inner.AppendAttemptStopRequestedAsync(projectRoot, sprintId, attemptId, expectedAttemptVersion, cancellationToken);

    public Task AppendAttemptStopConvergedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, CancellationToken cancellationToken) =>
        inner.AppendAttemptStopConvergedAsync(projectRoot, sprintId, attemptId, cancellationToken);

    public Task<SprintWorkflowState?> TryGetConvergedStageTransitionAsync(
        string projectRoot, SprintId sprintId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        inner.TryGetConvergedStageTransitionAsync(projectRoot, sprintId, idempotencyKey, cancellationToken);

    public Task AppendStageTransitionConvergedAsync(
        string projectRoot, SprintId sprintId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        inner.AppendStageTransitionConvergedAsync(projectRoot, sprintId, idempotencyKey, cancellationToken);

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
