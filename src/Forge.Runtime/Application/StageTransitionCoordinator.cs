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
        // Checked first, unconditionally, for both directions. Round 1 review of PR #96 (finding 1):
        // this must key on the whole saga's own completion marker
        // (ISprintStore.TryGetConvergedStageTransitionAsync), not the raw AppendTransitionAsync/
        // AppendStageRevisionRecordedAsync idempotency ledger -- that ledger entry lands at step 2 of
        // CommitRewindAsync's six steps, before evidence supersession, node reopen/invalidate, graph
        // re-advance, and the sprint-ready walk have run, so a crash in that window would otherwise
        // make every future replay report success on a permanently half-finished rewind.
        if (await store.TryGetConvergedStageTransitionAsync(projectRoot, sprintId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false) is { } replayed)
        {
            return new(true, replayed.Sprint, replayed.Nodes.GetValueOrDefault(targetStageId), DiagnosticCodes.None);
        }

        // Round 2 review of PR #96 (critical): an unconverged rewind's own durable marker
        // (SprintSnapshot.PendingRewindTargetStageId, set by CommitRewindAsync's own step 2 and
        // cleared only by its final convergence marker) is checked next, independently of whatever
        // node/sprint state its own later steps have mutated in between -- re-deriving direction from
        // that drifted state (below) can no longer even recognize an in-flight rewind as the same
        // operation once steps 3-6 begin: the target may already look like the "current" stage
        // (misclassified as a fresh Advance, which would wrongly report success without ever
        // finishing the rewind) or like the graph's only settled node (misclassified as a permanently
        // rejected Same, wedging the sprint with no recovery path). Resuming bypasses assessment,
        // staleness, and confirmation entirely -- those gates exist only for *starting* a new
        // operation, never for finishing one already committed to -- and ignores every caller-supplied
        // argument except the ones this method's own parameters can't override: the recorded
        // target/reason/key are the only ones that can still be correct. CommitRewindAsync's own steps
        // are each individually state-gated and idempotent (round 1 review of PR #96), so re-entering
        // it from the top converges correctly regardless of which step the crash landed in.
        SprintWorkflowState? currentState =
            await store.LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (currentState?.Sprint.PendingRewindTargetStageId is { } pendingRewindTarget)
        {
            return await CommitRewindAsync(
                projectRoot, sprintId, pendingRewindTarget, currentState.Sprint.PendingRewindReason!,
                currentState.Sprint.PendingRewindIdempotencyKey!.Value, cancellationToken).ConfigureAwait(false);
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

        // Round 2 review of PR #96 (non-critical contract mismatch): confirmation is required only
        // when the assessment itself says so -- currently true for a rewind, never for an advance
        // (plan section 8.3 requires no confirmation to move into normal, unstarted territory). Before
        // this fix, an advance unconditionally required confirmed=true even though
        // AssessStageTransition's own ConfirmationRequired field reports false for it, so a caller
        // that trusted the assessment's own field (rather than blindly always passing true) could
        // never advance at all.
        if (assessment.ConfirmationRequired && !confirmed)
        {
            return new(false, null, null, DiagnosticCodes.ConfirmationRequired);
        }

        if (isRewind && string.IsNullOrWhiteSpace(reason))
        {
            return new(false, null, null, DiagnosticCodes.StageTransitionReasonRequired);
        }

        // Plan section 8.4 point 1 calls the rewind reason "bounded" -- round 1 review of PR #96
        // (finding 5): reuses the exact limit and diagnostic ADR 0006 already established for the
        // equivalent human-authored bounded artifact (SprintScheduler.SupersedeAttemptAsync's own
        // supersession instruction), rather than a second, drifting rule.
        if (isRewind && reason!.Length > SprintScheduler.MaxSupersessionInstructionLength)
        {
            return new(false, null, null, DiagnosticCodes.SupersessionInstructionTooLong);
        }

        if (!assessment.Allowed)
        {
            return new(false, null, null, DiagnosticCodes.WorkflowBlocked);
        }

        return isRewind
            ? await CommitRewindAsync(projectRoot, sprintId, targetStageId, reason!, idempotencyKey, cancellationToken)
                .ConfigureAwait(false)
            : await CommitAdvanceAsync(projectRoot, sprintId, targetStageId, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<MoveStageResult> CommitAdvanceAsync(
        string projectRoot, SprintId sprintId, string targetStageId, Guid idempotencyKey,
        CancellationToken cancellationToken)
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

        // Round 1 review of PR #96 (finding 4): appended last and unconditionally on a successful
        // commit, exactly like CommitRewindAsync's own last step below -- without it, a replayed
        // advance had nothing to recognize it by and fell through to a fresh (now-stale) assessment,
        // returning `suggestion_stale` for an operation that had in fact already succeeded.
        await store.AppendStageTransitionConvergedAsync(projectRoot, sprintId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
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

        // Downstream closure computed once, up front: the frozen definition never changes for this
        // sprint, and both step 1 (round 1 review of PR #96, finding 2) and step 3/4 below need the
        // exact same set.
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> downstream = StageTransitionAssessor.CollectDownstreamClosure(targetStageId, definition.Graph);

        // Step 1: stop every active operation within the downstream closure first (plan section 8.4
        // point 2), converging each fully here rather than leaving it for a live executor tick this
        // CLI-only surface cannot guarantee will ever run. A parallel DAG can have more than one node
        // `Running` at once (round 1 review of PR #96, finding 2) -- every one of them in scope for
        // this rewind must be stopped, not only the first found.
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        foreach (string nodeId in downstream)
        {
            if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node) || node.State != NodeState.Running)
            {
                continue;
            }

            StopOperationResult stopResult = await StopAndFailRunningNodeAsync(
                projectRoot, sprintId, projectId, nodeId, cancellationToken).ConfigureAwait(false);
            if (!stopResult.Succeeded)
            {
                return new(false, null, null, stopResult.DiagnosticCode);
            }
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
        await SupersedeDownstreamEvidenceAsync(projectRoot, sprintId, downstream, currentRevision, cancellationToken)
            .ConfigureAwait(false);

        // Step 4: reopen the target for a fresh attempt and invalidate every node strictly
        // downstream of it -- gated on each node's own current state, so a retry after a crash mid
        // walk finishes only what remains rather than re-acting on an already-reset node.
        foreach (string nodeId in downstream)
        {
            await ReopenOrInvalidateNodeAsync(
                projectRoot, sprintId, projectId, nodeId, isTarget: nodeId == targetStageId, currentRevision,
                cancellationToken).ConfigureAwait(false);
        }

        // Step 5: recompute eligible stages from the frozen DAG -- the existing graph-advance
        // machinery, not a duplicated one.
        await scheduler.AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);

        // Step 6: walk the sprint back to `ready` through already-legal edges (mirrors
        // `SprintScheduler.TryAdvanceFindingsOnlyBlockedSprintAsync`'s own two/three-hop idiom) --
        // never all the way to `running` on its own, the same "resume reaches ready, a separate run
        // reaches running" contract the paused-sprint resume path already established.
        bool readyWalkConverged = await DriveSprintTowardReadyAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!readyWalkConverged)
        {
            // Post-release audit (PR #101): a genuine concurrent conflict (a version mismatch against
            // a mutation this saga did not itself make) made step 6 bail before reaching a stable end
            // state -- never mark the saga durably converged in that case. PendingRewindTargetStageId
            // stays set (only AppendStageTransitionConvergedAsync clears it), so the very next
            // MoveAsync/AssessStageTransition call resumes this exact rewind from the top and
            // finishes the ready-walk, instead of silently sealing a half-finished commit.
            //
            // PR #101 review finding 4: `Sprint`/`TargetNode` are null here, matching this record's
            // own documented contract ("on rejection they are null and no durable state changed --
            // fail closed, no partial transition") -- every other rejection in this method already
            // does the same; this is a rejection like any other, not an exception to the contract.
            // The state reload this used to need only for those two now-dropped snapshot fields is
            // gone too: loading it unconditionally before this check cost every SUCCESSFUL rewind
            // (the overwhelmingly common case) an extra full event-journal replay for a value only
            // this rejection branch ever read.
            return new(false, null, null, DiagnosticCodes.StageTransitionRewindInProgress);
        }

        // Round 1 review of PR #96 (finding 1): appended last and unconditionally, regardless of
        // which steps above this exact call did or did not need to (re-)run -- the durable "this
        // whole saga is fully done" marker MoveAsync's own outer replay check relies on, mirroring
        // StopOperationCoordinator.FinishStopAsync's own AppendAttemptStopConvergedAsync step.
        await store.AppendStageTransitionConvergedAsync(projectRoot, sprintId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Sprint, final.Nodes.GetValueOrDefault(targetStageId), DiagnosticCodes.None);
    }

    /// <summary>Stops a downstream node's live attempt and lands the node on `Failed` -- deliberately
    /// narrower than <see cref="StopOperationCoordinator.FinishStopAsync"/>: that method's own
    /// unconditional re-arm to `Ready` and sprint-pause are correct for an ad-hoc manual stop, but
    /// wrong here, since `Ready` has no legal edge back to `Pending` in the frozen node machine (round
    /// 1 review of PR #96, finding 2) -- a rewound downstream node must land on `Pending`, and
    /// `Failed -> Pending` already exists for exactly that. Reuses `workflow.node_rewind_interrupted`,
    /// the same message key <see cref="ReopenOrInvalidateNodeAsync"/>'s own `AwaitingHuman` branch
    /// already uses for the identical "this node's in-flight work was interrupted by the rewind, not
    /// a real provider failure" reason. Idempotent: every step re-checks current durable state, so
    /// calling this twice for the same node (step 1's own upfront pass, then
    /// <see cref="ReopenOrInvalidateNodeAsync"/>'s defensive re-check) is a safe no-op the second
    /// time. A no-op (reported as success) once the node is no longer `Running` at all.</summary>
    private async Task<StopOperationResult> StopAndFailRunningNodeAsync(
        string projectRoot, SprintId sprintId, Guid projectId, string nodeId, CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node) || node.State != NodeState.Running ||
            node.CurrentAttemptId is not { } attemptIdText)
        {
            return new(true, DiagnosticCodes.None);
        }

        AttemptId attemptId = new(Guid.Parse(attemptIdText));
        if (state.Attempts.TryGetValue(attemptIdText, out AttemptSnapshot? attempt) &&
            !WorkflowStateMachines.IsTerminal(attempt.State))
        {
            StopOperationResult stopResult = await stopCoordinator
                .RequestStopAsync(projectRoot, sprintId, attemptId, activeOperations, cancellationToken)
                .ConfigureAwait(false);
            if (!stopResult.Succeeded)
            {
                return stopResult;
            }

            state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            if (state.Attempts.TryGetValue(attemptIdText, out AttemptSnapshot? stopping) &&
                !WorkflowStateMachines.IsTerminal(stopping.State))
            {
                await store.AppendTransitionAsync(
                    projectRoot, sprintId, AggregateKind.Attempt, attemptIdText, "AttemptChanged",
                    "workflow.attempt_stopped", WorkflowStateNames.ToSnakeCase(AttemptState.Cancelled),
                    stopping.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            }
        }

        state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (state.Nodes.TryGetValue(nodeId, out NodeSnapshot? runningNode) && runningNode.State == NodeState.Running)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_rewind_interrupted",
                WorkflowStateNames.ToSnakeCase(NodeState.Failed), runningNode.Version, Guid.NewGuid(),
                cancellationToken).ConfigureAwait(false);
        }

        await store.AppendAttemptStopConvergedAsync(projectRoot, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, DiagnosticCodes.None);
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
        Guid projectId,
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

        if (node.State == NodeState.Running)
        {
            // Defensive: step 1 already stops every downstream `Running` node in the closure before
            // this step runs, but a resumed retry after a crash between step 1 and step 4 (round 1
            // review of PR #96, finding 2) must never leave one stranded here. `Running` has no legal
            // edge straight to `Ready`/`Pending` in the node machine, so this routes through the same
            // attempt-stop convergence step 1 uses (landing on `Failed`) rather than inventing a new
            // direct edge; the `Succeeded`/`Failed` branch just below then continues it the rest of
            // the way.
            StopOperationResult stopResult = await StopAndFailRunningNodeAsync(
                projectRoot, sprintId, projectId, nodeId, cancellationToken).ConfigureAwait(false);
            if (!stopResult.Succeeded)
            {
                return;
            }

            state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            if (!state.Nodes.TryGetValue(nodeId, out node))
            {
                return;
            }
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
            // No direct `awaiting_human -> pending` edge -- walked through the already-legal
            // `awaiting_human -> failed` hop first, matching the stop coordinator's own two-hop
            // re-arm for the identical reason (no single edge exists).
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
        // `Running` is handled above, before this switch, never falls through to here.
    }

    /// <summary>Walks the sprint toward `ready` through already-legal edges. Returns
    /// <see langword="true"/> once a stable end state is reached -- either nothing to do (the sprint
    /// was not in one of the states this walk starts from) or every hop it needed landed -- and
    /// <see langword="false"/> when a hop's own <see cref="ISprintStore.AppendTransitionAsync"/> call
    /// reports <see cref="AppendOutcome.Conflict"/> against a genuinely concurrent mutation this saga
    /// did not itself make. Post-release audit (PR #101): the caller (<see cref="CommitRewindAsync"/>)
    /// must never mark the whole saga durably converged on a <see langword="false"/> result -- this
    /// return value is the only thing that previously got silently dropped, letting a real conflict
    /// here get sealed as a completed rewind with the sprint still stuck mid-walk.</summary>
    private async Task<bool> DriveSprintTowardReadyAsync(
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
                return true;
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
                return false;
            }
        }

        // Exhausted the defensive bound without reaching a stable state -- never observed in
        // practice (the walk is at most two hops), but treated as not-converged for the same reason
        // a conflict is: never mark the saga done on an uncertain outcome.
        return false;
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
