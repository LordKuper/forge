using Forge.Application;
using Forge.Domain;

namespace Forge.Tests.Support;

/// <summary>
/// Wraps a real <see cref="ISprintStore"/> and rewrites every <see cref="WorkflowEvent.OccurredAt"/>
/// its <see cref="GetEventsAsync"/> returns to one fixed, caller-supplied instant -- simulating
/// <see cref="IClock.UtcNow"/> producing a tie across two or more events appended moments apart (PR
/// #99 review finding 4: this is already documented as reachable in this codebase,
/// <c>SprintScheduler.cs</c>'s own remarks on <c>RecordedAt</c>). <see cref="WorkflowEvent.Sequence"/>
/// is left untouched, so it remains the only genuinely monotonic field once every timestamp collides.
/// </summary>
internal sealed class TiedTimestampSprintStore(ISprintStore inner, DateTimeOffset tiedOccurredAt) : ISprintStore
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

    public Task AppendAttemptDiffRecordedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, DiffPayload diff,
        CancellationToken cancellationToken) =>
        inner.AppendAttemptDiffRecordedAsync(projectRoot, sprintId, attemptId, diff, cancellationToken);

    public Task AppendAttemptToolUseRecordedAsync(
        string projectRoot, SprintId sprintId, AttemptId attemptId, ToolUsePayload toolUse,
        CancellationToken cancellationToken) =>
        inner.AppendAttemptToolUseRecordedAsync(projectRoot, sprintId, attemptId, toolUse, cancellationToken);

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
        return [.. events.Select(item => item with { OccurredAt = tiedOccurredAt })];
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
