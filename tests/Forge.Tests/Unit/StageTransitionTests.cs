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
            new Dictionary<string, string?>(), ["evidence"], null, cancellationToken);
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
