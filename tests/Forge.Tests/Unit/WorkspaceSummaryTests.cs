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

    /// <summary>ADR 0069 (Q8): the elapsed-time anchor is absent, not zero, for a sprint that has
    /// never started an attempt -- the sidebar's own default state for a draft or ready sprint.
    /// A pure derivation over an event stream, so no clock is involved on either side: the anchor is
    /// a timestamp and the reader owns "now".</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ASprintThatHasNeverStartedAnAttemptHasNoElapsedTimeAnchor()
    {
        SprintJournalEntry entry = new(
            new(Guid.NewGuid()),
            [
                SprintEvent(1, AnchorTime, AggregateKind.Sprint, "draft"),
                SprintEvent(2, AnchorTime.AddMinutes(1), AggregateKind.Sprint, "ready"),
                // A node reaching `running` is not an attempt starting: only the attempt aggregate
                // counts, or every promoted node would look like work in flight.
                SprintEvent(3, AnchorTime.AddMinutes(2), AggregateKind.Node, "running"),
            ]);

        Assert.Null(entry.FirstAttemptStartedAt);
    }

    /// <summary>ADR 0069 (Q8): the anchor is the FIRST attempt's start and never moves afterwards --
    /// a sprint on its third attempt still reports how long it has been working, not how long the
    /// current attempt has. Also pins the deliberate choice of the `created` transition over the
    /// later `running` one: `CompleteAttemptAsync` walks an attempt's remaining states in one call at
    /// the end, so a `running` event's timestamp sits at the first attempt's completion. Reading it
    /// would understate elapsed time by the whole first attempt and report nothing at all while that
    /// attempt is still running.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TheElapsedTimeAnchorIsTheFirstAttemptsStartAndNotAnyLaterOrRetroactiveTransition()
    {
        SprintJournalEntry entry = new(
            new(Guid.NewGuid()),
            [
                SprintEvent(1, AnchorTime, AggregateKind.Sprint, "draft"),
                SprintEvent(2, AnchorTime.AddMinutes(1), AggregateKind.Node, "running"),
                SprintEvent(3, AnchorTime.AddMinutes(2), AggregateKind.Attempt, "created"),
                SprintEvent(4, AnchorTime.AddMinutes(9), AggregateKind.Attempt, "running"),
                SprintEvent(5, AnchorTime.AddMinutes(9), AggregateKind.Attempt, "failed"),
                SprintEvent(6, AnchorTime.AddMinutes(10), AggregateKind.Attempt, "created"),
            ]);

        Assert.Equal(AnchorTime.AddMinutes(2), entry.FirstAttemptStartedAt);
    }

    /// <summary>ADR 0069 (Q9): sprint-level diff statistics are a fresh read of the integration
    /// branch against the frozen base commit, so they are genuinely absent until that worktree
    /// exists -- and absent, never zero, since zero would claim the sprint changed nothing. Uses the
    /// worktree fake (real `git.exe` behavior belongs to `GitIsolationTests`): what is under test is
    /// that the projection reaches `DiffStatAsync` for the right sprint and degrades without
    /// throwing when it cannot.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiffStatisticsAreAbsentUntilAnIntegrationWorktreeExistsAndSurfaceOnceItDoes()
    {
        FakeWorktreeManager worktrees = new()
        {
            DiffStat = new(3, 120, 8, [new DiffFileStat("src/a.cs", 120, 8, "modified")], 0),
        };
        using TestEnvironment environment = new(worktrees: worktrees);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        ProjectWorkspaceSummary beforeIntegration = await environment.Application
            .GetWorkspaceSummaryAsync(environment.ProjectRoot, cancellationToken);
        Assert.Null(Assert.Single(beforeIntegration.ActiveSprints).DiffStat);

        SprintDefinition definition = (await environment.Resolve<ISprintStore>()
            .LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        GitOperationResult created = await environment.Resolve<SprintGitIsolation>()
            .EnsureIntegrationWorktreeAsync(
                environment.ProjectRoot, beforeIntegration.ProjectId!.Value, sprintId, definition.BaseCommit,
                cancellationToken);
        Assert.True(created.Succeeded, created.DiagnosticCode);

        ProjectWorkspaceSummary afterIntegration = await environment.Application
            .GetWorkspaceSummaryAsync(environment.ProjectRoot, cancellationToken);

        Assert.Equal(
            new SprintDiffStat(3, 120, 8), Assert.Single(afterIntegration.ActiveSprints).DiffStat);
    }

    private static readonly DateTimeOffset AnchorTime = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static WorkflowEvent SprintEvent(long sequence, DateTimeOffset occurredAt, AggregateKind kind, string toState) =>
        new(
            Guid.NewGuid(),
            sequence,
            occurredAt,
            "Changed",
            new(kind, "aggregate", sequence),
            // A real, registered key rather than an invented literal: LocalizationCatalogTests scans
            // every `workflow.`-prefixed literal in this repository and requires a resx entry for it.
            Forge.Localization.MessageKeys.WorkflowAttemptTransitioned,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkflowEvent.ToStateArgument] = toState,
            });

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
