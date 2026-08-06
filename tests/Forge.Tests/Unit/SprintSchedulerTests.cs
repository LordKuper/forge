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
