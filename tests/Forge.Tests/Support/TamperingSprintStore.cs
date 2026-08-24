using Forge.Application;
using Forge.Domain;

namespace Forge.Tests.Support;

/// <summary>
/// Wraps a real <see cref="ISprintStore"/> and rewrites every <see cref="WorkflowEvent.MessageKey"/>
/// its <see cref="GetEventsAsync"/> returns to carry a caller-supplied raw string verbatim --
/// simulating a corrupted, legacy, or otherwise unexpected journal entry whose message key is not
/// drawn from today's fixed, constrained-alphabet catalog. <see cref="SprintTimelineProjector"/>'s
/// own pass 1 (redaction pass 1 of 2) copies <see cref="WorkflowEvent.MessageKey"/> straight into
/// <see cref="SprintTimelineItem.MessageKey"/> with no redaction at all -- only
/// <see cref="WorkflowEvent.Arguments"/> is ever redacted by pass 1 -- so this is how a test proves
/// pass 2 (<see cref="SprintTimelineRedaction"/>), not pass 1, is what actually catches a secret in a
/// field pass 1 structurally never touches.
/// </summary>
internal sealed class TamperingSprintStore(ISprintStore inner, string rawMessageKey) : ISprintStore
{
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

    public async Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkflowEvent> events =
            await inner.GetEventsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        return [.. events.Select(item => item with { MessageKey = rawMessageKey })];
    }

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
