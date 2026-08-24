using Forge.Domain;
using Forge.Localization;

namespace Forge.Application;

/// <summary>Plan section 6.4's typed pointer at whatever an <see cref="AvailableAction"/> acts on.
/// Every field is optional because different actions name different parts of the hierarchy -- a
/// project-level suggestion names only <see cref="ProjectRoot"/>; a stage move names
/// <see cref="SprintId"/> and <see cref="StageId"/> together.</summary>
public sealed record AvailableActionTarget(
    string? ProjectRoot,
    Guid? SprintId,
    string? NodeId,
    Guid? AttemptId,
    string? StageId);

/// <summary>One typed input field an action's confirmation may require (plan section 6.4). Only a
/// rewind's mandatory reason uses this today.</summary>
public sealed record AvailableActionInputField(string Name, string Type, bool Required, int? MaxLength);

/// <summary>
/// Plan section 6.4's versioned contextual-action projection. Wraps two already-existing sources
/// rather than duplicating workflow policy: a project-level row is <see cref="SuggestedAction"/>
/// (<see cref="StatusAdvisor.Recommend"/>) re-shaped into this richer contract, and a sprint-scoped
/// stage-move row is <see cref="Domain.StageTransitionAssessment"/>
/// (<see cref="StageTransitionAssessor"/>) re-shaped the same way -- see
/// <see cref="AvailableActionProjector"/>. The Host revalidates every action fresh when it actually
/// executes (plan section 6.4: "a stale mutation is rejected without side effects"); this contract's
/// own <see cref="ExpectedStateVersion"/>/<see cref="IdempotencyKey"/> only let a client notice its
/// own view is stale before it tries.
/// </summary>
public sealed record AvailableAction(
    string SchemaVersion,
    string ActionId,
    string RationaleKey,
    IReadOnlyDictionary<string, string> RationaleArguments,
    AvailableActionTarget Target,
    long ExpectedStateVersion,
    SafetyClass SafetyClass,
    bool ConfirmationRequired,
    IReadOnlyList<AvailableActionInputField> InputFields,
    bool Enabled,
    IReadOnlyList<string> Blockers,
    Guid IdempotencyKey,
    StaleBehavior StaleBehavior)
{
    public const string ContractVersion = "1.0.0";
}

/// <summary>
/// Computes <see cref="AvailableAction"/> rows from a sprint's actual current durable state (plan
/// section 6.4) -- never a parallel policy engine. Sprint-scoped rows reuse
/// <see cref="ActiveOperationLookup"/> for stop eligibility and <see cref="StageTransitionAssessor"/>
/// (already Slice 3's own canonical prerequisite evaluator) for every stage-move candidate the
/// sprint's frozen graph declares; project-level rows reuse <see cref="StatusAdvisor.Recommend"/>'s
/// existing <see cref="SuggestedAction"/> list unchanged.
/// </summary>
public sealed class AvailableActionProjector(ISprintStore store, StageTransitionAssessor stageAssessor)
{
    public const string ResumeSprintActionId = "resume_sprint";
    public const string RunSprintActionId = "run_sprint";
    public const string CancelSprintActionId = "cancel_sprint";
    public const string StopCurrentOperationActionId = "stop_current_operation";
    public const string MoveToStageActionPrefix = "move_to_stage:";
    public const string RewindReasonField = "reason";

    /// <summary>Reshapes the project-level suggested-action list (recover-startup, initialize) into
    /// this richer contract. Every field not already present on <see cref="SuggestedAction"/>
    /// (confirmation requirement, input fields, enabled state, blockers) gets a fixed value: a
    /// suggestion is only ever offered once already actionable, so it is always enabled with no
    /// blockers and no further input.</summary>
    public static IReadOnlyList<AvailableAction> ForProject(
        string projectRoot, IReadOnlyList<SuggestedAction> suggested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(suggested);
        return
        [
            .. suggested.Select(action => new AvailableAction(
                AvailableAction.ContractVersion,
                action.ActionId,
                action.RationaleKey,
                action.RationaleArguments,
                new(projectRoot, null, null, null, null),
                action.ExpectedStateVersion,
                action.SafetyClass,
                action.SafetyClass != SafetyClass.Read,
                [],
                true,
                [],
                action.Command.IdempotencyKey,
                action.StaleBehavior)),
        ];
    }

    /// <summary>Every action a sprint's current durable state makes available: lifecycle actions
    /// (resume/run/cancel) gated on <see cref="SprintState"/> alone, stop gated on
    /// <see cref="ActiveOperationLookup"/>, and one stage-move candidate per node in the frozen graph
    /// other than the current stage, each independently assessed through
    /// <see cref="StageTransitionAssessor"/> -- bounded by the workflow's own declared node count,
    /// never by timeline size.</summary>
    public async Task<IReadOnlyList<AvailableAction>> ForSprintAsync(
        string projectRoot, Guid sprintId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        SprintId id = new(sprintId);
        SprintWorkflowState? state = await store.LoadAsync(projectRoot, id, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return [];
        }

        SprintDefinition? definition =
            await store.LoadDefinitionAsync(projectRoot, id, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return [];
        }

        List<AvailableAction> actions = [];
        long expectedVersion = state.LastSequence;
        AvailableActionTarget sprintTarget = new(projectRoot, sprintId, null, null, null);

        if (state.Sprint.State is SprintState.Paused or SprintState.Blocked or SprintState.Failed)
        {
            actions.Add(BuildSprintAction(
                ResumeSprintActionId, MessageKeys.WorkspaceActionResumeSprintRationale, sprintTarget, sprintId,
                state.Sprint.Version, SprintOrchestrator.ResumeSprintKey(state.Sprint),
                SafetyClass.ConfirmMutation, confirmationRequired: false));
        }

        if (state.Sprint.State is SprintState.Draft or SprintState.Ready)
        {
            actions.Add(BuildSprintAction(
                RunSprintActionId, MessageKeys.WorkspaceActionRunSprintRationale, sprintTarget, sprintId,
                state.Sprint.Version, SprintOrchestrator.RunSprintKey(state.Sprint),
                SafetyClass.ConfirmMutation, confirmationRequired: false));
        }

        if (!WorkflowStateMachines.IsTerminal(state.Sprint.State))
        {
            actions.Add(BuildSprintAction(
                CancelSprintActionId, MessageKeys.WorkspaceActionCancelSprintRationale, sprintTarget, sprintId,
                state.Sprint.Version, SprintOrchestrator.CancelSprintKey(state.Sprint),
                SafetyClass.ConfirmMutation, confirmationRequired: true));
        }

        AttemptSnapshot? active = ActiveOperationLookup.FindActive(state);
        if (active is not null)
        {
            AvailableActionTarget stopTarget = new(projectRoot, sprintId, active.NodeId, active.Id.Value, null);
            actions.Add(new(
                AvailableAction.ContractVersion,
                StopCurrentOperationActionId,
                MessageKeys.WorkspaceActionStopCurrentOperationRationale,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["attempt_id"] = active.Id.Value.ToString("D") },
                stopTarget,
                expectedVersion,
                SafetyClass.HumanApproval,
                true,
                [],
                true,
                [],
                StatusAdvisor.IdempotencyKey(
                    StopCurrentOperationActionId, new("attempt", active.Id.Value.ToString("D")), expectedVersion),
                StaleBehavior.RejectWithoutSideEffect));
        }

        if (!WorkflowStateMachines.IsTerminal(state.Sprint.State))
        {
            if (state.Sprint.PendingRewindTargetStageId is { } pendingRewindTarget)
            {
                // PR #101 review finding 3 (critical): an ordinary fresh stage-move candidate is not
                // offered while a prior rewind has not yet converged (round 2 review of PR #96's own
                // rule, reused here rather than re-derived) -- every such assessment would just report
                // stage_transition_rewind_in_progress uniformly, which is not actionable as a fresh
                // move. But that must not mean NO row at all: StageTransitionCoordinator.MoveAsync's
                // own resume path (its PendingRewindTargetStageId short-circuit) is reachable
                // regardless of Allowed, the same way `forge sprint move-stage` already reaches it by
                // ignoring Allowed for this call. Desktop renders exclusively from this list -- with no
                // row offered here at all, a Desktop user had no way to ever trigger the call that
                // finishes an interrupted rewind (only the CLI could). The target string passed to
                // AssessAsync is irrelevant here: StageTransitionAssessor.AssessAsync reports the
                // pending rewind's own recorded target before ever validating the one this call passes.
                StageTransitionAssessment resumeAssessment = await stageAssessor
                    .AssessAsync(projectRoot, id, pendingRewindTarget, cancellationToken).ConfigureAwait(false);
                if (resumeAssessment.Found)
                {
                    actions.Add(BuildResumeRewind(projectRoot, sprintId, resumeAssessment));
                }
            }
            else
            {
                string? currentStageId = StageTransitionAssessor.ResolveCurrentStageId(definition, state);
                foreach (NodeDefinition node in definition.Graph)
                {
                    if (string.Equals(node.Id, currentStageId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    StageTransitionAssessment assessment = await stageAssessor
                        .AssessAsync(projectRoot, id, node.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (assessment.Found)
                    {
                        actions.Add(BuildMoveToStage(projectRoot, sprintId, assessment));
                    }
                }
            }
        }

        return actions;
    }

    private static AvailableAction BuildSprintAction(
        string actionId,
        string rationaleKey,
        AvailableActionTarget target,
        Guid sprintId,
        long expectedVersion,
        Guid idempotencyKey,
        SafetyClass safetyClass,
        bool confirmationRequired) =>
        new(
            AvailableAction.ContractVersion,
            actionId,
            rationaleKey,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["sprint_id"] = sprintId.ToString("D") },
            target,
            expectedVersion,
            safetyClass,
            confirmationRequired,
            [],
            true,
            [],
            idempotencyKey,
            StaleBehavior.RejectWithoutSideEffect);

    private static AvailableAction BuildMoveToStage(
        string projectRoot, Guid sprintId, StageTransitionAssessment assessment)
    {
        string actionId = $"{MoveToStageActionPrefix}{assessment.TargetStageId}";
        AvailableActionTarget target = new(projectRoot, sprintId, null, null, assessment.TargetStageId);
        List<AvailableActionInputField> inputFields = assessment.Direction == StageTransitionDirection.Rewind
            ? [new(RewindReasonField, "string", true, SprintScheduler.MaxSupersessionInstructionLength)]
            : [];
        string rationaleKey = assessment.Direction switch
        {
            StageTransitionDirection.Advance => MessageKeys.WorkspaceActionMoveToStageAdvanceRationale,
            StageTransitionDirection.Rewind => MessageKeys.WorkspaceActionMoveToStageRewindRationale,
            _ => MessageKeys.WorkspaceActionMoveToStageSameRationale,
        };
        return new(
            AvailableAction.ContractVersion,
            actionId,
            rationaleKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sprint_id"] = sprintId.ToString("D"),
                ["target_stage_id"] = assessment.TargetStageId ?? string.Empty,
            },
            target,
            assessment.ExpectedStateVersion,
            assessment.Direction == StageTransitionDirection.Rewind ? SafetyClass.HumanApproval : SafetyClass.ConfirmMutation,
            assessment.ConfirmationRequired,
            inputFields,
            assessment.Allowed,
            [.. assessment.UnsatisfiedPrerequisites.Select(prerequisite => prerequisite.MessageKey)],
            StatusAdvisor.IdempotencyKey(
                actionId, new("sprint", sprintId.ToString("D")), assessment.ExpectedStateVersion),
            StaleBehavior.RejectWithoutSideEffect);
    }

    /// <summary>PR #101 review finding 3: the one row offered while a rewind has not yet converged --
    /// built from <see cref="BuildMoveToStage"/>'s own shape (same target/idempotency/blockers), but
    /// with two fields corrected for what a resume actually is. <see cref="AvailableAction.Enabled"/>
    /// is forced <see langword="true"/>: <paramref name="assessment"/>.Allowed is unconditionally
    /// <see langword="false"/> for a <c>stage_transition_rewind_in_progress</c> assessment (it means
    /// "not a legal fresh move", never "cannot be resumed"), but
    /// <see cref="StageTransitionCoordinator.MoveAsync"/>'s own resume path bypasses
    /// <see cref="StageTransitionAssessment.Allowed"/> entirely for exactly this diagnostic -- a
    /// disabled row here would recreate the same dead end this finding closes. The rationale key is
    /// its own distinct <see cref="MessageKeys.WorkspaceActionResumeRewindRationale"/>, not the
    /// ordinary rewind rationale, so a Desktop reader is told this specific row resumes an already
    /// in-flight operation rather than starting a fresh one.</summary>
    private static AvailableAction BuildResumeRewind(
        string projectRoot, Guid sprintId, StageTransitionAssessment assessment) =>
        BuildMoveToStage(projectRoot, sprintId, assessment) with
        {
            Enabled = true,
            RationaleKey = MessageKeys.WorkspaceActionResumeRewindRationale,
            // Unlike a fresh rewind, a resume carries no caller-supplied reason of its own -- the
            // coordinator's own resume path reuses the reason already recorded when the rewind first
            // committed, ignoring whatever a caller passes now.
            InputFields = [],
        };
}
