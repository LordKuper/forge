using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Slice 6's timeline pane: cursor-based incremental loading, filtering by item type, and unread
/// position tracking. Confirms real behavior against an actual sprint's append-only event journal
/// (never a hand-built fixture standing in for the real projector) before the smallest risk-based
/// tests were added.
/// </summary>
public sealed class SprintTimelineViewModelTests
{
    private static readonly IReadOnlyList<NodeDefinition> TwoNodeGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])];

    private static async Task<(Guid ProjectId, SprintId SprintId)> SeedSprintAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        SprintSnapshot? draft = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft!.Version, SprintOrchestrator.RunSprintKey(draft)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        return (added.Entry!.ProjectId, sprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InitializeLoadsEveryItemUnreadWhenNoWatermarkWasEverRecorded()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintId) = await SeedSprintAsync(environment, cancellationToken);
        SprintTimelineViewModel timeline = new(environment.Application, environment.Resolve<ProjectCatalogStore>());

        TimelineState state = await timeline.InitializeAsync(
            projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);

        Assert.NotEmpty(state.Items);
        Assert.All(state.Items, item => Assert.True(item.Unread));
        Assert.Equal(state.Items.Count, state.UnreadCount);
        Assert.False(state.HasMore);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FilteringByTypeNarrowsTheRenderedItemsWithoutDiscardingTheLoadedSet()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintId) = await SeedSprintAsync(environment, cancellationToken);
        SprintTimelineViewModel timeline = new(environment.Application, environment.Resolve<ProjectCatalogStore>());
        TimelineState initial = await timeline.InitializeAsync(
            projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);
        string oneType = initial.AvailableFilterTypes[0];

        TimelineState filtered = timeline.SetFilter(oneType);
        Assert.All(filtered.Items, item => Assert.Equal(oneType, item.Type));
        Assert.True(filtered.Items.Count <= initial.Items.Count);

        TimelineState cleared = timeline.SetFilter(null);
        Assert.Equal(initial.Items.Count, cleared.Items.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkAllReadAdvancesTheWatermarkAndNewlyAppendedItemsStillArriveUnread()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintId) = await SeedSprintAsync(environment, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SprintTimelineViewModel timeline = new(environment.Application, catalog);
        await timeline.InitializeAsync(projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);

        await timeline.MarkAllReadAsync(cancellationToken);
        TimelineState afterMarkRead = timeline.SetFilter(null);
        Assert.Equal(0, afterMarkRead.UnreadCount);

        // A fresh view-model instance (simulating navigating away and back) must still see the
        // persisted watermark, not start over.
        SprintTimelineViewModel reopened = new(environment.Application, catalog);
        TimelineState reopenedState = await reopened.InitializeAsync(
            projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);
        Assert.Equal(0, reopenedState.UnreadCount);

        // Cancelling the sprint appends a brand-new event after the watermark was recorded --
        // exactly the case that must render as unread again.
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintSnapshot running = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, running.Version, SprintOrchestrator.CancelSprintKey(running)),
            cancellationToken);
        TimelineState polled = await reopened.LoadMoreAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(polled.UnreadCount > 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DraftIsRestoredByAFreshViewModelInstanceAfterASimulatedRestart()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintId) = await SeedSprintAsync(environment, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SprintTimelineViewModel timeline = new(environment.Application, catalog);
        await timeline.InitializeAsync(projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);

        await timeline.SaveDraftAsync("stale finding, rewinding to replan", cancellationToken);

        SprintTimelineViewModel reopened = new(environment.Application, catalog);
        await reopened.InitializeAsync(projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);
        string? draft = await reopened.LoadDraftAsync(cancellationToken);

        Assert.Equal("stale finding, rewinding to replan", draft);
    }
}
