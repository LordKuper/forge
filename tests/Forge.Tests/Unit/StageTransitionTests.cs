using System.Globalization;
using Forge.Application;
using Forge.Configuration;
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

    /// <summary>Post-release audit (PR #101): a genuinely concurrent conflict during step 6's own append (not a Host
    /// crash) must not let <c>CommitRewindAsync</c> mark the saga durably converged. Before the fix,
    /// <c>DriveSprintTowardReadyAsync</c> silently swallowed a version conflict and
    /// <c>CommitRewindAsync</c> appended <see cref="ISprintStore.AppendStageTransitionConvergedAsync"/>
    /// unconditionally right after -- sealing a rewind that never actually finished walking the sprint
    /// back to `ready`, with nothing left to ever resume it (the converged marker is what clears
    /// <c>PendingRewindTargetStageId</c>, the only signal the resume path checks). Reaches the exact
    /// same "right before step 6" state as <see cref="ACrashAfterStepFiveButBeforeStepSixConvergesTheSprintReadyWalkOnResume"/>,
    /// then injects the conflict deterministically via a <see cref="FlakySprintStore"/> that fails the
    /// very first <c>AppendTransitionAsync</c> call the resumed saga makes -- step 6's own first hop
    /// (`ready_to_finalize -&gt; blocked`), since steps 1-5 make none of their own in this already-settled
    /// state.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AConcurrentConflictDuringStepSixsOwnAppendDoesNotConvergeTheSagaAndResumesCleanlyOnRetry()
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

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.ReadyToFinalize, wedged.Sprint.State);
        Assert.Equal("a", wedged.Sprint.PendingRewindTargetStageId);

        // Injects the conflict deterministically: the very next AppendTransitionAsync call this
        // FlakySprintStore-backed coordinator makes fails with a conflict, standing in for a real
        // concurrent mutation landing in step 6's own unlocked window between its read and its append
        // -- no real concurrency needed, so no flakiness.
        FlakySprintStore flakyStore = new(store);
        flakyStore.FailAt[flakyStore.AppendCount + 1] = AppendOutcome.Conflict;
        StageTransitionCoordinator flakyCoordinator = new(
            flakyStore,
            environment.Resolve<SprintScheduler>(),
            environment.Resolve<StageTransitionAssessor>(),
            environment.Resolve<StopOperationCoordinator>(),
            environment.Resolve<ActiveOperationRegistry>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment.Resolve<IClock>());

        MoveStageResult conflicted = await flakyCoordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", 0, null, null, false, Guid.NewGuid(), cancellationToken);

        Assert.False(conflicted.Succeeded);
        Assert.Equal(DiagnosticCodes.StageTransitionRewindInProgress, conflicted.DiagnosticCode);
        // PR #101 review finding 4: this rejection must honor MoveStageResult's own documented
        // contract ("on rejection they are null and no durable state changed -- fail closed, no
        // partial transition") exactly like every other rejection in this class, not carry non-null
        // post-commit snapshots alongside `Succeeded: false`.
        Assert.Null(conflicted.Sprint);
        Assert.Null(conflicted.TargetNode);
        SprintWorkflowState afterConflict =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // The saga must not be sealed as converged: the resume marker stays set, and the sprint is
        // still mid-walk (not yet `ready`), never silently advanced past the conflict.
        Assert.Equal("a", afterConflict.Sprint.PendingRewindTargetStageId);
        Assert.NotEqual(SprintState.Ready, afterConflict.Sprint.State);
        Assert.Null(await store.TryGetConvergedStageTransitionAsync(
            environment.ProjectRoot, sprintId, idempotencyKey, cancellationToken));

        // The very next MoveAsync call (through the normal, unconflicted resume path) must finish
        // the ready-walk the conflict interrupted and only then mark the saga converged.
        MoveStageResult resumed = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "a", 0, null, null, false, Guid.NewGuid(), cancellationToken);

        Assert.True(resumed.Succeeded, $"diag={resumed.DiagnosticCode}");
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Ready, final.Sprint.State);
        Assert.Null(final.Sprint.PendingRewindTargetStageId);
        Assert.NotNull(await store.TryGetConvergedStageTransitionAsync(
            environment.ProjectRoot, sprintId, idempotencyKey, cancellationToken));
    }

    /// <summary>PR #101 review finding 3 (critical): before this fix, a Desktop user had no way to
    /// ever resume a rewind a genuine conflict left mid-walk -- <c>AvailableActionProjector</c> offered
    /// no stage-move row at all while <c>PendingRewindTargetStageId</c> was set, and even if one had
    /// been offered, <c>WorkspaceShellPage.SprintWorkspace</c>'s own <c>MoveToStageAsync</c> returned
    /// early on <c>!assessment.Allowed</c> (unconditionally true for this diagnostic) before ever
    /// calling <c>MoveSprintToStageAsync</c>. Only the CLI recovered, because
    /// <c>CliApplication</c>'s `move-stage` command ignores <c>Allowed</c> for this call entirely. This
    /// test proves the fix end to end at the level Desktop's own <c>SprintActionsViewModel.MoveAsync</c>
    /// exercises: the action list now offers exactly one, ENABLED row while the rewind is pending, and
    /// invoking the mutation with exactly that row's own reported version/token/idempotency key --
    /// never a value Desktop invented -- resumes and fully converges the interrupted rewind.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnconvergedRewindOffersASingleEnabledResumeActionThatActuallyFinishesIt()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator, _) =
            Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId =
            await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "b", cancellationToken);

        // Step 2 only (the durable revision record that sets PendingRewindTargetStageId) -- steps 3-6
        // deliberately never run, reproducing the exact "interrupted mid-rewind" shape a genuine
        // conflict (this PR's own bug 2 scenario) or a Host crash leaves behind.
        Guid idempotencyKey = Guid.NewGuid();
        SprintWorkflowState beforeRewind =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AppendOutcome step2 = await store.AppendStageRevisionRecordedAsync(
            environment.ProjectRoot, sprintId, "a", "redo from the start", new StageRevision(1),
            beforeRewind.Sprint.Version, idempotencyKey, cancellationToken);
        Assert.True(step2.Succeeded);

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        AvailableAction resume = Assert.Single(
            actions, action => action.ActionId.StartsWith(
                AvailableActionProjector.MoveToStageActionPrefix, StringComparison.Ordinal));
        Assert.True(resume.Enabled);
        Assert.Empty(resume.Blockers);
        Assert.Equal(
            Forge.Localization.MessageKeys.WorkspaceActionResumeRewindRationale, resume.RationaleKey);

        // Desktop's own MoveAsync forwards exactly these three fields from the action it rendered --
        // never a caller-supplied target/version/token of its own (plan 12.5's "re-fetch/re-validate
        // before committing").
        MoveStageResult resumed = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, resume.Target.StageId!, resume.ExpectedStateVersion, null, null,
            confirmed: false, resume.IdempotencyKey, cancellationToken);

        Assert.True(resumed.Succeeded, $"diag={resumed.DiagnosticCode}");
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Null(final.Sprint.PendingRewindTargetStageId);
        Assert.NotNull(await store.TryGetConvergedStageTransitionAsync(
            environment.ProjectRoot, sprintId, idempotencyKey, cancellationToken));
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

    /// <summary>Plan section 8.5's "a Host crash during a move resumes or converges to one valid
    /// revision" guarantee, exercised for the Advance path specifically (plan ~617-621): unlike
    /// <c>CommitRewindAsync</c>, <c>CommitAdvanceAsync</c> has no durable "pending" marker -- it
    /// relies on <c>SkipNodeAsync</c>'s own idempotent, version-gated re-check
    /// (<c>node.State is not (Pending or Ready) =&gt; continue</c>) to make a resumed skip loop safely
    /// pick up wherever an interrupted one left off. Simulates the crash by performing exactly the
    /// loop's first iteration directly through <see cref="SprintScheduler.SkipNodeAsync"/> (skipping
    /// "b1") and stopping there -- precisely what a Host crash right after that call, before the loop
    /// reaches "b2", would leave behind. A fresh <c>MoveAsync</c> call (a new idempotency key, exactly
    /// like a restarted client would use) must finish skipping "b2" and activate the target, without
    /// re-attempting "b1" (already <c>Skipped</c>, so the loop's own state re-check must pass over
    /// it).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashMidTheOptionalPredecessorSkipLoopConvergesTheRemainingSkipsOnResumeForAnAdvance()
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
                    new("b1", NodeKind.Work, ["a"], Optional: true),
                    new("b2", NodeKind.Work, ["a"], Optional: true),
                    new("c", NodeKind.Work, ["b1", "b2"]),
                ]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        // Simulates the crash: exactly CommitAdvanceAsync's loop, first iteration only ("b1"),
        // stopping before "b2" is ever reached.
        long b1Version = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["b1"].Version;
        NodeActionResult preSkip = await scheduler.SkipNodeAsync(
            environment.ProjectRoot, sprintId, "b1", b1Version, cancellationToken);
        Assert.True(preSkip.Succeeded);
        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Skipped, wedged.Nodes["b1"].State);
        Assert.Equal(NodeState.Ready, wedged.Nodes["b2"].State);
        Assert.Equal(NodeState.Pending, wedged.Nodes["c"].State);
        long b1VersionAfterSkip = wedged.Nodes["b1"].Version;

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "c", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, assessment.Direction);
        Assert.True(assessment.Allowed);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "c", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, Guid.NewGuid(), cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Skipped, final.Nodes["b1"].State);
        // "b1" must never be re-attempted by the resumed loop -- its version is unchanged from right
        // after the original (simulated pre-crash) skip.
        Assert.Equal(b1VersionAfterSkip, final.Nodes["b1"].Version);
        Assert.Equal(NodeState.Skipped, final.Nodes["b2"].State);
        Assert.Equal(NodeState.Ready, final.Nodes["c"].State);
    }

    /// <summary>The other meaningful Advance-path crash boundary (plan ~617-621): a Host crash after
    /// the target has already been promoted to <c>ready</c> (<c>CommitAdvanceAsync</c>'s own
    /// <see cref="SprintScheduler.AdvanceGraphAsync"/> call already ran) but before the durable
    /// convergence marker (<see cref="ISprintStore.AppendStageTransitionConvergedAsync"/>) landed --
    /// the exact window round 1 review of PR #96 (finding 4) found missing for this same call.
    /// <para>Round 2 review of PR #109 found the prior version of this test a proven no-op: since
    /// <c>CompleteAttemptAsync</c> already calls <c>AdvanceGraphAsync</c> itself
    /// (<c>SprintScheduler.cs:583</c>), "b" was already <c>Ready</c> by the time the prior test's own
    /// direct <c>AdvanceGraphAsync</c> call ran, and that call changed nothing observable (node
    /// version and journal sequence unchanged) -- it reduced to
    /// <see cref="AdvanceDoesNotRequireConfirmationMatchingItsOwnAssessment"/> plus two already-true
    /// assertions.</para>
    /// <para>This version constructs a genuine crash instead of asserting around one: a
    /// <see cref="FlakySprintStore"/>-backed coordinator runs the real, complete
    /// <c>CommitAdvanceAsync</c> saga -- predecessor skip loop (a no-op here), the real
    /// <c>AdvanceGraphAsync</c> promotion, then the marker append -- and a hook on that last append
    /// throws, standing in for a Host process dying at exactly that instant, after every prior step's
    /// own durable mutation already landed but before this one did. The hook's own invocation is
    /// asserted directly (proving this call really reached and executed that step, not skipped or
    /// no-op'd it), the state right after is asserted unmarked (no convergence recorded for this
    /// attempt's key), and only then does a fresh <c>MoveAsync</c> call (a new idempotency key,
    /// exactly like a restarted client would use) resume: it must still recognize the target as
    /// reachable (direction re-derives as <c>Advance</c> again, since a merely-<c>Ready</c> target is
    /// never mistaken for the sprint's own "current" stage) and durably record convergence, without
    /// disturbing the already-<c>Ready</c> target a second time (its node version stays exactly what
    /// it was right after the crashed attempt).</para></summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACrashAfterAdvanceGraphPromotesTheTargetButBeforeTheConvergenceMarkerConvergesOnResumeForAnAdvance()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, StageTransitionCoordinator coordinator,
                StageTransitionAssessor assessor) = Resolve(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId sprintId = await CreateLinearSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken, "a", "b");
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);

        SprintWorkflowState beforeCrash = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, beforeCrash.Nodes["b"].State);
        long bVersionBeforeCrash = beforeCrash.Nodes["b"].Version;

        StageTransitionAssessment firstAssessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, firstAssessment.Direction);
        Assert.True(firstAssessment.Allowed);

        // Injects the crash deterministically: the very next AppendStageTransitionConvergedAsync
        // call this FlakySprintStore-backed coordinator makes throws instead of landing -- standing
        // in for a Host process dying at exactly that instant, after CommitAdvanceAsync's own
        // AdvanceGraphAsync promotion has already committed for real (production code, not
        // simulated) but before its final marker append does.
        FlakySprintStore flakyStore = new(store);
        bool hookInvoked = false;
        flakyStore.BeforeAppendStageTransitionConverged = _ =>
        {
            hookInvoked = true;
            throw new InvalidOperationException("simulated Host crash before the convergence marker lands");
        };
        StageTransitionCoordinator crashingCoordinator = new(
            flakyStore,
            environment.Resolve<SprintScheduler>(),
            environment.Resolve<StageTransitionAssessor>(),
            environment.Resolve<StopOperationCoordinator>(),
            environment.Resolve<ActiveOperationRegistry>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment.Resolve<IClock>());

        Guid crashedIdempotencyKey = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashingCoordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", firstAssessment.ExpectedStateVersion,
            firstAssessment.AssessmentToken, null, true, crashedIdempotencyKey, cancellationToken));
        Assert.True(hookInvoked, "The crash hook must actually have been reached for this to prove anything.");

        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, wedged.Nodes["b"].State);
        Assert.Equal(bVersionBeforeCrash, wedged.Nodes["b"].Version);
        Assert.Null(await store.TryGetConvergedStageTransitionAsync(
            environment.ProjectRoot, sprintId, crashedIdempotencyKey, cancellationToken));

        // The very next MoveAsync call (through the normal, unconflicted resume path, a fresh
        // idempotency key exactly like a restarted client would use) must finish what the crashed
        // attempt could not: recognize "b" as already reachable, not re-promote it, and durably
        // record convergence.
        Guid resumedIdempotencyKey = Guid.NewGuid();
        StageTransitionAssessment resumedAssessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "b", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, resumedAssessment.Direction);
        Assert.True(resumedAssessment.Allowed);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "b", resumedAssessment.ExpectedStateVersion,
            resumedAssessment.AssessmentToken, null, true, resumedIdempotencyKey, cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, result.TargetNode!.State);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, final.Nodes["b"].State);
        // "b" must never be re-promoted by the resumed call -- its version is unchanged from right
        // after the crashed attempt.
        Assert.Equal(bVersionBeforeCrash, final.Nodes["b"].Version);
        Assert.NotNull(await store.TryGetConvergedStageTransitionAsync(
            environment.ProjectRoot, sprintId, resumedIdempotencyKey, cancellationToken));
    }

    /// <summary>Plan ~605-609: <c>StagePrerequisiteIds.NoActiveOperation</c> is already checked inside
    /// <c>StageTransitionAssessor</c>'s Advance-direction branch, but until this test only Rewind's
    /// own stop-path (<see cref="RewindStopsTheActiveOperationFirstBeforeInvalidatingItsNode"/>)
    /// exercised an active-operation interaction -- no test asserted an Advance is actually rejected
    /// while an unrelated operation is running. "b1" is optional and deliberately left unstarted (the
    /// same skip-ahead shape as <see cref="AdvanceSkipAheadActivatesTargetWhenEveryIntermediateStageIsAlreadySatisfied"/>)
    /// so "c" stays genuinely `pending` -- <see cref="SprintScheduler.AdvanceGraphAsync"/>'s own
    /// automatic promotion never touches it on its own, and no prior test can be mistaken for this
    /// one by relying on that automatic promotion instead of the coordinator's own gate. The active
    /// attempt runs on "b2", a parallel branch that is NOT among "c"'s own predecessors, so every
    /// predecessor-based prerequisite (<c>PredecessorSuccess</c> et al., scoped only to the *required*
    /// -- non-optional -- predecessor "a") is already satisfied and <c>NoActiveOperation</c> is the
    /// only thing standing between this assessment and <c>Allowed</c> --
    /// <see cref="ActiveOperationImpact.HasActiveOperation"/> is sprint-wide, not scoped to the
    /// target's own predecessors (<c>StageTransitionAssessor.ResolveActiveOperation</c> scans every
    /// node).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnActiveOperationOnAnUnrelatedNodeBlocksAdvance()
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
                    new("b1", NodeKind.Work, ["a"], Optional: true),
                    new("c", NodeKind.Work, ["b1"]),
                    new("b2", NodeKind.Work, ["a"]),
                ]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        // "b1" is optional and deliberately left unstarted -- "c" therefore stays `pending` on its
        // own (AdvanceGraphAsync never promotes it without "b1" settled), so the assertion below that
        // it is still `pending` after the rejected move actually proves something.

        long b2Version = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["b2"].Version;
        StartAttemptResult startedB2 = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "b2", b2Version, cancellationToken);
        Assert.True(startedB2.Succeeded);

        StageTransitionAssessment assessment =
            await assessor.AssessAsync(environment.ProjectRoot, sprintId, "c", cancellationToken);
        Assert.Equal(StageTransitionDirection.Advance, assessment.Direction);
        Assert.True(assessment.ActiveOperation.HasActiveOperation);
        Assert.False(assessment.Allowed);
        // Round 2 review of PR #109: `Assert.Contains(..., NoActiveOperation)` alone does not pin
        // this test's own claim ("NoActiveOperation is the only thing standing between this
        // assessment and Allowed") -- `StageTransitionAssessor` adds several more Advance-only
        // prerequisites unconditionally (NoBlockingFindings, ProviderModelPolicy, GitIsolation,
        // RetryBudget, plus conditional ones), any of which could independently be unsatisfied while
        // this assertion still passes. Pinning the unsatisfied set to exactly one entry, and that one
        // entry's id specifically, makes the doc comment's claim actually load-bearing.
        StagePrerequisite onlyUnsatisfied = Assert.Single(assessment.UnsatisfiedPrerequisites);
        Assert.Equal(StagePrerequisiteIds.NoActiveOperation, onlyUnsatisfied.Id);
        // Every predecessor-based prerequisite is already satisfied -- NoActiveOperation is the only
        // reason this Advance is blocked.
        Assert.DoesNotContain(
            assessment.UnsatisfiedPrerequisites,
            prerequisite => prerequisite.Id == StagePrerequisiteIds.PredecessorSuccess);

        MoveStageResult result = await coordinator.MoveAsync(
            environment.ProjectRoot, sprintId, "c", assessment.ExpectedStateVersion, assessment.AssessmentToken,
            null, true, Guid.NewGuid(), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowBlocked, result.DiagnosticCode);
        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Pending, final.Nodes["c"].State);
        Assert.Equal(NodeState.Running, final.Nodes["b2"].State);
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
