using System.Diagnostics;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host;
using Forge.Providers;
using Forge.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.IntegrationTests;

/// <summary>
/// Plan section 7 / ADR 0044 / ADR 0047: end-to-end confirmation that stopping the sprint's active
/// operation actually cancels the live provider run, discards its worktree, re-arms the owning node
/// without spending automatic retry budget, and pauses the sprint -- driven through
/// <see cref="PlanningExecutionHostedService"/> (the least setup of the three provider-invoking
/// executors) with a real <see cref="FakeRunnableLlmProvider"/> that blocks until its token is
/// cancelled, the same "decouple from real `git.exe`/a real provider process" boundary every other
/// node-executor integration suite in this project already draws.
/// </summary>
public sealed class StopOperationIntegrationTests
{
    private const string IntakeNodeId = ImplementationCriticalGraphBuilder.IntakeNodeId;
    private const string PlanningNodeId = ImplementationCriticalGraphBuilder.PlanningNodeId;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StoppingTheActiveOperationCancelsDiscardsRearmsAndPauses()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        TaskCompletionSource providerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            async (_, _, token, _) =>
            {
                // Simulates a real provider process: it runs until the token it was handed observes
                // cancellation, exactly as ProcessRunner.RunAsync's own Process.Kill(true) reacts to
                // the same token in production.
                providerStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("The provider must observe cancellation before returning.");
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId =
            await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        PlanningExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            SprintWorkflowState running =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            NodeSnapshot planningNode = running.Nodes[PlanningNodeId];
            Assert.Equal(NodeState.Running, planningNode.State);
            Assert.Equal(1, planningNode.AttemptCount);
            AttemptId attemptId = new(Guid.Parse(planningNode.CurrentAttemptId!));

            StopOperationCoordinator coordinator = environment.Resolve<StopOperationCoordinator>();
            ActiveOperationRegistry registry = environment.Resolve<ActiveOperationRegistry>();
            StopOperationResult stopRequested = await coordinator.RequestStopAsync(
                environment.ProjectRoot, sprintId, attemptId, registry, cancellationToken);
            Assert.True(stopRequested.Succeeded, $"diag={stopRequested.DiagnosticCode}");

            await WaitForSprintPausedAsync(store, environment, sprintId, cancellationToken);

            SprintWorkflowState converged =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            Assert.Equal(SprintState.Paused, converged.Sprint.State);
            Assert.Equal(
                AttemptState.Cancelled, converged.Attempts[attemptId.Value.ToString("D")].State);
            // The re-arm never spent the automatic retry budget: still exactly the one attempt that
            // actually started, not counted a second time by this path.
            Assert.Equal(1, converged.Nodes[PlanningNodeId].AttemptCount);
            Assert.Contains(provider.Calls[0].WorkingDirectory, worktrees.RemovedPaths);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }
    }

    /// <summary>Regression coverage for the crash-recovery half of plan section 7.3: a durable stop
    /// intent recorded but never converged (simulating a Host crash between the request and the
    /// executor observing it) must be finished by the very next tick of a freshly constructed
    /// executor instance -- not by resuming the provider.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARestartedExecutorFinishesAStopThatWasDurablyRequestedBeforeTheCrash()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => throw new InvalidOperationException(
                "The provider must not run again once a stop intent is already durable."));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId =
            await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        // Simulates the attempt already being "running" when the Host died (StartAttemptAsync
        // already committed) with a stop intent already durably recorded (RequestStopAsync's own
        // append already committed too) -- but nothing yet converged, exactly the window a Host
        // crash between request and convergence leaves behind.
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, PlanningNodeId, state.Nodes[PlanningNodeId].Version,
            cancellationToken);
        Assert.True(started.Succeeded);
        SprintWorkflowState afterStart =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await store.AppendAttemptStopRequestedAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!,
            afterStart.Attempts[started.AttemptId!.Value.ToString("D")].Version, cancellationToken);

        // A brand-new service instance, matching every other executor's own restart-recovery test
        // shape (e.g. IntakeExecutionHostedServiceTests): nothing in this process ever registered
        // this attempt in ActiveOperationRegistry, proving convergence works purely from durable
        // state, not from anything a live process happened to remember.
        PlanningExecutionHostedService restarted = NewService(environment, store, scheduler);
        await restarted.StartAsync(cancellationToken);
        try
        {
            await WaitForSprintPausedAsync(store, environment, sprintId, cancellationToken);

            SprintWorkflowState converged =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            Assert.Equal(SprintState.Paused, converged.Sprint.State);
            Assert.Equal(
                AttemptState.Cancelled,
                converged.Attempts[started.AttemptId!.Value.ToString("D")].State);
            Assert.Empty(provider.Calls);
        }
        finally
        {
            await restarted.StopAsync(cancellationToken);
        }
    }

    /// <summary>Round 1 review of PR #95 (finding 1): a Host crash between
    /// <see cref="StopOperationCoordinator.FinishStopAsync"/>'s own node-stopped and node-rearmed
    /// appends leaves the node durably `Failed` with the sprint still `Running`. Before the fix,
    /// every executor's own stop-convergence check was gated on `node.State == Running`, so a
    /// `Failed` node was never revisited by anything -- the sprint wedged permanently. The
    /// generalized check (node's own <see cref="NodeSnapshot.CurrentAttemptId"/> plus the attempt's
    /// durable <see cref="AttemptSnapshot.StopRequestedAt"/>/<see cref="AttemptSnapshot.StopConvergedAt"/>,
    /// independent of the node's current state) must converge it on the very next tick instead.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARestartedExecutorFinishesAStopWhoseNodeWasLeftFailedByAnEarlierCrash()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => throw new InvalidOperationException(
                "The provider must not run again once a stop intent is already durable."));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId =
            await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, PlanningNodeId, state.Nodes[PlanningNodeId].Version,
            cancellationToken);
        Assert.True(started.Succeeded);

        await DriveToNodeFailedAfterStopAsync(
            store, environment.ProjectRoot, sprintId, PlanningNodeId, started.AttemptId!, cancellationToken);
        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Failed, wedged.Nodes[PlanningNodeId].State);
        Assert.Equal(SprintState.Running, wedged.Sprint.State);

        PlanningExecutionHostedService restarted = NewService(environment, store, scheduler);
        await restarted.StartAsync(cancellationToken);
        try
        {
            await WaitForSprintPausedAsync(store, environment, sprintId, cancellationToken);

            SprintWorkflowState converged =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            Assert.Equal(SprintState.Paused, converged.Sprint.State);
            Assert.Equal(NodeState.Ready, converged.Nodes[PlanningNodeId].State);
            Assert.Equal(
                AttemptState.Cancelled, converged.Attempts[started.AttemptId!.Value.ToString("D")].State);
            // The re-arm never spent the automatic retry budget -- still exactly the one attempt
            // that actually started.
            Assert.Equal(1, converged.Nodes[PlanningNodeId].AttemptCount);
            Assert.Empty(provider.Calls);
        }
        finally
        {
            await restarted.StopAsync(cancellationToken);
        }
    }

    /// <summary>Round 1 review of PR #95 (finding 2): a Host crash between
    /// <see cref="StopOperationCoordinator.FinishStopAsync"/>'s own node-rearmed and sprint-paused
    /// appends leaves the node durably `Ready` (its <see cref="NodeSnapshot.CurrentAttemptId"/> still
    /// naming the stopped attempt -- the node's own `running`/rearm transitions never clear it) with
    /// the sprint still `Running`. Before the fix, the executor's stop-check required
    /// `node.State == Running`, so a `Ready` node fell straight through to `StartAttemptAsync`,
    /// minting a brand-new attempt and silently spending automatic-retry budget the stop was meant
    /// to preserve. The generalized check must recognize the still-unconverged stop from the
    /// attempt's own durable state and finish it instead of starting fresh work.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARestartedExecutorFinishesAStopWhoseNodeWasAlreadyRearmedWithoutStartingAFreshAttempt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => throw new InvalidOperationException(
                "The provider must not run again once a stop intent is already durable -- a fresh " +
                    "attempt here would prove the stale-Ready-node bug is back."));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId =
            await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, PlanningNodeId, state.Nodes[PlanningNodeId].Version,
            cancellationToken);
        Assert.True(started.Succeeded);

        await DriveToNodeReadyAfterStopAsync(
            store, environment.ProjectRoot, sprintId, PlanningNodeId, started.AttemptId!, cancellationToken);
        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, wedged.Nodes[PlanningNodeId].State);
        Assert.Equal(SprintState.Running, wedged.Sprint.State);
        Assert.Equal(1, wedged.Nodes[PlanningNodeId].AttemptCount);

        PlanningExecutionHostedService restarted = NewService(environment, store, scheduler);
        await restarted.StartAsync(cancellationToken);
        try
        {
            await WaitForSprintPausedAsync(store, environment, sprintId, cancellationToken);

            SprintWorkflowState converged =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            Assert.Equal(SprintState.Paused, converged.Sprint.State);
            Assert.Equal(
                AttemptState.Cancelled, converged.Attempts[started.AttemptId!.Value.ToString("D")].State);
            // The critical assertion: no fresh attempt was ever minted. Before the fix, the node
            // being `Ready` (not `Running`) made the executor's stop-check gate skip straight to
            // StartAttemptAsync, bumping AttemptCount to 2 and invoking the provider a second time.
            Assert.Equal(1, converged.Nodes[PlanningNodeId].AttemptCount);
            Assert.Empty(provider.Calls);
        }
        finally
        {
            await restarted.StopAsync(cancellationToken);
        }
    }

    /// <summary>Manually replays exactly the first two of <see cref="StopOperationCoordinator.FinishStopAsync"/>'s
    /// own durable steps (attempt -&gt; cancelled, node -&gt; failed) and stops there -- simulating a
    /// Host crash between the node-stopped append and the node-rearmed append.</summary>
    private static async Task DriveToNodeFailedAfterStopAsync(
        ISprintStore store,
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = (await store.LoadAsync(projectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot attempt = state.Attempts[attemptId.Value.ToString("D")];
        AppendOutcome stopRequested = await store.AppendAttemptStopRequestedAsync(
            projectRoot, sprintId, attemptId, attempt.Version, cancellationToken);
        Assert.True(stopRequested.Succeeded, $"diag={stopRequested.DiagnosticCode}");

        AppendOutcome cancelled = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_stopped", WorkflowStateNames.ToSnakeCase(AttemptState.Cancelled), attempt.Version,
            Guid.NewGuid(), cancellationToken);
        Assert.True(cancelled.Succeeded, $"diag={cancelled.DiagnosticCode}");

        NodeSnapshot node = cancelled.State!.Nodes[nodeId];
        AppendOutcome failed = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_stopped",
            WorkflowStateNames.ToSnakeCase(NodeState.Failed), node.Version, Guid.NewGuid(), cancellationToken);
        Assert.True(failed.Succeeded, $"diag={failed.DiagnosticCode}");
    }

    /// <summary>Extends <see cref="DriveToNodeFailedAfterStopAsync"/> one more step (node -&gt; ready)
    /// and stops there -- simulating a Host crash between the node-rearmed append and the
    /// sprint-paused append.</summary>
    private static async Task DriveToNodeReadyAfterStopAsync(
        ISprintStore store,
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await DriveToNodeFailedAfterStopAsync(store, projectRoot, sprintId, nodeId, attemptId, cancellationToken);
        SprintWorkflowState state = (await store.LoadAsync(projectRoot, sprintId, cancellationToken))!;
        NodeSnapshot node = state.Nodes[nodeId];
        AppendOutcome ready = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_rearmed",
            WorkflowStateNames.ToSnakeCase(NodeState.Ready), node.Version, Guid.NewGuid(), cancellationToken);
        Assert.True(ready.Succeeded, $"diag={ready.DiagnosticCode}");
    }

    private static PlanningExecutionHostedService NewService(
        TestEnvironment environment, ISprintStore store, SprintScheduler scheduler) =>
        new(
            new PlanningExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store,
            scheduler,
            environment.Resolve<SprintGitIsolation>(),
            environment.Resolve<ProviderCatalog>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment,
            environment.Application,
            environment.Resolve<ActiveOperationRegistry>(),
            environment.Resolve<StopOperationCoordinator>(),
            NullLogger<PlanningExecutionHostedService>.Instance);

    private static async Task<SprintId> CreateSprintReadyForPlanningAsync(
        TestEnvironment environment,
        SprintOrchestrator orchestrator,
        SprintScheduler scheduler,
        ISprintStore store,
        CancellationToken cancellationToken)
    {
        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        Assert.True(created.Succeeded);
        SprintId sprintId = created.SprintId!;

        SprintWorkflowState draft = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Sprint.Version,
                SprintOrchestrator.RunSprintKey(draft.Sprint)), cancellationToken);
        Assert.True(toReady.Succeeded);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)), cancellationToken);
        Assert.True(toRunning.Succeeded);

        SprintWorkflowState running = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult intakeStarted = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, IntakeNodeId, running.Nodes[IntakeNodeId].Version, cancellationToken);
        Assert.True(intakeStarted.Succeeded);
        CompleteAttemptResult intakeCompleted = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, IntakeNodeId, intakeStarted.AttemptId!, true,
            "sha256:" + new string('0', 64), [], [], cancellationToken);
        Assert.True(intakeCompleted.Succeeded);
        return sprintId;
    }

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Waits for the sprint itself to reach `Paused` -- <see cref="StopOperationCoordinator.FinishStopAsync"/>'s
    /// own last durable step -- rather than stopping at the node reaching `Ready`: those are two
    /// separate appends, and asserting on the sprint immediately after observing the node alone
    /// races the coordinator's own remaining step under load.</summary>
    private static async Task WaitForSprintPausedAsync(
        ISprintStore store,
        TestEnvironment environment,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SprintState observed = SprintState.Draft;
        while (stopwatch.Elapsed < PollTimeout)
        {
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            observed = state.Sprint.State;
            if (observed == SprintState.Paused)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D} stayed '{observed}' instead of 'Paused'.");
    }
}
