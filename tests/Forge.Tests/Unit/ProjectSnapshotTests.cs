using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProjectSnapshotTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];
    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnInitializedProjectWithNoSprintsReportsAnEmptySprintList()
    {
        using TestEnvironment environment = await InitializedAsync();

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Sprints);
        Assert.Null(snapshot.ActiveSprintId);
        Assert.Empty(snapshot.Attention);
        Assert.Null(snapshot.Details);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheOnlyNonTerminalSprintBecomesActiveAndFirstInCreationOrder()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, cancellationToken);

        SprintStatus sprint = Assert.Single(snapshot.Sprints);
        Assert.Equal(sprintId.Value, sprint.Id);
        Assert.Equal(1, sprint.CreationSequence);
        Assert.Equal(SprintState.Draft, sprint.State);
        Assert.Equal("implementation-critical", sprint.Workflow);
        Assert.Equal(sprintId.Value, snapshot.ActiveSprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TwoNonTerminalSprintsLeaveTheActiveSprintUnresolved()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken);
        await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, cancellationToken);

        Assert.Equal(2, snapshot.Sprints.Count);
        Assert.Null(snapshot.ActiveSprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FullDetailOnTheActiveSprintReportsItsRunningNodeAndAttempt()
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

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, null, cancellationToken);

        Assert.NotNull(snapshot.Details);
        Assert.Equal(sprintId.Value, snapshot.Details.SprintId);
        EntityStatus node = Assert.Single(snapshot.Details.Nodes);
        Assert.Equal("a", node.Id);
        Assert.Equal("running", node.State);
        Assert.Equal("work", node.Kind);
        EntityStatus attempt = Assert.Single(snapshot.Details.Attempts);
        Assert.Equal(started.AttemptId!.Value.ToString("D"), attempt.Id);
        Assert.Equal("created", attempt.State);
        Assert.Equal("a", attempt.OwnerId);
        Assert.Empty(snapshot.Details.Gates);
        Assert.Empty(snapshot.Details.Artifacts);
        Assert.Null(snapshot.Details.Routing.ResumeNotBefore);
    }

    // ADR 0005: "phase profile, last activity, active deadline... attached to their owners." The
    // 2026-08-15 audit found LastActivityAt read from the domain model but never carried into the
    // snapshot's EntityStatus row.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAttemptsLastActivityHeartbeatSurvivesProjectionSeparatelyFromItsUpdatedAt()
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
        RecordActivityResult activity = await scheduler.RecordAttemptActivityAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, cancellationToken);
        Assert.True(activity.Succeeded);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, null, cancellationToken);

        EntityStatus attempt = Assert.Single(snapshot.Details!.Attempts);
        Assert.NotNull(attempt.LastActivityAt);
        Assert.Equal(activity.Attempt!.LastActivityAt, attempt.LastActivityAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExplicitSprintIdAttachesDetailEvenAtSummaryDetail()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Summary, sprintId.Value, cancellationToken);

        Assert.Equal(SnapshotDetail.Summary, snapshot.Detail);
        Assert.NotNull(snapshot.Details);
        Assert.Equal(sprintId.Value, snapshot.Details.SprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ABlockedSprintFromExhaustedRetriesAppearsInAttention()
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
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, SampleDigest, [], [], cancellationToken);
            nodeVersion = completed.Node!.Version;
        }

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, cancellationToken);

        Assert.Equal(SprintState.Blocked, Assert.Single(snapshot.Sprints).State);
        Assert.Contains(sprintId.Value, snapshot.Attention);
        // A sprint that is its project's only sprint but blocked is not "the only non-terminal
        // sprint" candidate for auto-active selection either — blocked is non-terminal, so it still
        // resolves as active; this asserts that stays true rather than silently dropping to null.
        Assert.Equal(sprintId.Value, snapshot.ActiveSprintId);
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
