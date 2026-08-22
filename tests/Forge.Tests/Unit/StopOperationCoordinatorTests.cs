using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 7.3 / ADR 0047: <see cref="StopOperationCoordinator"/>'s own rejection
/// reasons, idempotent replay, live-registry cancellation, and convergence steps.</summary>
public sealed class StopOperationCoordinatorTests
{
    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    private static readonly IReadOnlyList<NodeDefinition> SingleNodeGraph = [new("a", NodeKind.Work, [])];

    private static readonly IReadOnlyList<NodeDefinition> TwoIndependentNodesGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestStopAsyncRejectsWhenTheSprintHasNoActiveOperation()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, SprintOrchestrator orchestrator, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintSnapshot sprint = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, sprint.Version, SprintOrchestrator.CancelSprintKey(sprint)),
            cancellationToken);

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();
        StopOperationResult result = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, attempt.Id, registry, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NoActiveOperation, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestStopAsyncRejectsAnAlreadySettledAttempt()
    {
        using TestEnvironment environment = await InitializedAsync();
        // Two independent nodes so the sprint stays Running once "a" settles -- otherwise the
        // sprint itself would leave Running first and mask this case behind NoActiveOperation.
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, TwoIndependentNodesGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", attempt.Id, true, SampleDigest, [], [], cancellationToken);

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();
        StopOperationResult result = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, attempt.Id, registry, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.AttemptTerminal, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestStopAsyncRejectsAnUnknownAttempt()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler _, ISprintStore _, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot _) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();
        StopOperationResult result = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, AttemptId.New(), registry, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, result.DiagnosticCode);
    }

    /// <summary>A non-terminal attempt (a supersession's fresh, still-`created` replacement) that is
    /// not yet the node's own current attempt must be rejected as a changed active operation, not
    /// silently accepted -- plan section 12.4's "a stale stop cannot cancel an attempt that started
    /// after the targeted one" cuts both ways: it also must not stop an attempt that never became
    /// current at all.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestStopAsyncRejectsAnAttemptThatIsNotTheNodesCurrentActiveOperation()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), confirmed: true, "Try a different approach.",
            cancellationToken);
        SprintWorkflowState afterSupersede =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot replacement =
            Assert.Single(afterSupersede.Attempts.Values, candidate => candidate.Id != attempt.Id);
        Assert.Equal(NodeState.Ready, afterSupersede.Nodes["a"].State);

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();
        StopOperationResult result = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, replacement.Id, registry, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ActiveOperationChanged, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestStopAsyncIsIdempotentOnReplay()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler _, ISprintStore store, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();

        StopOperationResult first = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, attempt.Id, registry, cancellationToken);
        Assert.True(first.Succeeded);
        DateTimeOffset? firstRequestedAt = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Attempts[attempt.Id.Value.ToString("D")].StopRequestedAt;
        Assert.NotNull(firstRequestedAt);

        StopOperationResult second = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, attempt.Id, registry, cancellationToken);

        Assert.True(second.Succeeded);
        DateTimeOffset? secondRequestedAt = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Attempts[attempt.Id.Value.ToString("D")].StopRequestedAt;
        // A replay never re-records the intent -- the timestamp is whichever call actually won.
        Assert.Equal(firstRequestedAt, secondRequestedAt);
        IReadOnlyList<WorkflowEvent> events =
            await store.GetEventsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Single(events, item => item.Type == WorkflowEvent.AttemptStopRequestedType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestStopAsyncCancelsTheRegisteredToken()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler _, ISprintStore _, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();
        CancellationTokenSource operation = registry.Register(attempt.Id, CancellationToken.None);
        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();

        StopOperationResult result = await coordinator.RequestStopAsync(
            environment.ProjectRoot, sprintId, attempt.Id, registry, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(operation.Token.IsCancellationRequested);
        registry.Unregister(attempt.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinishStopAsyncCancelsARunningAttemptDiscardsTheWorktreeRearmsTheNodeAndPausesTheSprint()
    {
        FakeWorktreeManager worktrees = new();
        using TestEnvironment environment = new(worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken)).Succeeded);
        (SprintScheduler _, ISprintStore store, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintGitIsolation gitIsolation = environment.Resolve<SprintGitIsolation>();
        Guid projectId = Guid.NewGuid();
        GitOperationResult integration = await gitIsolation.EnsureIntegrationWorktreeAsync(
            environment.ProjectRoot, projectId, sprintId, new string('a', 40), cancellationToken);
        Assert.True(integration.Succeeded);
        GitOperationResult attemptWorktree = await gitIsolation.CreateAttemptWorktreeAsync(
            environment.ProjectRoot, projectId, sprintId, attempt.Id, cancellationToken);
        Assert.True(attemptWorktree.Succeeded);
        await store.AppendAttemptStopRequestedAsync(environment.ProjectRoot, sprintId, attempt.Id, cancellationToken);

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        await coordinator.FinishStopAsync(
            environment.ProjectRoot, sprintId, projectId, "a", attempt.Id, cancellationToken);

        SprintWorkflowState converged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Cancelled, converged.Attempts[attempt.Id.Value.ToString("D")].State);
        Assert.Equal(NodeState.Ready, converged.Nodes["a"].State);
        Assert.Equal(SprintState.Paused, converged.Sprint.State);
        // The re-arm never touched AttemptCount -- still exactly the one attempt this test started.
        Assert.Equal(1, converged.Nodes["a"].AttemptCount);
        Assert.NotEmpty(worktrees.RemovedPaths);
    }

    /// <summary>ADR 0044's sanctioned use of `Validating -> Cancelled`: a stop request submitted
    /// while the attempt is validating its own outcome must still converge.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinishStopAsyncCancelsAValidatingAttempt()
    {
        FakeWorktreeManager worktrees = new();
        using TestEnvironment environment = new(worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken)).Succeeded);
        (SprintScheduler _, ISprintStore store, SprintId sprintId, SprintOrchestrator _, AttemptSnapshot attempt) =
            await StartAttemptAsync(environment, SingleNodeGraph, "a");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AttemptSnapshot validating =
            await DriveAttemptToValidatingAsync(store, environment.ProjectRoot, sprintId, attempt, cancellationToken);

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        await coordinator.FinishStopAsync(
            environment.ProjectRoot, sprintId, Guid.NewGuid(), "a", validating.Id, cancellationToken);

        SprintWorkflowState converged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Cancelled, converged.Attempts[attempt.Id.Value.ToString("D")].State);
        Assert.Equal(NodeState.Ready, converged.Nodes["a"].State);
        Assert.Equal(SprintState.Paused, converged.Sprint.State);
    }

    /// <summary>Proves the stop path bypasses `MaxAutomaticRetries` entirely, not merely by
    /// coincidence: the node's ordinary automatic-retry budget is already exhausted (two failures,
    /// each already retried) before the stop, so an ordinary third failure would leave the node
    /// terminally `Failed` -- yet the stop-based re-arm still succeeds.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinishStopAsyncRearmsTheNodeEvenAfterTheAutomaticRetryBudgetIsExhausted()
    {
        FakeWorktreeManager worktrees = new();
        using TestEnvironment environment = new(worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken)).Succeeded);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: SingleNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        // Exhausts MaxAutomaticRetries (2): two failures, each automatically retried.
        for (int failure = 0; failure < SprintScheduler.MaxAutomaticRetries; failure++)
        {
            SprintWorkflowState beforeStart =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", beforeStart.Nodes["a"].Version, cancellationToken);
            Assert.True(started.Succeeded);
            CompleteAttemptResult failed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, SampleDigest, [], [],
                cancellationToken);
            Assert.True(failed.Succeeded);
            Assert.Equal(NodeState.Ready, failed.Node!.State);
        }

        // The third attempt: an ordinary failure here would leave the node terminally Failed.
        SprintWorkflowState beforeThird =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult third = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", beforeThird.Nodes["a"].Version, cancellationToken);
        Assert.True(third.Succeeded);
        Assert.Equal(3, (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["a"].AttemptCount);

        StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
        await coordinator.FinishStopAsync(
            environment.ProjectRoot, sprintId, Guid.NewGuid(), "a", third.AttemptId!, cancellationToken);

        SprintWorkflowState converged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // Re-armed despite the exhausted budget -- the stop path never consulted it.
        Assert.Equal(NodeState.Ready, converged.Nodes["a"].State);
        Assert.Equal(SprintState.Paused, converged.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinishStopAsyncResumesAfterACrashBetweenTheAttemptCancelAndTheNodeReArm()
    {
        // A fake worktree manager, matching every other worktree-touching test in this file: real
        // `git.exe` against a `FlakySprintStore`-backed sprint would exercise git failure handling
        // this test has nothing to do with.
        using TestEnvironment environment = new(worktrees: new FakeWorktreeManager());
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken)).Succeeded);
        (SprintOrchestrator orchestrator, SprintScheduler _, FlakySprintStore flakyStore) =
            environment.ResolveWithFlakyStore();
        StopOperationCoordinator coordinator = new(flakyStore, environment.Resolve<SprintGitIsolation>());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: SingleNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        SprintScheduler realScheduler = environment.Resolve<SprintScheduler>();
        SprintWorkflowState running =
            (await flakyStore.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await realScheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", running.Nodes["a"].Version, cancellationToken);
        Assert.True(started.Succeeded);

        // Fails the very next AppendTransitionAsync call -- FinishStopAsync's own first append (the
        // attempt's Running -> Cancelled transition) -- simulating a crash before anything durable
        // from this call landed at all. FinishStopAsync never throws on a conflicting append; it
        // simply returns without advancing that step, so this call is the "interrupted" one.
        flakyStore.FailAt[flakyStore.AppendCount + 1] = AppendOutcome.Conflict;
        Guid projectId = Guid.NewGuid();
        await coordinator.FinishStopAsync(
            environment.ProjectRoot, sprintId, projectId, "a", started.AttemptId!, cancellationToken);
        SprintWorkflowState afterInterrupted =
            (await flakyStore.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // StartAttemptAsync itself never walks the attempt past `created` -- only a real completion
        // does -- so the node being Running, not the attempt's own state, is what "interrupted"
        // means to prove here.
        Assert.Equal(AttemptState.Created, afterInterrupted.Attempts[started.AttemptId!.Value.ToString("D")].State);
        Assert.Equal(NodeState.Running, afterInterrupted.Nodes["a"].State);

        await coordinator.FinishStopAsync(
            environment.ProjectRoot, sprintId, projectId, "a", started.AttemptId!, cancellationToken);

        SprintWorkflowState converged =
            (await flakyStore.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Cancelled, converged.Attempts[started.AttemptId!.Value.ToString("D")].State);
        Assert.Equal(NodeState.Ready, converged.Nodes["a"].State);
        Assert.Equal(SprintState.Paused, converged.Sprint.State);
    }

    private static async Task<(SprintScheduler Scheduler, ISprintStore Store, SprintId SprintId, SprintOrchestrator Orchestrator, AttemptSnapshot Attempt)>
        StartAttemptAsync(TestEnvironment environment, IReadOnlyList<NodeDefinition> graph, string nodeId)
    {
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: graph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, nodeId, state.Nodes[nodeId].Version, cancellationToken);
        Assert.True(started.Succeeded);
        SprintWorkflowState afterStart =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot attempt = afterStart.Attempts[started.AttemptId!.Value.ToString("D")];
        return (scheduler, store, sprintId, orchestrator, attempt);
    }

    /// <summary>Same raw-append walk as `SprintSchedulerRoutingAndSupersessionTests`' own helper --
    /// there is no public scheduler verb that stops at `validating`.</summary>
    private static async Task<AttemptSnapshot> DriveAttemptToValidatingAsync(
        ISprintStore store,
        string projectRoot,
        SprintId sprintId,
        AttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        string attemptKey = attempt.Id.Value.ToString("D");
        long version = attempt.Version;
        foreach (AttemptState toState in
                 (AttemptState[])[AttemptState.Preparing, AttemptState.Running, AttemptState.Validating])
        {
            AppendOutcome outcome = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
                "workflow.attempt_transitioned", WorkflowStateNames.ToSnakeCase(toState), version, Guid.NewGuid(),
                cancellationToken);
            Assert.True(outcome.Succeeded, $"diag={outcome.DiagnosticCode}");
            version = outcome.State!.Attempts[attemptKey].Version;
        }

        return (await store.LoadAsync(projectRoot, sprintId, cancellationToken))!.Attempts[attemptKey];
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

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
