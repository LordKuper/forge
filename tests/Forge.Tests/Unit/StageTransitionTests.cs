using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 8 / ADR 0045-0047's stage-transition backend (Slice 3): prerequisite
/// evaluation, advance/rewind commits, supersession, idempotent replay, and terminal-sprint
/// rejection.</summary>
public sealed class StageTransitionTests
{
    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AdvanceToTheNormalNextStageActivatesIt()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, assessment.Direction);
        Assert.True(assessment.Allowed);
        Assert.Empty(assessment.UnsatisfiedPrerequisites);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AdvanceSkipAheadActivatesTargetWhenEveryIntermediateStageIsAlreadySatisfied()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "c", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, assessment.Direction);
        Assert.True(assessment.Allowed);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "c", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AdvanceSkipAheadIsRejectedWithoutFabricatingCompletionWhenAnIntermediateStageIsUnsatisfied()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        // "b" is deliberately left unstarted.

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "c", cancellationToken);
        Assert.False(assessment.Allowed);
        Assert.Contains(
            assessment.UnsatisfiedPrerequisites,
            prerequisite => prerequisite.Id == StagePrerequisiteIds.PredecessorSuccess &&
                prerequisite.Arguments["stage_id"] == "b");

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "c", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, Guid.NewGuid(), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowBlocked, result.DiagnosticCode);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        // "b" is ordinarily promoted to `ready` (eligible, not yet run) the moment "a" succeeds --
        // the rejected move must not fabricate it any further than that.
        Assert.Equal(NodeState.Ready, final.Nodes["b"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AdvanceSkipAheadSkipsAnUnmetOptionalIntermediateStage()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph:
                [
                    new("a", NodeKind.Work, []),
                    new("b", NodeKind.Work, ["a"], Optional: true),
                    new("c", NodeKind.Work, ["b"]),
                ]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        // "b" is optional and deliberately left unstarted.

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "c", cancellationToken);
        Assert.True(assessment.Allowed);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "c", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Skipped, final.Nodes["b"].State);
        Assert.Equal(NodeState.Ready, final.Nodes["c"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewindIncrementsRevisionExactlyOnceAndSupersedesDownstreamEvidence()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "c", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Assert.Equal(StageTransitionDirection.Rewind, assessment.Direction);
        Assert.True(assessment.Allowed);
        Assert.True(assessment.ConfirmationRequired);
        Assert.NotNull(assessment.Supersession);
        Assert.Equal(3, assessment.Supersession!.AttemptIds.Count);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "redo from the start", true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new StageRevision(1), result.Sprint!.Revision);
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);

        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(new StageRevision(1), final.Sprint.Revision);
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
        Assert.Equal(0, final.Nodes["a"].AttemptCount);
        Assert.Equal(NodeState.Pending, final.Nodes["b"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        Assert.Equal(SprintState.Ready, final.Sprint.State);

        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.All(results, result => Assert.NotNull(result.Superseded));
        Assert.All(results, result => Assert.Equal(new StageRevision(1), result.Superseded!.AtRevision));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersededConfirmationEvidenceNeverSatisfiesTestWorkEligibility()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, _, _) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph:
                [
                    new("confirm", NodeKind.Work, [], NodeRole.Confirmation),
                    new("test_work", NodeKind.Work, ["confirm"], NodeRole.TestWork),
                ]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordConfirmationResult confirmation = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "definition of done",
            [new ConfirmationEvidence(ConfirmationEvidenceKind.Inspection, "inspected the change")], cancellationToken);
        Assert.True(confirmation.Succeeded);

        SprintDefinition definition =
            (await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        NodeDefinition testWorkNode = definition.Graph.Single(node => node.Id == "test_work");

        Assert.True(await scheduler.IsTestWorkEligibleAsync(
            environment.ProjectRoot, sprintId, definition, testWorkNode, cancellationToken));

        await store.MarkConfirmationSupersededAsync(
            environment.ProjectRoot, sprintId, confirmation.Confirmation!.ConfirmationId,
            new SupersededBy(new StageRevision(1), DateTimeOffset.UtcNow), cancellationToken);

        Assert.False(await scheduler.IsTestWorkEligibleAsync(
            environment.ProjectRoot, sprintId, definition, testWorkNode, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewindStopsTheActiveOperationFirstBeforeInvalidatingItsNode()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        // "b" is left actively running (never completed) -- the exact active operation a rewind to
        // "a" must stop before invalidating "b".
        long bVersion = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["b"].Version;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "b", bVersion, cancellationToken);
        Assert.True(started.Succeeded);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Assert.Equal(StageTransitionDirection.Rewind, assessment.Direction);
        Assert.True(assessment.ActiveOperation.HasActiveOperation);
        Assert.True(assessment.ActiveOperation.StopRequired);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "restart from a", true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot stoppedAttempt = final.Attempts[started.AttemptId!.Value.ToString("D")];
        Assert.Equal(AttemptState.Cancelled, stoppedAttempt.State);
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewindWithoutABoundedReasonIsRejectedWithoutSideEffects()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Assert.Equal(StageTransitionDirection.Rewind, assessment.Direction);

        MoveStageResult withoutReason = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "   ", true, Guid.NewGuid(), cancellationToken);
        Assert.False(withoutReason.Succeeded);
        Assert.Equal(DiagnosticCodes.StageTransitionReasonRequired, withoutReason.DiagnosticCode);

        MoveStageResult unconfirmed = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "a real reason", false, Guid.NewGuid(), cancellationToken);
        Assert.False(unconfirmed.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfirmationRequired, unconfirmed.DiagnosticCode);

        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(StageRevision.Initial, final.Sprint.Revision);
        Assert.Equal(NodeState.Succeeded, final.Nodes["a"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayingTheSameIdempotencyKeyNeverCreatesASecondRevision()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Guid idempotencyKey = Guid.NewGuid();
        MoveStageResult first = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "redo", true, idempotencyKey, cancellationToken);
        Assert.True(first.Succeeded);
        Assert.Equal(new StageRevision(1), first.Sprint!.Revision);

        MoveStageResult replay = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "redo", true, idempotencyKey, cancellationToken);

        Assert.True(replay.Succeeded);
        Assert.Equal(new StageRevision(1), replay.Sprint!.Revision);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(new StageRevision(1), final.Sprint.Revision);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TerminalSprintsCannotBeMoved()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, _, StageTransitionCoordinator coordinator, StageTransitionAssessor assessor) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        SprintSnapshot draft = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Version, SprintOrchestrator.CancelSprintKey(draft)),
            cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Assert.True(assessment.Found);
        Assert.False(assessment.Allowed);
        Assert.Equal(DiagnosticCodes.SprintTransitionInvalid, assessment.DiagnosticCode);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", assessment.ExpectedStateVersion, assessment.AssessmentToken, null,
            true, Guid.NewGuid(), cancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTransitionInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AStaleAssessmentTokenIsRejectedWithoutSideEffects()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StageTransitionAssessment staleAssessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);

        // The sprint's state moves on (a completes) after the assessment above was captured but
        // before it is presented to the commit -- the exact staleness window plan section 8.5 guards.
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        long versionBeforeCommitAttempt = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Sprint.Version;

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", staleAssessment.ExpectedStateVersion,
            staleAssessment.AssessmentToken, null, true, Guid.NewGuid(), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
        long versionAfterRejection = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Sprint.Version;
        Assert.Equal(versionBeforeCommitAttempt, versionAfterRejection);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnOpenFindingBlocksAdvanceUntilResolved()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, _, StageTransitionAssessor assessor) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        RecordFindingResult finding = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["evidence"], null, null, cancellationToken);
        Assert.True(finding.Succeeded);

        StageTransitionAssessment blocked =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Assert.False(blocked.Allowed);
        Assert.Contains(
            blocked.UnsatisfiedPrerequisites, prerequisite => prerequisite.Id == StagePrerequisiteIds.NoBlockingFindings);

        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, finding.Finding!.FindingId, FindingStatus.Resolved, cancellationToken);

        StageTransitionAssessment recovered =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Assert.True(recovered.Allowed);
    }

    /// <summary>Round 1 review of PR #96 (finding 3): `NoBlockingFindings`/`ProviderModelPolicy`/
    /// `GitIsolation`/`RetryBudget` were originally applied to both directions, which made a rewind
    /// impossible in exactly the state it exists to recover from. An open finding still correctly
    /// blocks an advance, but must never block the rewind whose own supersession is what actually
    /// resolves it.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewindSucceedsDespiteAnOpenFindingThatWouldStillBlockAnAdvance()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        RecordFindingResult finding = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["evidence"], null, null, cancellationToken);
        Assert.True(finding.Succeeded);

        StageTransitionAssessment advanceBlocked =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "c", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, advanceBlocked.Direction);
        Assert.False(advanceBlocked.Allowed);
        Assert.Contains(
            advanceBlocked.UnsatisfiedPrerequisites,
            prerequisite => prerequisite.Id == StagePrerequisiteIds.NoBlockingFindings);

        StageTransitionAssessment rewindAssessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Assert.Equal(StageTransitionDirection.Rewind, rewindAssessment.Direction);
        Assert.True(rewindAssessment.Allowed);
        Assert.DoesNotContain(
            rewindAssessment.UnsatisfiedPrerequisites,
            prerequisite => prerequisite.Id == StagePrerequisiteIds.NoBlockingFindings);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", rewindAssessment.ExpectedStateVersion,
            rewindAssessment.AssessmentToken, "redo despite the open finding", true, Guid.NewGuid(),
            cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);
    }

    /// <summary>Round 1 review of PR #96 (finding 5): plan section 8.4 calls the rewind reason
    /// "bounded", but only non-empty was ever enforced. Reuses
    /// <see cref="SprintScheduler.MaxSupersessionInstructionLength"/>, the same limit ADR 0006
    /// already established for the equivalent human-authored bounded artifact.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewindReasonExceedingTheBoundIsRejectedWithoutSideEffects()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);

        string tooLong = new('x', SprintScheduler.MaxSupersessionInstructionLength + 1);
        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            tooLong, true, Guid.NewGuid(), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SupersessionInstructionTooLong, result.DiagnosticCode);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(StageRevision.Initial, final.Sprint.Revision);
    }

    /// <summary>Round 1 review of PR #96 (finding 4): <c>CommitAdvanceAsync</c> never recorded the
    /// caller's idempotency key, so a replayed advance fell through to a fresh (now-stale) assessment
    /// and was rejected as `suggestion_stale` instead of returning the original result -- contradicting
    /// plan section 8.5's "repeating the same move is safe" for the one direction this test covers.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayingTheSameIdempotencyKeyForAnAdvanceReturnsTheOriginalResult()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Guid idempotencyKey = Guid.NewGuid();

        MoveStageResult first = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, idempotencyKey, cancellationToken);
        Assert.True(first.Succeeded);
        Assert.Equal(NodeState.Ready, first.TargetNode!.State);

        MoveStageResult replay = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, idempotencyKey, cancellationToken);

        Assert.True(replay.Succeeded, $"diag={replay.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, replay.TargetNode!.State);
    }

    /// <summary>Round 1 review of PR #96 (finding 2): a parallel DAG can have more than one node
    /// `Running` at once, but step 1 originally stopped only the first one found anywhere in the
    /// sprint, leaving every other running branch's node completely untouched by the rewind. Both
    /// branches here must be stopped and invalidated by a single rewind to their common ancestor.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RewindStopsEveryRunningNodeInAParallelDownstreamClosureNotOnlyTheFirst()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph:
                [
                    new("a", NodeKind.Work, []),
                    new("b1", NodeKind.Work, ["a"]),
                    new("b2", NodeKind.Work, ["a"]),
                ]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        SprintWorkflowState beforeStart =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult startedB1 = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "b1", beforeStart.Nodes["b1"].Version, cancellationToken);
        Assert.True(startedB1.Succeeded);
        StartAttemptResult startedB2 = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "b2", beforeStart.Nodes["b2"].Version, cancellationToken);
        Assert.True(startedB2.Succeeded);

        SprintWorkflowState runningBoth =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, runningBoth.Nodes["b1"].State);
        Assert.Equal(NodeState.Running, runningBoth.Nodes["b2"].State);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Assert.Equal(StageTransitionDirection.Rewind, assessment.Direction);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            "rewind past both branches", true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Cancelled, final.Attempts[startedB1.AttemptId!.Value.ToString("D")].State);
        Assert.Equal(AttemptState.Cancelled, final.Attempts[startedB2.AttemptId!.Value.ToString("D")].State);
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["b1"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["b2"].State);
        Assert.Equal(0, final.Nodes["b1"].AttemptCount);
        Assert.Equal(0, final.Nodes["b2"].AttemptCount);
    }

    /// <summary>Round 1 review of PR #96 (finding 1), reproducing the reviewer's own repro: manually
    /// appends exactly <c>CommitRewindAsync</c>'s own step-2 event (the durable revision increment)
    /// for a given idempotency key and stops there -- simulating a Host crash after step 2 but before
    /// steps 3-6 (evidence supersession, node reopen/invalidate, graph re-advance, sprint-ready walk)
    /// ever ran. Before the fix, the outer replay check keyed on the same raw ledger entry step 2
    /// writes, so a blind replay of that key reported a clean success for a rewind that had only
    /// bumped the revision counter -- the target still showed its pre-rewind terminal outcome and
    /// nothing downstream was touched. The fix must never silently report that false success: a
    /// replay with the caller's now-stale tokens is safely refused, and a genuine retry (a fresh
    /// assessment, same idempotency key) converges the rewind fully.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashAfterTheRevisionEventButBeforeTheRestOfTheRewindConvergesOnRetryInsteadOfFalseSuccess()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "c", cancellationToken);

        StageTransitionAssessment originalAssessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        Guid idempotencyKey = Guid.NewGuid();

        // Simulates the crash: exactly CommitRewindAsync's own step 2, nothing else.
        SprintWorkflowState beforeCrash = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AppendOutcome midSaga = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", beforeCrash.Sprint.Revision.Next(),
            beforeCrash.Sprint.Version, idempotencyKey, cancellationToken);
        Assert.True(midSaga.Succeeded);

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(new StageRevision(1), wedged.Sprint.Revision);
        // The exact half-finished state the bug used to report as a clean success.
        Assert.Equal(NodeState.Succeeded, wedged.Nodes["a"].State);

        // A blind replay carrying the caller's original (now-stale) tokens must be refused, never
        // told the rewind already succeeded.
        MoveStageResult blindReplay = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", originalAssessment.ExpectedStateVersion,
            originalAssessment.AssessmentToken, "redo from the start", true, idempotencyKey, cancellationToken);
        Assert.False(blindReplay.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, blindReplay.DiagnosticCode);

        // A genuine retry -- a fresh assessment, the same idempotency key -- must converge the
        // rewind fully rather than short-circuiting on the half-finished ledger entry step 2 left
        // behind.
        StageTransitionAssessment retryAssessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "a", cancellationToken);
        MoveStageResult converged = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", retryAssessment.ExpectedStateVersion,
            retryAssessment.AssessmentToken, "redo from the start", true, idempotencyKey, cancellationToken);

        Assert.True(converged.Succeeded, $"diag={converged.DiagnosticCode}");
        Assert.Equal(new StageRevision(1), converged.Sprint!.Revision);
        Assert.Equal(NodeState.Ready, converged.TargetNode!.State);

        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(new StageRevision(1), final.Sprint.Revision);
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["b"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.All(results, result => Assert.NotNull(result.Superseded));

        // A further replay of the same key, now that the saga has actually converged, safely returns
        // the same result again without incrementing the revision a second time.
        MoveStageResult replayAfterConvergence = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", retryAssessment.ExpectedStateVersion,
            retryAssessment.AssessmentToken, "redo from the start", true, idempotencyKey, cancellationToken);
        Assert.True(replayAfterConvergence.Succeeded);
        Assert.Equal(new StageRevision(1), replayAfterConvergence.Sprint!.Revision);
    }

    private static (SprintOrchestrator, SprintScheduler, StageTransitionCoordinator, StageTransitionAssessor) Resolve(
        TestEnvironment environment) =>
        (
            environment.Resolve<SprintOrchestrator>(),
            environment.Resolve<SprintScheduler>(),
            environment.Resolve<StageTransitionCoordinator>(),
            environment.Resolve<StageTransitionAssessor>());

    private static async Task<SprintId> CreateLinearSprintAsync(
        SprintOrchestrator orchestrator, string root, CancellationToken cancellationToken, params string[] nodeIds)
    {
        List<NodeDefinition> graph = [];
        for (int index = 0; index < nodeIds.Length; index++)
        {
            graph.Add(new(nodeIds[index], NodeKind.Work, index == 0 ? [] : [nodeIds[index - 1]]));
        }

        CreateSprintResult created =
            await orchestrator.CreateSprintAsync(new(root, 1, Guid.NewGuid(), Graph: graph), cancellationToken);
        Assert.True(created.Succeeded);
        return created.SprintId!;
    }

    private static async Task CompleteWorkNodeAsync(
        SprintScheduler scheduler,
        ISprintStore store,
        string root,
        SprintId sprintId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        long version = (await store.LoadAsync(root, sprintId, cancellationToken))!.Nodes[nodeId].Version;
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(root, sprintId, nodeId, version, cancellationToken);
        Assert.True(started.Succeeded);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            root, sprintId, nodeId, started.AttemptId!, true, SampleDigest, [], [], cancellationToken);
        Assert.True(completed.Succeeded);
    }

    private static async Task RunToRunningAsync(
        SprintOrchestrator orchestrator, string root, SprintId sprintId, CancellationToken cancellationToken)
    {
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(root, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(root, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(root, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
    }

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
