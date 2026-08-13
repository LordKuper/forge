using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class NotificationProjectorTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASprintBlockedByExhaustedRetriesProjectsABlockedNotification()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", nodeVersion, cancellationToken);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, SampleDigest, [], [],
                cancellationToken);
            nodeVersion = completed.Node!.Version;
        }

        ControlEventsPage page = await reader.ReadAsync(environment.ProjectRoot, null, cancellationToken);
        IReadOnlyList<NotificationProjection> projections = NotificationProjector.Project(page.Events);

        NotificationProjection blocked = Assert.Single(
            projections, projection => projection.Kind == NotificationKind.Blocked);
        Assert.Equal(sprintId.Value, blocked.SprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASprintAwaitingAHumanGateProjectsAnAwaitingHumanNotification()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        ControlEventsPage page = await reader.ReadAsync(environment.ProjectRoot, null, cancellationToken);
        IReadOnlyList<NotificationProjection> projections = NotificationProjector.Project(page.Events);

        Assert.Contains(projections, projection => projection.Kind == NotificationKind.AwaitingHuman);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NodeAndAttemptTransitionsAreNeverProjectedEvenWhenTheirStateNameCollidesWithASprintKind()
    {
        Guid sprintId = Guid.NewGuid();
        ControlEventRecord nodeFailed = new(
            sprintId,
            new(
                Guid.NewGuid(), 0, DateTimeOffset.UnixEpoch, "NodeChanged",
                new(AggregateKind.Node, "a", 1), "workflow.node_failed",
                new Dictionary<string, string?> { [WorkflowEvent.ToStateArgument] = "failed" }));

        IReadOnlyList<NotificationProjection> projections = NotificationProjector.Project([nodeFailed]);

        Assert.Empty(projections);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EveryEventIdIsUniqueEvenAcrossRepeatedReadsSoACallerCanDedupOnItAlone()
    {
        Guid sprintId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        ControlEventRecord completed = new(
            sprintId,
            new(
                eventId, 4, DateTimeOffset.UnixEpoch, "SprintChanged",
                new(AggregateKind.Sprint, sprintId.ToString("D"), 5), "workflow.sprint_completed",
                new Dictionary<string, string?> { [WorkflowEvent.ToStateArgument] = "completed" }));

        NotificationProjection first = Assert.Single(NotificationProjector.Project([completed]));
        NotificationProjection redelivered = Assert.Single(NotificationProjector.Project([completed]));

        Assert.Equal(NotificationKind.Completed, first.Kind);
        Assert.Equal(eventId, first.EventId);
        Assert.Equal(first, redelivered);
    }

    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

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
