using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SprintSchedulerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InitializationPromotesOnlyZeroDependencyNodesToReady()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        Assert.Equal(NodeState.Ready, state.Nodes["a"].State);
        Assert.Equal(NodeState.Pending, state.Nodes["b"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AGraphWithAnUnknownDependencyIsRejectedWithoutCreatingASprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, ["missing"])]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintGraphInvalid, result.DiagnosticCode);
        Assert.Empty(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACyclicGraphIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, ["b"]), new("b", NodeKind.Work, ["a"])]),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintGraphInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartingAnAttemptRequiresARunningSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        StartAttemptResult result =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 1, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintNotRunning, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACompletedSuccessfulAttemptSucceedsTheNodeAndRecordsAResult()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [], cancellationToken);

        Assert.True(completed.Succeeded);
        Assert.Equal(NodeState.Succeeded, completed.Node!.State);
        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        NodeResult result = Assert.Single(results);
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Equal(started.AttemptId, result.AttemptId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADownstreamNodeBecomesReadyOnceItsDependencySucceeds()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [], cancellationToken);

        SprintWorkflowState state =
            await scheduler.AdvanceGraphAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(NodeState.Ready, state.Nodes["b"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailuresAutoRetryUntilTheBudgetIsExhaustedThenBlockTheSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", nodeVersion, cancellationToken);
            Assert.True(started.Succeeded);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, SampleDigest, [], [],
                cancellationToken);
            Assert.True(completed.Succeeded);
            nodeVersion = completed.Node!.Version;
        }

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.Blocked, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ManualRetryReArmsAnExhaustedNode()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await ExhaustRetriesAsync(scheduler, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot failed =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["a"];

        NodeActionResult retried = await scheduler.RetryNodeAsync(
            environment.ProjectRoot, sprintId, "a", failed.Version, SprintScheduler.RetryNodeKey(sprintId, failed),
            cancellationToken);

        Assert.True(retried.Succeeded);
        Assert.Equal(NodeState.Ready, retried.Node!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetryWithAStaleKeyIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await ExhaustRetriesAsync(scheduler, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot failed =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["a"];

        NodeActionResult retried = await scheduler.RetryNodeAsync(
            environment.ProjectRoot, sprintId, "a", failed.Version, Guid.NewGuid(), cancellationToken);

        Assert.False(retried.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, retried.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AHumanGateAutoPromotesToAwaitingHumanOnceTheSprintIsRunning()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovingAHumanGateSucceedsTheNodeAndRecordsAResult()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];

        NodeActionResult resolved = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        Assert.True(resolved.Succeeded);
        Assert.Equal(NodeState.Succeeded, resolved.Node!.State);
        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvingAGateAfterAnInterruptedPriorAttemptStillReachesARealOutcome()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        // Simulate a crash right after a prior ResolveHumanGateAsync call's first append landed,
        // before the rest of that sequence ran: an attempt exists, but the node never moved.
        await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Attempt, AttemptId.New().Value.ToString("D"),
            "AttemptChanged", "workflow.attempt_created", "created", 0, Guid.NewGuid(), cancellationToken);

        NodeActionResult resolved = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        // Must reach a real terminal outcome, never a false "success" that leaves the gate stuck.
        Assert.True(resolved.Succeeded);
        Assert.Equal(NodeState.Succeeded, resolved.Node!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RejectingAHumanGateFailsTheNodeWithoutAutomaticRetry()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];

        NodeActionResult resolved = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        Assert.True(resolved.Succeeded);
        Assert.Equal(NodeState.Failed, resolved.Node!.State);
        // A rejected gate never auto-retries, so it must block the sprint immediately rather than
        // leave it stuck in `running` forever with nothing left to do and nothing moving it on.
        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.Blocked, sprint!.State);
    }

    /// <summary>Regression: a caller that cannot remember the *original* (version, key) pair a
    /// prior, already-successful call used -- e.g. a stateless CLI invocation that re-derives both
    /// fresh from whatever the node's current state happens to be -- must still resolve cleanly
    /// instead of hitting `node_transition_invalid`. The original resumability design recognized a
    /// retry only by recomputing the exact same deterministic hash the first call used, which a
    /// caller supplying a *different*, freshly-observed version can never reproduce once the node
    /// has moved past `awaiting_human`.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovingAnAlreadyApprovedGateWithAFreshlyDerivedVersionAndKeyStillSucceeds()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult first = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        Assert.True(first.Succeeded);
        Assert.Equal(NodeState.Succeeded, first.Node!.State);

        // A later, independent caller with no memory of the original call: it reads the node's
        // *current* state and derives its own (version, key) pair from that, the same way
        // ForgeApplication.ResolveGateAsync does.
        NodeSnapshot current = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult resumed = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, current.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, current), cancellationToken);

        Assert.True(resumed.Succeeded, $"diag={resumed.DiagnosticCode}");
        Assert.Equal(NodeState.Succeeded, resumed.Node!.State);
    }

    /// <summary>Regression companion: the fix above must not let a caller silently reinterpret an
    /// already-decided gate as the opposite decision. A freshly-derived reject call against a gate
    /// that was already approved must be refused, not resumed.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RejectingAnAlreadyApprovedGateWithAFreshlyDerivedVersionAndKeyIsRefused()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult first = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        Assert.True(first.Succeeded);

        NodeSnapshot current = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult flipped = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, current.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, current), cancellationToken);

        Assert.False(flipped.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeTransitionInvalid, flipped.DiagnosticCode);
        SprintWorkflowState finalState =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, finalState.Nodes["gate"].State);
    }

    /// <summary>Regression: linkage-based resumability must be scoped to
    /// <see cref="NodeKind.HumanGate"/> nodes. A prior fix recognized a resumed gate call by finding
    /// *any* attempt linked to the target node id, which also matches an ordinary
    /// <see cref="NodeKind.Work"/> node's own in-progress attempt (it is stamped with the same
    /// `NodeIdArgument`) -- letting `ResolveHumanGateAsync` hijack a live, unrelated Work node's
    /// attempt and fraudulently walk it to `succeeded`.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvingAGateAgainstAWorkNodeIsRefusedEvenWhileThatNodeHasALiveAttempt()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["a"];

        NodeActionResult resolved = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "a", true, node.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, node), cancellationToken);

        Assert.False(resolved.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeKindMismatch, resolved.DiagnosticCode);
        NodeSnapshot untouched = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["a"];
        Assert.Equal(NodeState.Running, untouched.State);
        Assert.Empty(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    /// <summary>Regression: a rejected gate can be manually re-armed to `awaiting_human` via
    /// <see cref="SprintScheduler.RetryNodeAsync"/> for a second decision. Linkage-based resumability
    /// must not pick up the first, terminal (`failed`) attempt for this second round -- it must mint
    /// a fresh attempt, so the second decision is a real, separately recorded outcome rather than a
    /// no-op replay of the first rejection.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApprovingARetriedGateAfterAnEarlierRejectionRecordsAFreshApproval()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult rejected = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        Assert.True(rejected.Succeeded);
        Assert.Equal(NodeState.Failed, rejected.Node!.State);

        NodeSnapshot failed = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult retried = await scheduler.RetryNodeAsync(
            environment.ProjectRoot, sprintId, "gate", failed.Version,
            SprintScheduler.RetryNodeKey(sprintId, failed), cancellationToken);
        Assert.True(retried.Succeeded);
        Assert.Equal(NodeState.Ready, retried.Node!.State);

        // Retrying the node alone does not resume a `blocked` sprint -- an operator must explicitly
        // resume and re-run it (mirroring `ConfirmationGateTests`'s own recovery sequence), which is
        // also the only moment a human gate re-promotes itself from `ready` to `awaiting_human`.
        SprintSnapshot blocked = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult resumed = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, blocked.Version, SprintOrchestrator.ResumeSprintKey(blocked)),
            cancellationToken);
        Assert.True(resumed.Succeeded);
        SprintTransitionResult running = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, resumed.Sprint!.Version, SprintOrchestrator.RunSprintKey(resumed.Sprint)),
            cancellationToken);
        Assert.True(running.Succeeded);
        NodeSnapshot reArmed = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        Assert.Equal(NodeState.AwaitingHuman, reArmed.State);

        NodeActionResult approved = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, reArmed.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, reArmed), cancellationToken);

        Assert.True(approved.Succeeded, $"diag={approved.DiagnosticCode}");
        Assert.Equal(NodeState.Succeeded, approved.Node!.State);
        // Two distinct decisions on the same node id, each with its own recorded result.
        Assert.Equal(2, (await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken)).Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingActivityOnARunningAttemptBumpsItsLastActivityTimeWithoutChangingItsState()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        AttemptSnapshot beforeActivity =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
                .Attempts[started.AttemptId!.Value.ToString("D")];
        Assert.Null(beforeActivity.LastActivityAt);

        RecordActivityResult recorded = await scheduler.RecordAttemptActivityAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, cancellationToken);

        Assert.True(recorded.Succeeded);
        Assert.NotNull(recorded.Attempt!.LastActivityAt);
        Assert.Equal(AttemptState.Created, recorded.Attempt.State);
        // Repeats freely: not gated by the attempt's transition version.
        RecordActivityResult recordedAgain = await scheduler.RecordAttemptActivityAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, cancellationToken);
        Assert.True(recordedAgain.Succeeded);
        Assert.True(recordedAgain.Attempt!.LastActivityAt >= recorded.Attempt.LastActivityAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActivityIsRejectedOnceTheAttemptReachesATerminalState()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        RecordActivityResult recorded = await scheduler.RecordAttemptActivityAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, cancellationToken);

        Assert.False(recorded.Succeeded);
        Assert.Equal(DiagnosticCodes.AttemptTerminal, recorded.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActivityForAnUnknownAttemptIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordActivityResult recorded = await scheduler.RecordAttemptActivityAsync(
            environment.ProjectRoot, sprintId, AttemptId.New(), cancellationToken);

        Assert.False(recorded.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, recorded.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindingsCanBeRecordedAndResolved()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["src/Foo.cs:12"], null, cancellationToken);
        Assert.True(recorded.Succeeded);

        RecordFindingResult resolved = await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, FindingStatus.Resolved,
            cancellationToken);

        Assert.True(resolved.Succeeded);
        Assert.Equal(FindingStatus.Resolved, resolved.Finding!.Status);
        Finding stored = Assert.Single(
            await scheduler.GetFindingsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(FindingStatus.Resolved, stored.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandoffsCanBeRecordedAndRead()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        RecordHandoffResult recorded = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "a", new string('a', 40), "Did the thing.",
            ["Chose approach X."], ["Approach X is unproven."], null, cancellationToken);

        Assert.True(recorded.Succeeded);
        Handoff stored = Assert.Single(
            await scheduler.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal("Did the thing.", stored.Summary);
        Assert.Equal("a", stored.NodeId.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAFindingWithNoEvidenceIsRejectedAsAnInvalidRecord()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        // finding.schema.json requires at least one piece of evidence.
        RecordFindingResult result = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Low, "finding.example",
            new Dictionary<string, string?>(), [], null, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, result.DiagnosticCode);
        Assert.Empty(await scheduler.GetFindingsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAHandoffWithAMalformedBaseShaIsRejectedAsAnInvalidRecord()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        // handoff.schema.json requires a 40- or 64-hex-character base_sha.
        RecordHandoffResult result = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "a", "not-a-commit-sha", "summary", [], [], null,
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, result.DiagnosticCode);
        Assert.Empty(await scheduler.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompletingAnAttemptWithAMalformedDigestIsRejectedBeforeAnyDurableChange()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        // node-result.schema.json requires input_digest to match ^sha256:[0-9a-f]{64}$.
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, "not-a-digest", [], [],
            cancellationToken);

        Assert.False(completed.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, completed.DiagnosticCode);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["a"];
        Assert.Equal(NodeState.Running, node.State);
        Assert.Empty(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompletingEveryNodeMovesTheSprintToReadyToFinalize()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        StartAttemptResult startedA =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", startedA.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        StartAttemptResult startedB =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "b", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "b", startedB.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.ReadyToFinalize, sprint!.State);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(FindingSeverity.Info)]
    [InlineData(FindingSeverity.Low)]
    [InlineData(FindingSeverity.Medium)]
    [InlineData(FindingSeverity.High)]
    [InlineData(FindingSeverity.Critical)]
    public async Task AnOpenFindingOfAnySeverityBlocksFinalizationEvenWithEveryNodeSettled(FindingSeverity severity)
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, severity, "finding.example", new Dictionary<string, string?>(),
            ["src/Foo.cs:1"], null, cancellationToken);

        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.Running, sprint!.State);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(FindingStatus.Accepted)]
    [InlineData(FindingStatus.Resolved)]
    [InlineData(FindingStatus.Dismissed)]
    public async Task ANonOpenFindingDoesNotBlockFinalization(FindingStatus status)
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["src/Foo.cs:1"], null, cancellationToken);
        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, status, cancellationToken);

        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.ReadyToFinalize, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvingTheLastOpenFindingLetsAnAlreadySettledSprintAdvance()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Critical, "finding.example",
            new Dictionary<string, string?>(), ["src/Foo.cs:1"], null, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        Assert.Equal(
            SprintState.Running,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, FindingStatus.Resolved, cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.ReadyToFinalize, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompletingAnAttemptForTheWrongNodeIsRejectedWithoutChangingEitherNode()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoIndependentNodeGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult startedA =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "b", 2, cancellationToken);

        // Node A's attempt is presented as if it belonged to node B — the durable owner recorded at
        // attempt creation must reject the mismatch rather than let the wrong pair settle.
        CompleteAttemptResult crossed = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "b", startedA.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        Assert.False(crossed.Succeeded);
        Assert.Equal(DiagnosticCodes.AttemptOwnershipMismatch, crossed.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, state.Nodes["a"].State);
        Assert.Equal(NodeState.Running, state.Nodes["b"].State);
        Assert.Empty(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));

        // The real pairing still completes normally afterward.
        CompleteAttemptResult real = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", startedA.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        Assert.True(real.Succeeded);
        Assert.Equal(NodeState.Succeeded, real.Node!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MultipleSimultaneousGatesKeepTheSprintAwaitingHumanUntilBothResolve()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph: [new("gate1", NodeKind.HumanGate, []), new("gate2", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate1"].State);
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate2"].State);
        Assert.Equal(SprintState.AwaitingHuman, state.Sprint.State);

        NodeSnapshot gate1 = state.Nodes["gate1"];
        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate1", true, gate1.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate1), cancellationToken);

        // One gate resolved, one still open: the sprint must stay `awaiting_human`, not resume.
        SprintSnapshot? afterFirst = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.AwaitingHuman, afterFirst!.State);

        // A store reopen mid-sequence must not change that derived state.
        FileSprintEventLog reopened = new(new FakeClock());
        SprintWorkflowState reopenedState =
            (await reopened.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.AwaitingHuman, reopenedState.Sprint.State);

        NodeSnapshot gate2 = reopenedState.Nodes["gate2"];
        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate2", true, gate2.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate2), cancellationToken);

        // Both gates settled and nothing else is outstanding, so the sprint doesn't just resume
        // `running` — it advances straight through to `ready_to_finalize` in the same call.
        SprintSnapshot? afterBoth = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.ReadyToFinalize, afterBoth!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SequentialGatesResolveOneAfterAnotherWithoutDeadlocking()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph:
                [
                    new("gate1", NodeKind.HumanGate, []),
                    new("gate2", NodeKind.HumanGate, ["gate1"]),
                ]),
            cancellationToken)).SprintId!;

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        SprintWorkflowState initial = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, initial.Nodes["gate1"].State);
        Assert.Equal(NodeState.Pending, initial.Nodes["gate2"].State);

        NodeSnapshot gate1 = initial.Nodes["gate1"];
        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate1", true, gate1.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate1), cancellationToken);

        // gate2 only becomes eligible once gate1 resolves, in the very same call that also
        // resynchronizes the sprint back toward `running` — it must not be left stranded at `ready`
        // with nothing ever revisiting it.
        SprintWorkflowState afterFirst = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, afterFirst.Nodes["gate2"].State);
        Assert.Equal(SprintState.AwaitingHuman, afterFirst.Sprint.State);

        NodeSnapshot gate2 = afterFirst.Nodes["gate2"];
        NodeActionResult resolved = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate2", true, gate2.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate2), cancellationToken);

        Assert.True(resolved.Succeeded);
        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.ReadyToFinalize, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvingTheFindingThatBlockedAnAlreadySettledSprintUnblocksIt()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [], cancellationToken);
        Assert.Equal(
            SprintState.ReadyToFinalize,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);
        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["src/Foo.cs:1"], null, cancellationToken);
        Assert.Equal(
            SprintState.Blocked,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        RecordFindingResult resolved = await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, FindingStatus.Resolved, cancellationToken);

        Assert.True(resolved.Succeeded);
        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.ReadyToFinalize, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SkippingAStuckNodeInABlockedSprintDoesNotBypassTheOperatorsResumeDecision()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await ExhaustRetriesAsync(scheduler, environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(
            SprintState.Blocked,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        // A manual retry re-arms the exhausted node, but the sprint stays `blocked` — a real node
        // failure, not a late finding, put it there, so it must not silently clear on its own.
        NodeSnapshot failed = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["a"];
        NodeActionResult retried = await scheduler.RetryNodeAsync(
            environment.ProjectRoot, sprintId, "a", failed.Version, SprintScheduler.RetryNodeKey(sprintId, failed),
            cancellationToken);
        Assert.True(retried.Succeeded);

        // Skipping the re-armed node settles every node good, exactly like the late-open-finding
        // recovery path — but this sprint was never blocked by a finding, so skipping it must not
        // bypass the operator's explicit resume_sprint -> run_sprint decision that a real node
        // failure requires.
        NodeActionResult skipped = await scheduler.SkipNodeAsync(
            environment.ProjectRoot, sprintId, "a", retried.Node!.Version, cancellationToken);
        Assert.True(skipped.Succeeded);
        Assert.Equal(
            SprintState.Blocked,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        // The full exploit: with every node now settled good, recording and resolving a completely
        // unrelated finding must not launder this node-caused block into `ready_to_finalize` either
        // — the durable block reason is `node`, not `finding`, regardless of how settled the graph
        // looks right now.
        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Low, "finding.unrelated",
            new Dictionary<string, string?>(), ["src/Unrelated.cs:1"], null, cancellationToken);
        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, FindingStatus.Resolved, cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.Blocked, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvingAnUnrelatedFindingDoesNotLaunderARejectedGateToReadyToFinalize()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];

        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        Assert.Equal(
            SprintState.Blocked,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        // A rejected gate can be manually re-armed and skipped exactly like a failed work node —
        // settling the graph without ever passing through a real approval.
        NodeSnapshot rejected = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        NodeActionResult retried = await scheduler.RetryNodeAsync(
            environment.ProjectRoot, sprintId, "gate", rejected.Version,
            SprintScheduler.RetryNodeKey(sprintId, rejected), cancellationToken);
        Assert.True(retried.Succeeded);
        NodeActionResult skipped = await scheduler.SkipNodeAsync(
            environment.ProjectRoot, sprintId, "gate", retried.Node!.Version, cancellationToken);
        Assert.True(skipped.Succeeded);

        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Low, "finding.unrelated",
            new Dictionary<string, string?>(), ["src/Unrelated.cs:1"], null, cancellationToken);
        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, FindingStatus.Resolved, cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.Blocked, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentFindingWritesNeverLoseADistinctFinding()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        const int count = 20;
        await Task.WhenAll(Enumerable.Range(0, count).Select(index => scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Low, "finding.example",
            new Dictionary<string, string?>(), [$"src/Foo.cs:{index}"], null, cancellationToken)));

        IReadOnlyList<Finding> findings =
            await scheduler.GetFindingsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(count, findings.Count);
        Assert.Equal(count, findings.Select(item => item.FindingId).Distinct().Count());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProgressSurvivesReopeningTheStoreFromScratch()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [], cancellationToken);

        // A brand new store instance simulates a process restart: only durable files are shared.
        FileSprintEventLog reopened = new(new FakeClock());
        SprintWorkflowState? state = await reopened.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(state);
        Assert.Equal(NodeState.Succeeded, state.Nodes["a"].State);
        Assert.Equal(SprintState.ReadyToFinalize, state.Sprint.State);
    }

    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];

    private static readonly IReadOnlyList<NodeDefinition> TwoNodeGraph =
    [
        new("a", NodeKind.Work, []),
        new("b", NodeKind.Work, ["a"]),
    ];

    private static readonly IReadOnlyList<NodeDefinition> TwoIndependentNodeGraph =
    [
        new("a", NodeKind.Work, []),
        new("b", NodeKind.Work, []),
    ];

    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    private static async Task ExhaustRetriesAsync(
        SprintScheduler scheduler,
        string root,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started =
                await scheduler.StartAttemptAsync(root, sprintId, "a", nodeVersion, cancellationToken);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                root, sprintId, "a", started.AttemptId!, false, SampleDigest, [], [], cancellationToken);
            nodeVersion = completed.Node!.Version;
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

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
