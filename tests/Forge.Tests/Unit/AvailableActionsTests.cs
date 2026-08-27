using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 6.4's versioned contextual-action projection, computed from a sprint's
/// actual current state rather than a parallel policy engine. Confirms real behavior for both the
/// project-level (no sprint selected) and sprint-scoped shapes before the smallest risk-based tests
/// were added.</summary>
public sealed class AvailableActionsTests
{
    private static readonly IReadOnlyList<NodeDefinition> TwoNodeGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUninitializedProjectOffersInitializeAsAProjectLevelAction()
    {
        using TestEnvironment environment = new();

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, null, TestContext.Current.CancellationToken);

        AvailableAction initialize =
            Assert.Single(actions, action => action.ActionId == ForgeApplication.InitializeProjectAction);
        Assert.Equal(environment.ProjectRoot, initialize.Target.ProjectRoot);
        Assert.True(initialize.Enabled);
        Assert.Empty(initialize.Blockers);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADraftSprintOffersRunButNeitherResumeNorStop()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        Assert.Contains(actions, action => action.ActionId == AvailableActionProjector.RunSprintActionId);
        Assert.DoesNotContain(actions, action => action.ActionId == AvailableActionProjector.ResumeSprintActionId);
        Assert.DoesNotContain(
            actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
        Assert.Contains(actions, action => action.ActionId == AvailableActionProjector.CancelSprintActionId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARunningSprintWithAnActiveAttemptOffersStopTargetingTheExactAttempt()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        AvailableAction stop =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
        Assert.Equal(sprintId.Value, stop.Target.SprintId);
        Assert.Equal("a", stop.Target.NodeId);
        Assert.Equal(started.AttemptId!.Value, stop.Target.AttemptId);
        Assert.Equal(SafetyClass.HumanApproval, stop.SafetyClass);
        Assert.True(stop.ConfirmationRequired);

        // "b" depends on "a" (not yet succeeded), so moving straight to "b" must report a blocker,
        // never silently allow skipping ahead (plan section 8.3: "never fabricates completion").
        AvailableAction moveToB = Assert.Single(
            actions, action => action.ActionId == $"{AvailableActionProjector.MoveToStageActionPrefix}b");
        Assert.False(moveToB.Enabled);
        Assert.NotEmpty(moveToB.Blockers);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFailedSprintOffersResumeAndNoActiveOperation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        string sampleDigest = "sha256:" + new string('0', 64);
        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", nodeVersion, cancellationToken);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, sampleDigest, [], [],
                cancellationToken);
            nodeVersion = completed.Node!.Version;
        }

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        Assert.Contains(actions, action => action.ActionId == AvailableActionProjector.ResumeSprintActionId);
        Assert.DoesNotContain(
            actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
    }

    // Round-trip regression (PR #97 review): the whole point of ExpectedStateVersion/IdempotencyKey
    // on an AvailableAction is that a client can feed them straight back into the exact mutation the
    // action describes and have it succeed. Earlier, run/resume/cancel_sprint reported
    // SprintWorkflowState.LastSequence (the journal position) while SprintOrchestrator.TransitionAsync
    // validates freshness against SprintSnapshot.Version (the sprint aggregate's own transition
    // count) -- coincidentally equal only for a freshly created sprint, diverging as soon as any
    // non-sprint-aggregate event (a node/attempt transition) is appended. These tests assert against
    // the real mutation, not just a plausible-looking version number, so they would have caught it.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARunSprintActionsReportedVersionAndKeySucceedAgainstTheRealMutation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction run =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.RunSprintActionId);

        SprintTransitionResult result = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, run.ExpectedStateVersion, run.IdempotencyKey),
            cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACancelSprintActionsReportedVersionAndKeySucceedAgainstTheRealMutation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        // Advances SprintSnapshot.Version away from LastSequence by appending a non-sprint-aggregate
        // (node) event, reproducing the exact divergence the reviewer's probe found.
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction cancel =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.CancelSprintActionId);

        SprintTransitionResult result = await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, cancel.ExpectedStateVersion, cancel.IdempotencyKey),
            cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AResumeSprintActionsReportedVersionAndKeySucceedAgainstTheRealMutation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        string sampleDigest = "sha256:" + new string('0', 64);
        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", nodeVersion, cancellationToken);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, sampleDigest, [], [],
                cancellationToken);
            nodeVersion = completed.Node!.Version;
        }

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction resume =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.ResumeSprintActionId);

        SprintTransitionResult result = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, resume.ExpectedStateVersion, resume.IdempotencyKey),
            cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    // ADR 0058: the gate decision is a Host-described action linked to the event that requested it,
    // not a boolean a client scrapes off node state. Two gates, because the action id is node-scoped
    // precisely so several pending gates cannot collapse into one ambiguous `approve_gate` row; and a
    // user message posted afterwards, so the sprint's own LastSequence is a *different*, unrelated
    // event -- an implementation that reported it instead of each gate's own transition would still
    // produce a plausible-looking number, and only this shape catches that.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task EachPendingGateOffersApproveAndRejectLinkedToTheEventThatRequestedIt()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("first", NodeKind.HumanGate, []), new("second", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await store.AppendUserMessageAsync(
            environment.ProjectRoot, sprintId, Guid.NewGuid(), "posted after both gates opened", cancellationToken);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        IReadOnlyList<WorkflowEvent> events =
            await store.GetEventsAsync(environment.ProjectRoot, sprintId, cancellationToken);

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        foreach (string nodeId in (string[])["first", "second"])
        {
            Assert.Equal(NodeState.AwaitingHuman, state.Nodes[nodeId].State);
            foreach (string prefix in (string[])
                [AvailableActionProjector.ApproveGateActionPrefix, AvailableActionProjector.RejectGateActionPrefix])
            {
                AvailableAction gate = Assert.Single(actions, action => action.ActionId == $"{prefix}{nodeId}");
                Assert.Equal(sprintId.Value, gate.Target.SprintId);
                Assert.Equal(nodeId, gate.Target.NodeId);
                Assert.Equal(SafetyClass.HumanApproval, gate.SafetyClass);
                Assert.True(gate.ConfirmationRequired);
                Assert.True(gate.Enabled);

                // The linked event must be this node's own transition into `awaiting_human` -- checked
                // by resolving the sequence back to the real journal entry rather than recomputing the
                // projector's own lookup, so a wrong-but-plausible sequence (the sprint's LastSequence,
                // another gate's transition, the same node's earlier `running` step) fails here.
                long linked = Assert.NotNull(gate.Target.TimelineSequence);
                Assert.NotEqual(state.LastSequence, linked);
                WorkflowEvent requesting = Assert.Single(events, item => item.Sequence == linked);
                Assert.Equal(AggregateKind.Node, requesting.Aggregate.Kind);
                Assert.Equal(nodeId, requesting.Aggregate.Id);
                Assert.Equal(
                    WorkflowStateNames.ToSnakeCase(NodeState.AwaitingHuman),
                    requesting.Arguments[WorkflowEvent.ToStateArgument]);
            }
        }
    }

    /// <summary>The same round-trip guarantee the sprint-lifecycle rows above are pinned to, for the
    /// gate pair: what the action reports is enough to execute the mutation it describes. The second
    /// half is the state-versus-history check -- the resolved gate's `awaiting_human` transition is
    /// still in the journal forever, so a projector keyed on the event rather than on current node
    /// state would keep offering a decision that has already been made.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AGateActionsReportedNodeResolvesTheRealGateAndTheRowsThenDisappear()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction approve = Assert.Single(
            actions,
            action => action.ActionId == $"{AvailableActionProjector.ApproveGateActionPrefix}gate");

        NodeActionResult resolved = await environment.Application.ResolveGateAsync(
            environment.ProjectRoot, sprintId.Value, approve.Target.NodeId!, true, true, cancellationToken);

        Assert.True(resolved.Succeeded);
        Assert.Equal(DiagnosticCodes.None, resolved.DiagnosticCode);
        IReadOnlyList<AvailableAction> after = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        Assert.DoesNotContain(
            after,
            action => action.ActionId.StartsWith(AvailableActionProjector.ApproveGateActionPrefix, StringComparison.Ordinal) ||
                action.ActionId.StartsWith(AvailableActionProjector.RejectGateActionPrefix, StringComparison.Ordinal));
    }

    /// <summary>ADR 0058 links a gate row to the node's *most recent* transition into
    /// `awaiting_human`, never its first. A rejected gate is retried back into a second decision by an
    /// already-supported path (`SprintSchedulerTests
    /// .ApprovingARetriedGateAfterAnEarlierRejectionRecordsAFreshApproval`), leaving the first round's
    /// request in the journal forever; a projector keyed on the first match would still report a real,
    /// plausible-looking sequence -- the stale one -- and anchor the pending decision to the request
    /// that has already been answered.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARetriedGateLinksToItsSecondAwaitingHumanTransitionNotItsFirst()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        IReadOnlyList<AvailableAction> firstRound = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        long firstLinked = Assert.NotNull(
            Assert.Single(
                    firstRound,
                    action => action.ActionId == $"{AvailableActionProjector.ApproveGateActionPrefix}gate")
                .Target.TimelineSequence);

        // Reject, retry the failed node, then resume and re-run the blocked sprint -- the one path that
        // re-arms the same gate for a second decision, so the node reaches `awaiting_human` twice.
        Assert.True((await environment.Application.ResolveGateAsync(
            environment.ProjectRoot, sprintId.Value, "gate", false, true, cancellationToken)).Succeeded);
        NodeSnapshot failed =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        Assert.True((await scheduler.RetryNodeAsync(
            environment.ProjectRoot, sprintId, "gate", failed.Version,
            SprintScheduler.RetryNodeKey(sprintId, failed), cancellationToken)).Succeeded);
        SprintSnapshot blocked =
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult resumed = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, blocked.Version, SprintOrchestrator.ResumeSprintKey(blocked)),
            cancellationToken);
        Assert.True(resumed.Succeeded);
        Assert.True((await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, resumed.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(resumed.Sprint)),
            cancellationToken)).Succeeded);

        IReadOnlyList<AvailableAction> secondRound = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        // Both requests are resolved from the real journal, so the expectation is derived from the
        // recorded history rather than from the projector's own lookup.
        IReadOnlyList<WorkflowEvent> events =
            await store.GetEventsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        long[] requests =
        [
            .. events
                .Where(item => item.Aggregate.Kind == AggregateKind.Node &&
                    string.Equals(item.Aggregate.Id, "gate", StringComparison.Ordinal) &&
                    item.Arguments.TryGetValue(WorkflowEvent.ToStateArgument, out string? toState) &&
                    string.Equals(
                        toState,
                        WorkflowStateNames.ToSnakeCase(NodeState.AwaitingHuman),
                        StringComparison.Ordinal))
                .Select(item => item.Sequence)
                .OrderBy(sequence => sequence),
        ];
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0], firstLinked);
        Assert.Equal(
            NodeState.AwaitingHuman,
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"].State);
        foreach (string prefix in (string[])
            [AvailableActionProjector.ApproveGateActionPrefix, AvailableActionProjector.RejectGateActionPrefix])
        {
            AvailableAction gate = Assert.Single(secondRound, action => action.ActionId == $"{prefix}gate");
            Assert.Equal(requests[1], gate.Target.TimelineSequence);
        }
    }

    private static async Task RunToRunningAsync(
        SprintOrchestrator orchestrator,
        string root,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(root, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(root, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(root, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
    }
}
