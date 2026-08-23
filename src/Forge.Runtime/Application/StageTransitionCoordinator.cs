using System.Globalization;
using Forge.Configuration;
using Forge.Domain;

namespace Forge.Application;

/// <summary>Plan section 8.5's `MoveSprintToStage` result. <paramref name="Sprint"/>/
/// <paramref name="TargetNode"/> are the post-commit snapshots on success; on rejection they are
/// <see langword="null"/> and no durable state changed (fail closed, no partial transition).
/// </summary>
public sealed record MoveStageResult(bool Succeeded, SprintSnapshot? Sprint, NodeSnapshot? TargetNode, string DiagnosticCode);

/// <summary>
/// Plan section 8.5's idempotent, resumable `MoveSprintToStage` saga. Always recomputes a fresh
/// <see cref="StageTransitionAssessment"/> immediately before acting and rejects a caller-supplied
/// <c>assessmentToken</c>/expected version mismatch without any side effect -- a stale
/// client-held assessment is never trusted (plan section 8.5, ADR 0046).
///
/// Advance (plan section 8.3) reuses <see cref="SprintScheduler.SkipNodeAsync"/> for every optional,
/// unmet intervening node a skip-ahead target requires, then <see cref="SprintScheduler.AdvanceGraphAsync"/>
/// to let the existing graph-advance machinery do the rest -- it never fabricates a result or marks
/// a mandatory stage skipped (an unmet *mandatory* predecessor already fails the assessment's own
/// <see cref="StageTransitionAssessment.Allowed"/> gate before this coordinator is ever reached).
///
/// Rewind (plan section 8.4) is a multi-step saga mirroring <see cref="SprintScheduler.SupersedeAttemptAsync"/>'s
/// own discipline: every step re-checks current durable state before acting, so a Host crash between
/// any two steps converges to the same end state on retry rather than duplicating or skipping one.
/// Step order: (1) stop the active operation first when one exists, converging it fully rather than
/// waiting for a live executor tick that may never come; (2) durably record exactly one stage
/// revision increment, deduplicated through the same idempotency-key ledger every other mutation
/// here already uses; (3) reopen the target and invalidate every node strictly downstream of it;
/// (4) recompute eligible stages from the frozen DAG; (5) walk the sprint back to `ready` through
/// already-legal state-machine edges. Node identity never changes -- only execution state gains a
/// revision (ADR 0045).
/// </summary>
public sealed class StageTransitionCoordinator(
    ISprintStore store,
    SprintScheduler scheduler,
    StageTransitionAssessor assessor,
    StopOperationCoordinator stopCoordinator,
    ActiveOperationRegistry activeOperations,
    IConfigurationRegistry registry,
    IClock clock)
{
    private const string BlockedByRewind = "rewind";

    public async Task<MoveStageResult> MoveAsync(
        string projectRoot,
        SprintId sprintId,
        string targetStageId,
        long expectedStateVersion,
        string? assessmentToken,
        string? reason,
        bool confirmed,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStageId);

        // Idempotent replay is recognized *before* any fresh assessment (plan section 8.5: "never
        // creates a second revision"): a rewind's target is, by definition, a node already at a
        // terminal outcome before the rewind commits and no longer at one afterward, so re-deriving
        // direction from current state on a replay would no longer even see the same operation.
        // Checked first, unconditionally, for both directions -- an advance replay converges to a
        // safe no-op through its own state-gated steps regardless, so this adds no risk there either.
        if (await store.TryGetIdempotentReplayAsync(projectRoot, sprintId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false) is { } replayed)
        {
            return new(true, replayed.Sprint, replayed.Nodes.GetValueOrDefault(targetStageId), DiagnosticCodes.None);
        }

        StageTransitionAssessment assessment = await assessor
            .AssessAsync(projectRoot, sprintId, targetStageId, cancellationToken).ConfigureAwait(false);
        if (!assessment.Found)
        {
            return new(false, null, null, assessment.DiagnosticCode);
        }

        if (assessment.Direction == StageTransitionDirection.Same ||
            assessment.DiagnosticCode == DiagnosticCodes.SprintTransitionInvalid)
        {
            // Same direction (nothing to commit) and a terminal sprint (plan section 8.4: "Terminal
            // sprints cannot be moved") both reduce to the same rejection: no legal move exists.
            return new(false, null, null, DiagnosticCodes.SprintTransitionInvalid);
        }

        // The Host recomputes the assessment immediately before mutation and rejects any mismatch
        // (plan section 8.5) -- a caller-supplied version or token that no longer matches this fresh
        // read is stale, full stop, before anything else (including whether it was confirmed) is
        // even considered.
        if (expectedStateVersion != assessment.ExpectedStateVersion || assessmentToken != assessment.AssessmentToken)
        {
            return new(false, null, null, DiagnosticCodes.SuggestionStale);
        }

        bool isRewind = assessment.Direction == StageTransitionDirection.Rewind;
        if (!confirmed)
        {
            return new(false, null, null, DiagnosticCodes.ConfirmationRequired);
        }

        if (isRewind && string.IsNullOrWhiteSpace(reason))
        {
            return new(false, null, null, DiagnosticCodes.StageTransitionReasonRequired);
        }

        if (!assessment.Allowed)
        {
            return new(false, null, null, DiagnosticCodes.WorkflowBlocked);
        }

        return isRewind
            ? await CommitRewindAsync(projectRoot, sprintId, targetStageId, reason!, idempotencyKey, cancellationToken)
                .ConfigureAwait(false)
            : await CommitAdvanceAsync(projectRoot, sprintId, targetStageId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MoveStageResult> CommitAdvanceAsync(
        string projectRoot, SprintId sprintId, string targetStageId, CancellationToken cancellationToken)
    {
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, NodeDefinition> byId = definition.Graph.ToDictionary(
            node => node.Id, node => node, StringComparer.Ordinal);
        HashSet<string> allPredecessors = StageTransitionAssessor.CollectTransitivePredecessors(
            targetStageId, byId, includeOptional: true);

        foreach (string predecessorId in allPredecessors)
        {
            SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false);
            if (!state.Nodes.TryGetValue(predecessorId, out NodeSnapshot? node) ||
                node.State is not (NodeState.Pending or NodeState.Ready))
            {
                continue;
            }

            if (!byId[predecessorId].Optional)
            {
                // The assessment already required every mandatory predecessor to be satisfied
                // before reaching here -- a mandatory node still unmet at commit time is a genuine
                // race since the assessment was read, not something this coordinator may paper over
                // by skipping it. Never marks a mandatory stage as skipped (plan section 8.3).
                return new(false, null, null, DiagnosticCodes.WorkflowBlocked);
            }

            await scheduler.SkipNodeAsync(projectRoot, sprintId, predecessorId, node.Version, cancellationToken)
                .ConfigureAwait(false);
        }

        SprintWorkflowState advanced = await scheduler.AdvanceGraphAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!advanced.Nodes.TryGetValue(targetStageId, out NodeSnapshot? targetNode) ||
            targetNode.State == NodeState.Pending)
        {
            // Every intervening node the loop above could legally act on was skipped or already
            // satisfied, yet the target still did not become eligible -- never fabricates a result;
            // report the block rather than silently reporting success for a stage that is not
            // actually reachable yet.
            return new(false, null, null, DiagnosticCodes.WorkflowBlocked);
        }

        return new(true, advanced.Sprint, targetNode, DiagnosticCodes.None);
    }

    private async Task<MoveStageResult> CommitRewindAsync(
        string projectRoot,
        SprintId sprintId,
        string targetStageId,
        string reason,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        Guid projectId = await ProjectIdentity.ReadProjectIdAsync(projectRoot, registry, cancellationToken)
            .ConfigureAwait(false);

        // Step 1: stop the active operation first (plan section 8.4 point 2), converging it fully
        // here rather than leaving it for a live executor tick that this CLI-only surface cannot
        // guarantee will ever run.
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        NodeSnapshot? activeNode = state.Nodes.Values.FirstOrDefault(node =>
            node.State == NodeState.Running && node.CurrentAttemptId is { } id &&
            state.Attempts.TryGetValue(id, out AttemptSnapshot? attempt) && !WorkflowStateMachines.IsTerminal(attempt.State));
        if (activeNode is not null)
        {
            AttemptId attemptId = new(Guid.Parse(activeNode.CurrentAttemptId!));
            StopOperationResult stopResult = await stopCoordinator
                .RequestStopAsync(projectRoot, sprintId, attemptId, activeOperations, cancellationToken)
                .ConfigureAwait(false);
            if (!stopResult.Succeeded)
            {
                return new(false, null, null, stopResult.DiagnosticCode);
            }

            await stopCoordinator
                .FinishStopAsync(projectRoot, sprintId, projectId, activeNode.Id.Value, attemptId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Step 2: exactly one durable revision increment per idempotency key -- a replay of the same
        // key returns the already-folded state without appending again (plan section 8.5: "never
        // creates a second revision"), reusing AppendTransitionAsync's own idempotency ledger rather
        // than a second mechanism.
        state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        StageRevision newRevision = state.Sprint.Revision.Next();
        AppendOutcome revisionOutcome = await store.AppendStageRevisionRecordedAsync(
            projectRoot, sprintId, targetStageId, reason, newRevision, state.Sprint.Version, idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (!revisionOutcome.Succeeded)
        {
            return new(false, null, null, revisionOutcome.DiagnosticCode);
        }

        StageRevision currentRevision = revisionOutcome.State!.Sprint.Revision;

        // Step 3: mark every downstream result/handoff/decision/finding as superseded -- gated on
        // each record's own `Superseded is null` check inside the Mark*SupersededAsync methods
        // themselves, so a retry after a crash mid walk only marks what is not already marked.
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> downstream = StageTransitionAssessor.CollectDownstreamClosure(targetStageId, definition.Graph);
        await SupersedeDownstreamEvidenceAsync(projectRoot, sprintId, downstream, currentRevision, cancellationToken)
            .ConfigureAwait(false);

        // Step 4: reopen the target for a fresh attempt and invalidate every node strictly
        // downstream of it -- gated on each node's own current state, so a retry after a crash mid
        // walk finishes only what remains rather than re-acting on an already-reset node.
        foreach (string nodeId in downstream)
        {
            await ReopenOrInvalidateNodeAsync(
                projectRoot, sprintId, nodeId, isTarget: nodeId == targetStageId, currentRevision, cancellationToken)
                .ConfigureAwait(false);
        }

        // Step 5: recompute eligible stages from the frozen DAG -- the existing graph-advance
        // machinery, not a duplicated one.
        await scheduler.AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);

        // Step 6: walk the sprint back to `ready` through already-legal edges (mirrors
        // `SprintScheduler.TryAdvanceFindingsOnlyBlockedSprintAsync`'s own two/three-hop idiom) --
        // never all the way to `running` on its own, the same "resume reaches ready, a separate run
        // reaches running" contract the paused-sprint resume path already established.
        await DriveSprintTowardReadyAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);

        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Sprint, final.Nodes.GetValueOrDefault(targetStageId), DiagnosticCodes.None);
    }

    /// <summary>Marks every non-superseded result/handoff/decision belonging to a node in
    /// <paramref name="downstream"/>, and every non-superseded finding either belonging to one of
    /// those nodes or carrying no node attribution at all (plan section 8.4 point 5; see
    /// <c>Forge.Domain.Finding.NodeId</c>'s own remarks on why an unattributed finding is treated
    /// conservatively as affected). Each individual mark is independently idempotent
    /// (<c>ISprintStore.Mark*SupersededAsync</c> no-ops once already marked), so calling this twice
    /// for the same revision (a resumed retry) is safe.</summary>
    private async Task SupersedeDownstreamEvidenceAsync(
        string projectRoot,
        SprintId sprintId,
        HashSet<string> downstream,
        StageRevision revision,
        CancellationToken cancellationToken)
    {
        SupersededBy marker = new(revision, clock.UtcNow);

        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        foreach (NodeResult result in results)
        {
            if (downstream.Contains(result.NodeId.Value) && result.Superseded is null)
            {
                await store.MarkNodeResultSupersededAsync(projectRoot, sprintId, result.AttemptId, marker, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        IReadOnlyList<Handoff> handoffs =
            await store.GetHandoffsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        foreach (Handoff handoff in handoffs)
        {
            if (downstream.Contains(handoff.NodeId.Value) && handoff.Superseded is null)
            {
                await store.MarkHandoffSupersededAsync(projectRoot, sprintId, handoff.HandoffId, marker, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        IReadOnlyList<ConfirmationArtifact> confirmations =
            await store.GetConfirmationsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        foreach (ConfirmationArtifact confirmation in confirmations)
        {
            if (downstream.Contains(confirmation.NodeId.Value) && confirmation.Superseded is null)
            {
                await store.MarkConfirmationSupersededAsync(
                    projectRoot, sprintId, confirmation.ConfirmationId, marker, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<TestWorkArtifact> testWork =
            await store.GetTestWorkAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        foreach (TestWorkArtifact artifact in testWork)
        {
            if (downstream.Contains(artifact.NodeId.Value) && artifact.Superseded is null)
            {
                await store.MarkTestWorkSupersededAsync(projectRoot, sprintId, artifact.TestWorkId, marker, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        IReadOnlyList<Finding> findings =
            await store.GetFindingsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        foreach (Finding finding in findings)
        {
            bool affected = finding.NodeId is null || downstream.Contains(finding.NodeId.Value);
            if (affected && finding.Superseded is null)
            {
                await store.MarkFindingSupersededAsync(projectRoot, sprintId, finding.FindingId, marker, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>One downstream node's own idempotent reset. <paramref name="isTarget"/> reopens
    /// straight to `ready` (its own upstream is untouched and already satisfied); every other
    /// downstream node is invalidated to `pending` so its real eligibility is recomputed once the
    /// target actually re-succeeds, never assumed still satisfied from now-superseded evidence.
    /// `AttemptCount` resets to 0 on both edges: a rewound stage's retry budget starts fresh.
    /// </summary>
    private async Task ReopenOrInvalidateNodeAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        bool isTarget,
        StageRevision currentRevision,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return;
        }

        NodeState toState = isTarget ? NodeState.Ready : NodeState.Pending;
        string messageKey = isTarget ? "workflow.node_reopened" : "workflow.node_invalidated";
        Dictionary<string, string?> extra = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.RevisionArgument] = currentRevision.Value.ToString(CultureInfo.InvariantCulture),
            [WorkflowEvent.AttemptNumberArgument] = "0",
        };

        if (node.State is NodeState.Succeeded or NodeState.Failed)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", messageKey,
                WorkflowStateNames.ToSnakeCase(toState), node.Version, Guid.NewGuid(), cancellationToken, extra)
                .ConfigureAwait(false);
            return;
        }

        if (node.State == NodeState.AwaitingHuman)
        {
            // No direct `awaiting_human -> pending`/`awaiting_human -> ready` edge -- walked through
            // the already-legal `awaiting_human -> failed` hop first, matching the stop
            // coordinator's own two-hop re-arm for the identical reason (no single edge exists).
            AppendOutcome toFailed = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_rewind_interrupted",
                WorkflowStateNames.ToSnakeCase(NodeState.Failed), node.Version, Guid.NewGuid(), cancellationToken)
                .ConfigureAwait(false);
            if (!toFailed.Succeeded)
            {
                return;
            }

            NodeSnapshot failedNode = toFailed.State!.Nodes[nodeId];
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", messageKey,
                WorkflowStateNames.ToSnakeCase(toState), failedNode.Version, Guid.NewGuid(), cancellationToken, extra)
                .ConfigureAwait(false);
        }

        // Pending/Ready/Skipped/Cancelled: nothing to reopen or invalidate -- a node that never ran
        // has no evidence to supersede, and an explicitly skipped/cancelled node is left as the
        // operator's own deliberate prior decision (out of scope for an automatic rewind reset).
    }

    private async Task DriveSprintTowardReadyAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false);
            (string messageKey, SprintState? next) = state.Sprint.State switch
            {
                SprintState.AwaitingHuman or SprintState.ReadyToFinalize =>
                    ("workflow.sprint_blocked", (SprintState?)SprintState.Blocked),
                SprintState.Blocked or SprintState.Failed or SprintState.Paused =>
                    ("workflow.sprint_ready", (SprintState?)SprintState.Ready),
                _ => (string.Empty, (SprintState?)null),
            };
            if (next is not { } target)
            {
                return;
            }

            Dictionary<string, string?>? extra = target == SprintState.Blocked
                ? new(StringComparer.Ordinal) { [WorkflowEvent.BlockedReasonArgument] = BlockedByRewind }
                : null;
            AppendOutcome outcome = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                messageKey, WorkflowStateNames.ToSnakeCase(target), state.Sprint.Version, Guid.NewGuid(),
                cancellationToken, extra).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                return;
            }
        }
    }

    private async Task<SprintWorkflowState> RequireStateAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        await store.LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sprint '{sprintId.Value}' vanished during a stage transition.");

    private async Task<SprintDefinition> RequireDefinitionAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        await store.LoadDefinitionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sprint '{sprintId.Value}' has no frozen definition.");
}
