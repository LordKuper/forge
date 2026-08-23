using System.Globalization;
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
    /// ever ran. Before the round 1 fix, the outer replay check keyed on the same raw ledger entry
    /// step 2 writes, so a blind replay of that key reported a clean success for a rewind that had
    /// only bumped the revision counter -- the target still showed its pre-rewind terminal outcome
    /// and nothing downstream was touched.
    ///
    /// Round 2 review of PR #96 (critical) found the round 1 fix only narrowed this window rather
    /// than closing it: it made a *blind stale replay* safely refused, but any call that first
    /// re-assessed (getting a fresh token) still re-derived <c>Direction</c> from the now-drifted node
    /// state instead of recognizing the in-flight rewind, and could misclassify the retry entirely
    /// (see the crash-at-later-steps tests below for the concrete failure). The real fix
    /// (<c>SprintSnapshot.PendingRewindTargetStageId</c>) makes resumption unconditional: *any*
    /// subsequent <c>MoveAsync</c> call against this sprint -- even one carrying the original,
    /// already-stale tokens, as this test uses -- re-enters <c>CommitRewindAsync</c> directly for the
    /// recorded target/reason/key and converges the rewind fully, bypassing assessment/staleness/
    /// confirmation entirely (those gates exist only for *starting* a new operation).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashAfterTheRevisionEventButBeforeTheRestOfTheRewindConvergesOnTheNextCallEvenWithStaleTokens()
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
        Assert.Equal("a", wedged.Sprint.PendingRewindTargetStageId);

        // A blind replay carrying the caller's original (now-stale) tokens now resumes and converges
        // the rewind fully -- it is no longer treated as "starting a new operation" that a stale token
        // could block, but as finishing one already committed to.
        MoveStageResult blindReplay = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", originalAssessment.ExpectedStateVersion,
            originalAssessment.AssessmentToken, "redo from the start", true, idempotencyKey, cancellationToken);
        Assert.True(blindReplay.Succeeded, $"diag={blindReplay.DiagnosticCode}");
        Assert.Equal(new StageRevision(1), blindReplay.Sprint!.Revision);
        Assert.Equal(NodeState.Ready, blindReplay.TargetNode!.State);

        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(new StageRevision(1), final.Sprint.Revision);
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["b"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        Assert.Equal(SprintState.Ready, final.Sprint.State);
        Assert.Null(final.Sprint.PendingRewindTargetStageId);
        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.All(results, result => Assert.NotNull(result.Superseded));
        Assert.All(results, result => Assert.Equal(new StageRevision(1), result.Superseded!.AtRevision));

        // A further replay of the same key, now that the saga has actually converged, safely returns
        // the same result again without incrementing the revision a second time.
        MoveStageResult replayAfterConvergence = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", originalAssessment.ExpectedStateVersion,
            originalAssessment.AssessmentToken, "redo from the start", true, idempotencyKey, cancellationToken);
        Assert.True(replayAfterConvergence.Succeeded);
        Assert.Equal(new StageRevision(1), replayAfterConvergence.Sprint!.Revision);
    }

    /// <summary>Round 2 review of PR #96 (critical), reconstructing the reviewer's own "Window A"
    /// repro: crash mid-step-4, after the target has already been reopened to `ready` but before any
    /// downstream sibling has been invalidated. Before this fix, a fresh assessment saw the target as
    /// the sprint's own "current" stage and flipped <c>Direction</c> to <c>Advance</c>, so
    /// <c>CommitAdvanceAsync</c> ran instead, saw the target already non-`pending`, reported success,
    /// and durably sealed the half-finished rewind as if it were a completed advance -- the downstream
    /// siblings stayed `Succeeded` forever. The resume path must recognize the durable marker before
    /// any direction is ever derived, so the very next call -- even one carrying a deliberately wrong
    /// target, no reason, an unconfirmed flag, and an unrelated fresh idempotency key, none of which
    /// resuming may honor -- still converges the *original* rewind to its recorded target.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashMidStep4AfterReopeningTheTargetButBeforeInvalidatingSiblingsResumesCorrectlyInsteadOfMisreadingAsAnAdvance()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator, _) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "c", cancellationToken);

        Guid idempotencyKey = Guid.NewGuid();
        StageRevision revision = new(1);

        // Step 2: durably record the rewind.
        SprintWorkflowState beforeCrash = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AppendOutcome step2 = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", revision, beforeCrash.Sprint.Version,
            idempotencyKey, cancellationToken);
        Assert.True(step2.Succeeded);

        // Step 3: supersede every downstream node's evidence (a, b, c).
        foreach (string nodeId in new[] { "a", "b", "c" })
        {
            await SupersedeNodeResultDirectlyAsync(store, environment.ProjectRoot, sprintId, nodeId, revision, cancellationToken);
        }

        // Step 4, partial: only the target is reopened -- the exact "Window A" crash point.
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "a", isTarget: true, revision, cancellationToken);

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, wedged.Nodes["a"].State);
        Assert.Equal(NodeState.Succeeded, wedged.Nodes["b"].State);
        Assert.Equal(NodeState.Succeeded, wedged.Nodes["c"].State);
        Assert.Equal("a", wedged.Sprint.PendingRewindTargetStageId);

        // The next call carries deliberately wrong/garbage arguments -- a different target, no reason,
        // unconfirmed, a stale expected version, a bogus token, and an unrelated fresh idempotency key
        // -- none of which the resume path may honor; only the durably recorded rewind may.
        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "c", -1, "bogus-token", null, false, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal("a", result.TargetNode!.Id.Value);
        Assert.Equal(NodeState.Ready, result.TargetNode.State);

        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(revision, final.Sprint.Revision);
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
        Assert.Equal(0, final.Nodes["a"].AttemptCount);
        Assert.Equal(NodeState.Pending, final.Nodes["b"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        Assert.Equal(SprintState.Ready, final.Sprint.State);
        Assert.Null(final.Sprint.PendingRewindTargetStageId);

        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.NotNull(result.Superseded));
        Assert.All(results, result => Assert.Equal(revision, result.Superseded!.AtRevision));
    }

    /// <summary>Round 2 review of PR #96 (critical), reconstructing the reviewer's own "Window B"
    /// repro: crash after step 4 fully finishes (every downstream node already reset) but before
    /// steps 5-6 (graph re-advance, sprint-ready walk) ever run. Before this fix, with no node left
    /// `Succeeded`/`Skipped`, direction-resolution fell back to treating the target as the graph's
    /// only settled node, so <c>Direction</c> became permanently <c>Same</c> and every subsequent
    /// <c>MoveAsync</c> call was rejected before ever reaching <c>CommitRewindAsync</c> again -- a
    /// permanently wedged sprint no client action could recover, still finalizable via
    /// <c>CompleteSprintAsync</c> despite zero real work having been redone. This test proves both
    /// halves of the fix: finalization is refused while the marker holds, and the very next
    /// <c>MoveAsync</c> call (again with garbage arguments the resume path may not honor) converges
    /// the rewind the rest of the way, unwedging the sprint.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashAfterStep4ButBeforeStepsFiveAndSixIsRecoveredRatherThanPermanentlyWedgedOrFinalizable()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator, _) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "c", cancellationToken);

        // Every node already settled good, so completing "c" already drove the sprint to
        // `ready_to_finalize` -- the exact state the rewind commits against in this repro.
        SprintWorkflowState beforeRewind = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.ReadyToFinalize, beforeRewind.Sprint.State);

        Guid idempotencyKey = Guid.NewGuid();
        StageRevision revision = new(1);

        AppendOutcome step2 = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", revision, beforeRewind.Sprint.Version,
            idempotencyKey, cancellationToken);
        Assert.True(step2.Succeeded);

        foreach (string nodeId in new[] { "a", "b", "c" })
        {
            await SupersedeNodeResultDirectlyAsync(store, environment.ProjectRoot, sprintId, nodeId, revision, cancellationToken);
        }

        // Step 4, fully finished: target reopened, both downstream siblings invalidated.
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "a", isTarget: true, revision, cancellationToken);
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "b", isTarget: false, revision, cancellationToken);
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "c", isTarget: false, revision, cancellationToken);

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, wedged.Nodes["a"].State);
        Assert.Equal(NodeState.Pending, wedged.Nodes["b"].State);
        Assert.Equal(NodeState.Pending, wedged.Nodes["c"].State);
        // Steps 5-6 never ran: the sprint itself is still exactly where it was before the rewind
        // started -- the "no node left Succeeded/Skipped, sprint still ready_to_finalize" wedge.
        Assert.Equal(SprintState.ReadyToFinalize, wedged.Sprint.State);
        Assert.Equal("a", wedged.Sprint.PendingRewindTargetStageId);

        // The danger the round 2 report called out by name: finalizing this sprint would seal a
        // half-finished rewind (zero real work redone) as a genuinely completed one.
        SprintTransitionResult finalizeAttempt =
            await scheduler.CompleteSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.False(finalizeAttempt.Succeeded);
        Assert.Equal(DiagnosticCodes.StageTransitionRewindInProgress, finalizeAttempt.DiagnosticCode);
        SprintWorkflowState stillWedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.ReadyToFinalize, stillWedged.Sprint.State);

        // The next call, garbage arguments and all, must resume and converge rather than being
        // rejected as `sprint_transition_invalid` (Direction stuck at Same) or silently doing nothing.
        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", 999, "bogus-token", null, false, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, final.Nodes["a"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["b"].State);
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        Assert.Equal(SprintState.Ready, final.Sprint.State);
        Assert.Null(final.Sprint.PendingRewindTargetStageId);

        // Now genuinely unwedged: finalization is refused for the ordinary reason (not ready to
        // finalize), never again for the rewind-in-progress reason.
        SprintTransitionResult afterConvergence =
            await scheduler.CompleteSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.False(afterConvergence.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTransitionInvalid, afterConvergence.DiagnosticCode);
    }

    /// <summary>Round 2 review of PR #96 (critical): the crash window between step 5 (graph
    /// re-advance) and step 6 (the sprint-ready walk) -- every node already reset and step 5 already
    /// run, but the sprint itself has not yet been walked back to `ready`.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashAfterStepFiveButBeforeStepSixConvergesTheSprintReadyWalkOnResume()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator, _) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "c", cancellationToken);

        Guid idempotencyKey = Guid.NewGuid();
        StageRevision revision = new(1);
        SprintWorkflowState beforeRewind = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        AppendOutcome step2 = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", revision, beforeRewind.Sprint.Version,
            idempotencyKey, cancellationToken);
        Assert.True(step2.Succeeded);

        foreach (string nodeId in new[] { "a", "b", "c" })
        {
            await SupersedeNodeResultDirectlyAsync(store, environment.ProjectRoot, sprintId, nodeId, revision, cancellationToken);
        }

        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "a", isTarget: true, revision, cancellationToken);
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "b", isTarget: false, revision, cancellationToken);
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "c", isTarget: false, revision, cancellationToken);

        // Step 5: recompute eligible stages from the frozen DAG (a is already `ready`, so this is a
        // no-op here -- exercised anyway to prove resuming past this exact point is safe).
        await scheduler.AdvanceGraphAsync(environment.ProjectRoot, sprintId, cancellationToken);

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.ReadyToFinalize, wedged.Sprint.State);
        Assert.Equal("a", wedged.Sprint.PendingRewindTargetStageId);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", 0, null, null, false, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Ready, final.Sprint.State);
        Assert.Null(final.Sprint.PendingRewindTargetStageId);
    }

    /// <summary>Round 2 review of PR #96 (critical): the crash window inside step 6's own multi-hop
    /// walk -- the sprint has already taken the first hop (`ready_to_finalize -> blocked`) but not
    /// yet the second (`blocked -> ready`).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashMidStepSixsOwnMultiHopWalkFinishesTheRemainingHopOnResume()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator, _) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b", "c");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "c", cancellationToken);

        Guid idempotencyKey = Guid.NewGuid();
        StageRevision revision = new(1);
        SprintWorkflowState beforeRewind = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        AppendOutcome step2 = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", revision, beforeRewind.Sprint.Version,
            idempotencyKey, cancellationToken);
        Assert.True(step2.Succeeded);

        foreach (string nodeId in new[] { "a", "b", "c" })
        {
            await SupersedeNodeResultDirectlyAsync(store, environment.ProjectRoot, sprintId, nodeId, revision, cancellationToken);
        }

        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "a", isTarget: true, revision, cancellationToken);
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "b", isTarget: false, revision, cancellationToken);
        await ReopenNodeDirectlyAsync(store, environment.ProjectRoot, sprintId, "c", isTarget: false, revision, cancellationToken);
        await scheduler.AdvanceGraphAsync(environment.ProjectRoot, sprintId, cancellationToken);

        // Step 6, first hop only: `ready_to_finalize -> blocked`, matching
        // DriveSprintTowardReadyAsync's own first switch arm exactly.
        SprintWorkflowState beforeHop = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_blocked", WorkflowStateNames.ToSnakeCase(SprintState.Blocked), beforeHop.Sprint.Version,
            Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.BlockedReasonArgument] = "rewind" });

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, wedged.Sprint.State);
        Assert.Equal("a", wedged.Sprint.PendingRewindTargetStageId);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", 0, null, null, false, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Ready, final.Sprint.State);
        Assert.Null(final.Sprint.PendingRewindTargetStageId);
    }

    /// <summary>Round 2 review of PR #96: <c>AssessStageTransition</c> must surface an in-flight,
    /// unconverged rewind directly rather than silently re-deriving (and misreporting) <c>Direction</c>
    /// from the drifted node state -- regardless of which target the caller actually asked about.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AssessStageTransitionSurfacesAnUnconvergedRewindInsteadOfMisreportingDirection()
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
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        SprintWorkflowState beforeCrash = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AppendOutcome step2 = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", new StageRevision(1),
            beforeCrash.Sprint.Version, Guid.NewGuid(), cancellationToken);
        Assert.True(step2.Succeeded);

        foreach (string queriedTarget in new[] { "a", "b" })
        {
            StageTransitionAssessment assessment =
                await assessor.AssessAsync(environment.ProjectRoot, sprintId, queriedTarget, cancellationToken);
            Assert.True(assessment.Found);
            Assert.False(assessment.Allowed);
            Assert.Equal(DiagnosticCodes.StageTransitionRewindInProgress, assessment.DiagnosticCode);
            Assert.Equal(StageTransitionDirection.Rewind, assessment.Direction);
            // The recorded rewind's real target ("a"), never whatever stage was actually queried.
            Assert.Equal("a", assessment.TargetStageId);
        }
    }

    /// <summary>Round 2 review of PR #96 (non-critical contract mismatch): <c>MoveAsync</c> required
    /// <c>confirmed == true</c> unconditionally, including for an advance, while
    /// <c>AssessStageTransition</c>'s own response reports <c>ConfirmationRequired == false</c> for an
    /// advance (plan section 8.3 requires no confirmation to move into normal, unstarted territory) --
    /// a client that trusted the assessment's own field, rather than blindly always passing
    /// <see langword="true"/>, could never advance at all.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AdvanceDoesNotRequireConfirmationMatchingItsOwnAssessment()
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
        Assert.False(assessment.ConfirmationRequired);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, false, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);
    }

    private static async Task SupersedeNodeResultDirectlyAsync(
        ISprintStore store, string projectRoot, SprintId sprintId, string nodeId, StageRevision revision,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NodeResult> results = await store.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken);
        NodeResult result = results.Single(item => item.NodeId.Value == nodeId);
        await store.MarkNodeResultSupersededAsync(
            projectRoot, sprintId, result.AttemptId, new SupersededBy(revision, DateTimeOffset.UtcNow), cancellationToken);
    }

    private static async Task ReopenNodeDirectlyAsync(
        ISprintStore store, string projectRoot, SprintId sprintId, string nodeId, bool isTarget, StageRevision revision,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = (await store.LoadAsync(projectRoot, sprintId, cancellationToken))!;
        NodeSnapshot node = state.Nodes[nodeId];
        NodeState toState = isTarget ? NodeState.Ready : NodeState.Pending;
        string messageKey = isTarget ? "workflow.node_reopened" : "workflow.node_invalidated";
        Dictionary<string, string?> extra = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.RevisionArgument] = revision.Value.ToString(CultureInfo.InvariantCulture),
            [WorkflowEvent.AttemptNumberArgument] = "0",
        };
        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", messageKey,
            WorkflowStateNames.ToSnakeCase(toState), node.Version, Guid.NewGuid(), cancellationToken, extra);
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
