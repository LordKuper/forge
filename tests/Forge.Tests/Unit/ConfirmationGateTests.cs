using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Stage 11: a test-work node must never become eligible on dependency completion alone — only a
/// recorded, `Confirmed` <see cref="ConfirmationArtifact"/> from its confirmation-role dependency
/// makes it so. See <c>SprintScheduler.IsTestWorkEligibleAsync</c>.
/// </summary>
public sealed class ConfirmationGateTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TestWorkStaysPendingAfterConfirmationNodeSucceedsWithNoRecordedArtifact()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        Assert.Equal(NodeState.Pending, state.Nodes["test_work"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartingTestWorkBeforeConfirmationIsRejectedAsWorkflowBlocked()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", 1, cancellationToken);

        Assert.False(started.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowBlocked, started.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmedArtifactPromotesTestWorkToReady()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        RecordConfirmationResult recorded = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.Confirmed,
            "Feature X matches its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the full test suite locally; all green.")],
            cancellationToken);

        Assert.True(recorded.Succeeded);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["test_work"].State);

        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", state.Nodes["test_work"].Version, cancellationToken);
        Assert.True(started.Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NotConfirmedOutcomeBlocksARunningSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.NotConfirmed,
            "Feature X does not yet match its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Inspection, "Acceptance criterion 2 is not met.")],
            cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, state.Sprint.State);
        Assert.Equal("confirmation", state.Sprint.BlockedReason);
        Assert.Equal(NodeState.Pending, state.Nodes["test_work"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ImplementationCriticalGraphBuilderProducesAValidGraphWithIsolatedRoles()
    {
        IReadOnlyList<NodeDefinition> graph = Forge.Compiler.ImplementationCriticalGraphBuilder.Build();

        Assert.True(SprintGraphValidator.IsValid(graph));
        Assert.Equal(
            [
                NodeRole.Intake, NodeRole.Planning, NodeRole.Implementation, NodeRole.Confirmation,
                NodeRole.TestWork, NodeRole.Review, NodeRole.HumanApproval, NodeRole.Finalization,
            ],
            graph.Select(node => node.Role));
        NodeDefinition testWork = graph.Single(node => node.Role == NodeRole.TestWork);
        Assert.Contains(Forge.Compiler.ImplementationCriticalGraphBuilder.ConfirmationNodeId, testWork.DependsOn);
        NodeDefinition humanApproval = graph.Single(node => node.Role == NodeRole.HumanApproval);
        Assert.Equal(NodeKind.HumanGate, humanApproval.Kind);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintDefaultsToTheImplementationCriticalGraphWhenNoneIsSupplied()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(
            Forge.Compiler.ImplementationCriticalGraphBuilder.Build().Select(node => node.Id),
            definition.Graph.Select(node => node.Id));
    }

    private static readonly IReadOnlyList<NodeDefinition> ConfirmThenTestWorkGraph =
    [
        new("confirm", NodeKind.Work, [], NodeRole.Confirmation),
        new("test_work", NodeKind.Work, ["confirm"], NodeRole.TestWork),
    ];

    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    private static async Task SucceedAsync(
        SprintScheduler scheduler,
        string root,
        SprintId sprintId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        StartAttemptResult started = await scheduler.StartAttemptAsync(root, sprintId, nodeId, 2, cancellationToken);
        Assert.True(started.Succeeded);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            root, sprintId, nodeId, started.AttemptId!, true, SampleDigest, [], [], cancellationToken);
        Assert.True(completed.Succeeded);
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
