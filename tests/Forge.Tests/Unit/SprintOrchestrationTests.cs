using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SprintOrchestrationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintPersistsADraftSprintAndRegistersItInTheManifest()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.SprintId);
        SprintSnapshot? sprint =
            await orchestrator.GetSprintAsync(environment.ProjectRoot, result.SprintId!, cancellationToken);
        Assert.Equal(SprintState.Draft, sprint!.State);
        Assert.Equal(1, sprint.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StaleExpectedProjectVersionRejectsCreationWithoutSideEffect()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 0, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
        Assert.Null(result.SprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayingCreateWithTheSameIdempotencyKeyReturnsTheSameSprintInsteadOfADuplicate()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        Guid idempotencyKey = Guid.NewGuid();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult first =
            await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, idempotencyKey), cancellationToken);
        CreateSprintResult replay =
            await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, idempotencyKey), cancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.Equal(first.SprintId, replay.SprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAdvancesOneLegalHopAtATimeFromDraftToRunning()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintSnapshot draft =
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(draft)), cancellationToken);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);

        Assert.True(toReady.Succeeded);
        Assert.Equal(SprintState.Ready, toReady.Sprint.State);
        Assert.True(toRunning.Succeeded);
        Assert.Equal(SprintState.Running, toRunning.Sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelFromDraftSucceedsAndCancelIsTerminal()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintSnapshot draft =
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        SprintTransitionResult cancelled = await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.CancelSprintKey(draft)),
            cancellationToken);
        SprintTransitionResult secondCancel = await orchestrator.CancelSprintAsync(
            new(
                environment.ProjectRoot,
                sprintId,
                cancelled.Sprint!.Version,
                SprintOrchestrator.CancelSprintKey(cancelled.Sprint)),
            cancellationToken);

        Assert.True(cancelled.Succeeded);
        Assert.Equal(SprintState.Cancelled, cancelled.Sprint.State);
        Assert.False(secondCancel.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTransitionInvalid, secondCancel.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResumeReturnsABlockedSprintToReady()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        // Drive the sprint to "blocked" directly through the store: no orchestrator verb reaches
        // it yet (that needs the node/DAG machinery of a later Stage 6 slice).
        await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"),
            "SprintChanged", "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);
        await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"),
            "SprintChanged", "workflow.sprint_advanced", "running", 2, Guid.NewGuid(), cancellationToken);
        AppendOutcome blocked = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"),
            "SprintChanged", "workflow.sprint_blocked", "blocked", 3, Guid.NewGuid(), cancellationToken);

        SprintTransitionResult resumed = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, 4, SprintOrchestrator.ResumeSprintKey(blocked.State!.Sprint)),
            cancellationToken);

        Assert.True(resumed.Succeeded);
        Assert.Equal(SprintState.Ready, resumed.Sprint!.State);
    }

    /// <summary>Regression: a pending human gate is only ever resolved through
    /// <c>SprintScheduler.ResolveHumanGateAsync</c>, never through <c>ResumeSprintAsync</c> --
    /// resuming past it would silently bypass the decision, flipping the sprint to `running` while
    /// the gate node itself stays `awaiting_human` forever.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResumingASprintAwaitingAHumanGateIsRefusedRatherThanBypassingTheGate()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        Assert.Equal(SprintState.AwaitingHuman, toRunning.Sprint!.State);

        SprintTransitionResult resumed = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, toRunning.Sprint.Version,
                SprintOrchestrator.ResumeSprintKey(toRunning.Sprint)),
            cancellationToken);

        Assert.False(resumed.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTransitionInvalid, resumed.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.AwaitingHuman, state.Sprint.State);
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate"].State);
    }

    /// <summary>Regression: <c>RunSprintAsync</c> must report the sprint's state *after* its own
    /// `AdvanceGraphAsync` side effect, not the pre-advance snapshot -- a graph that opens with a
    /// gate promotes straight past `running` into `awaiting_human` within this same call.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunReportsTheStateAfterItsOwnAdvanceGraphSideEffectNotTheStaleSnapshot()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);

        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);

        Assert.True(toRunning.Succeeded);
        // Not `Running`: the returned snapshot must already reflect the gate's own auto-promotion,
        // matching whatever a fresh read of durable state shows immediately after this call returns.
        Assert.Equal(SprintState.AwaitingHuman, toRunning.Sprint!.State);
        SprintSnapshot fresh =
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(fresh.State, toRunning.Sprint.State);
        Assert.Equal(fresh.Version, toRunning.Sprint.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TransitioningAnUnknownSprintReportsSprintNotFound()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();

        SprintTransitionResult result = await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, SprintId.New(), 1, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintNotFound, result.DiagnosticCode);
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
