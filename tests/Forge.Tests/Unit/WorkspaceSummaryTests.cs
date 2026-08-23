using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 6.2's bounded, per-project workspace-summary query. Confirms real behavior
/// across multiple projects with different states before the smallest risk-based tests were added
/// (repository rule): an uninitialized root, an initialized root with no sprints, and an initialized
/// root with a partially advanced multi-node sprint.</summary>
public sealed class WorkspaceSummaryTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];
    private static readonly IReadOnlyList<NodeDefinition> TwoNodeGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])];
    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUninitializedRootReportsUnavailableWithoutThrowing()
    {
        using TestEnvironment environment = new();
        string uncataloged = Path.Combine(environment.Root, "not-a-project");
        Directory.CreateDirectory(uncataloged);

        ProjectWorkspaceSummary summary = await environment.Application
            .GetWorkspaceSummaryAsync(uncataloged, TestContext.Current.CancellationToken);

        Assert.False(summary.Initialized);
        Assert.Null(summary.ProjectId);
        Assert.Empty(summary.ActiveSprints);
        Assert.Empty(summary.AttentionSprintIds);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnInitializedProjectWithNoSprintsReportsAnEmptyActiveList()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);

        ProjectWorkspaceSummary summary = await environment.Application
            .GetWorkspaceSummaryAsync(environment.ProjectRoot, cancellationToken);

        Assert.True(summary.Initialized);
        Assert.NotNull(summary.ProjectId);
        Assert.Empty(summary.ActiveSprints);
        Assert.Empty(summary.AttentionSprintIds);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CurrentStageAndProgressReflectAPartiallyAdvancedSprintAcrossTwoProjects()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult startedA = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        CompleteAttemptResult completedA = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", startedA.AttemptId!, true, SampleDigest, [], [], cancellationToken);
        Assert.True(completedA.Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState afterAdvance = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // "b" is dependency-eligible (Ready) but not yet running -- start it so it becomes this
        // sprint's actual "current stage" (ADR 0048: the frontier is the running/awaiting-human node,
        // never merely a ready one).
        await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "b", afterAdvance.Nodes["b"].Version, cancellationToken);

        // A second, distinct cataloged project (plan section 12.1's "cross-project isolation") with
        // its own single-node sprint left untouched (still Ready) -- the summary must never conflate
        // the two projects' own progress.
        string secondRoot = Path.Combine(environment.Root, "second-project");
        Directory.CreateDirectory(secondRoot);
        await environment.InitializeAsync(secondRoot, true, cancellationToken);
        await orchestrator.CreateSprintAsync(
            new(secondRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken);

        ProjectWorkspaceSummary first = await environment.Application
            .GetWorkspaceSummaryAsync(environment.ProjectRoot, cancellationToken);
        ProjectWorkspaceSummary second = await environment.Application
            .GetWorkspaceSummaryAsync(secondRoot, cancellationToken);

        SprintWorkspaceSummary firstSprint = Assert.Single(first.ActiveSprints);
        Assert.Equal("b", firstSprint.CurrentStageId);
        Assert.Equal(1, firstSprint.StagesCompleted);
        Assert.Equal(2, firstSprint.StagesTotal);
        Assert.True(firstSprint.HasActiveOperation);
        Assert.Equal("b", firstSprint.ActiveOperationNodeId);

        SprintWorkspaceSummary secondSprint = Assert.Single(second.ActiveSprints);
        Assert.Equal(SprintState.Draft, secondSprint.State);
        Assert.Equal(0, secondSprint.StagesCompleted);
        Assert.Equal(1, secondSprint.StagesTotal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnActiveOperationIsReportedWithoutLoadingTheSprintsTimeline()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        ProjectWorkspaceSummary summary = await environment.Application
            .GetWorkspaceSummaryAsync(environment.ProjectRoot, cancellationToken);

        SprintWorkspaceSummary sprint = Assert.Single(summary.ActiveSprints);
        Assert.True(sprint.HasActiveOperation);
        Assert.Equal("a", sprint.ActiveOperationNodeId);
        Assert.Equal(started.AttemptId!.Value, sprint.ActiveOperationAttemptId);
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
