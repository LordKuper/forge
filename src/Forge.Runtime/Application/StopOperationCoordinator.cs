using Forge.Domain;

namespace Forge.Application;

public sealed record StopOperationResult(bool Succeeded, string DiagnosticCode);

/// <summary>
/// Plan section 7's idempotent, resumable stop-current-operation saga. <see cref="RequestStopAsync"/>
/// is the mutation half: validates the target attempt is the sprint's exact active operation,
/// durably records the stop intent before touching anything in-memory, then best-effort cancels the
/// live process through <see cref="ActiveOperationRegistry"/> (a no-op if this Host process never
/// registered it, e.g. after a crash and restart). <see cref="FinishStopAsync"/> is the convergence
/// half every node-role executor calls once it observes a `running` node whose current attempt
/// already carries a durable stop intent, whether because this same process's own cancellation
/// already unblocked the provider run and the next tick found the intent, or because a Host crash
/// left the intent recorded with nothing yet converged on resume: it durably settles the attempt as
/// `cancelled`, discards its worktree, re-arms the owning node without touching
/// <see cref="NodeSnapshot.AttemptCount"/>/<see cref="SprintScheduler.MaxAutomaticRetries"/>, and
/// pauses the sprint. Every step re-checks current durable state before acting, so a Host crash
/// between any two steps converges to the same end state on retry instead of duplicating or
/// skipping one -- the same discipline <see cref="SprintScheduler.SupersedeAttemptAsync"/> already
/// applies to its own multi-step compound operation.
/// </summary>
public sealed class StopOperationCoordinator(ISprintStore store, SprintGitIsolation gitIsolation)
{
    public async Task<StopOperationResult> RequestStopAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        ActiveOperationRegistry activeOperations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeOperations);
        SprintWorkflowState? state =
            await store.LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(false, DiagnosticCodes.SprintNotFound);
        }

        string attemptKey = attemptId.Value.ToString("D");
        if (!state.Attempts.TryGetValue(attemptKey, out AttemptSnapshot? attempt))
        {
            return new(false, DiagnosticCodes.WorkflowEventConflict);
        }

        if (attempt.StopRequestedAt is not null)
        {
            // Idempotent replay: the intent is already durable (this call, an earlier one, or the
            // saga has already finished). Re-validating a snapshot that may have legitimately moved
            // on since (the attempt settled, the node re-armed, the sprint paused) would reject a
            // repeat of the exact same request that plan section 12.4 requires stay idempotent.
            activeOperations.TryCancel(attemptId);
            return new(true, DiagnosticCodes.None);
        }

        if (state.Sprint.State != SprintState.Running)
        {
            return new(false, DiagnosticCodes.NoActiveOperation);
        }

        if (WorkflowStateMachines.IsTerminal(attempt.State))
        {
            return new(false, DiagnosticCodes.AttemptTerminal);
        }

        if (attempt.NodeId is not { } nodeId ||
            !state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node) ||
            node.State != NodeState.Running ||
            node.CurrentAttemptId != attemptKey)
        {
            // Either this attempt never became the node's current one, or the node has since moved
            // on to a different generation (a stale target -- plan section 12.4's "a stale stop
            // cannot cancel an attempt that started after the targeted one").
            return new(false, DiagnosticCodes.ActiveOperationChanged);
        }

        await store.AppendAttemptStopRequestedAsync(projectRoot, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        activeOperations.TryCancel(attemptId);
        return new(true, DiagnosticCodes.None);
    }

    /// <summary>Converges a stopped attempt to its final durable state. Safe to call repeatedly and
    /// safe to call for an attempt whose earlier steps already landed -- each step below is gated on
    /// the current durable state, not on whether this exact call already ran one before.</summary>
    public async Task FinishStopAsync(
        string projectRoot,
        SprintId sprintId,
        Guid projectId,
        string nodeId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        string attemptKey = attemptId.Value.ToString("D");

        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Attempts.TryGetValue(attemptKey, out AttemptSnapshot? attempt) &&
            !WorkflowStateMachines.IsTerminal(attempt.State))
        {
            // Every non-terminal attempt state (created, preparing, running, validating) has a
            // direct edge to cancelled in the frozen machine. Created is the common case in
            // practice: nothing in this codebase walks an attempt further than that on its own --
            // only a real completion (SprintScheduler.CompleteAttemptAsync/ResolveHumanGateAsync)
            // ever does, so the node being Running is what actually means "this attempt is the
            // sprint's live active operation," not the attempt's own state. ADR 0044's
            // Validating -> Cancelled edge is the one case attempt.supersede deliberately still
            // refuses; this coordinator is its sanctioned caller.
            AppendOutcome cancelOutcome = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
                "workflow.attempt_stopped", WorkflowStateNames.ToSnakeCase(AttemptState.Cancelled), attempt.Version,
                Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            if (!cancelOutcome.Succeeded)
            {
                // A concurrent writer changed this attempt between the read above and this append --
                // never assumed to mean the cancellation actually landed. A future call (the next
                // tick, or restart recovery) re-derives every step from fresh state instead of
                // guessing what happened, the same "stop, don't guess" discipline every other step
                // below already follows.
                return;
            }
        }

        // Discarding is idempotent and safe to repeat regardless of whether the attempt transition
        // above actually ran this call or a prior one -- matching every node executor's own
        // best-effort discard on a failure path. A failed discard is left for a future
        // reconciliation pass (SprintGitIsolation.ReconcileAsync), never retried inline here.
        _ = await gitIsolation
            .DiscardAttemptAsync(projectRoot, projectId, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);

        state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        NodeSnapshot node = state.Nodes[nodeId];
        if (node.State == NodeState.Running)
        {
            // Dedicated message keys, never workflow.node_failed/workflow.node_retrying: this must
            // never be represented as the provider failing (plan section 7.2), which would let
            // SprintScheduler.CompleteAttemptAsync's own MaxAutomaticRetries accounting see it. This
            // walk never calls CompleteAttemptAsync at all.
            AppendOutcome stoppedOutcome = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_stopped",
                WorkflowStateNames.ToSnakeCase(NodeState.Failed), node.Version, Guid.NewGuid(), cancellationToken)
                .ConfigureAwait(false);
            if (!stoppedOutcome.Succeeded)
            {
                return;
            }

            node = stoppedOutcome.State!.Nodes[nodeId];
        }

        if (node.State == NodeState.Failed)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_rearmed",
                WorkflowStateNames.ToSnakeCase(NodeState.Ready), node.Version, Guid.NewGuid(), cancellationToken)
                .ConfigureAwait(false);
        }

        state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (state.Sprint.State == SprintState.Running)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_paused", WorkflowStateNames.ToSnakeCase(SprintState.Paused), state.Sprint.Version,
                Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SprintWorkflowState> RequireStateAsync(
        string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
        await store.LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Sprint '{sprintId.Value}' vanished while its stop was converging.");
}
