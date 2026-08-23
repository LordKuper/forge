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
        SprintId sprintId = await SeedAdditionalSprintAsync(environment, cancellationToken);
        return (added.Entry!.ProjectId, sprintId);
    }

    /// <summary>Seeds a(nother) sprint into a project <see cref="SeedSprintAsync"/> already
    /// initialized -- used by the concurrency regression tests below, which need two distinct sprints
    /// in the same project without re-running (and duplicating) project initialization/catalog
    /// registration. <see cref="CreateSprintCommand.ExpectedStateVersion"/> is re-resolved from the
    /// project's own current status rather than hardcoded, since creating (and running) an earlier
    /// sprint already advances it past whatever a freshly initialized project starts at.</summary>
    private static async Task<SprintId> SeedAdditionalSprintAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ProjectRootStatus status = await environment.Resolve<ProjectRootResolver>()
            .ResolveAsync(environment.ProjectRoot, cancellationToken).ConfigureAwait(false);
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, StatusAdvisor.StateVersion(status), Guid.NewGuid(), Graph: TwoNodeGraph),
            cancellationToken)).SprintId!;
        SprintSnapshot? draft = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft!.Version, SprintOrchestrator.RunSprintKey(draft)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        return sprintId;
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

    /// <summary>
    /// PR #99 review finding 4: ADR 0051 originally claimed every timeline item's
    /// <c>OccurredAt</c> is strictly increasing, and keyed the unread watermark on it. That premise
    /// does not hold -- <c>IClock.UtcNow</c> is not guaranteed to advance between two events appended
    /// moments apart (already documented as reachable, <c>SprintScheduler.cs</c>'s own remarks on
    /// <c>RecordedAt</c>) -- so a tie could leave a genuinely new item born already-read.
    /// <see cref="TiedTimestampSprintStore"/> forces every event this test reads back to report the
    /// exact same <c>OccurredAt</c>, reproducing a tie deterministically: after marking everything
    /// read at that tied instant, a brand-new event appended afterwards (and read back with the same
    /// tied timestamp) must still surface as unread, because the watermark now compares
    /// <see cref="SprintTimelineItem.Sequence"/> -- the journal's own dense, strictly increasing
    /// counter -- never the colliding timestamp.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATiedOccurredAtNeverHidesAGenuinelyNewItemBehindTheReadWatermark()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintId) = await SeedSprintAsync(environment, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        DateTimeOffset tiedInstant = DateTimeOffset.UtcNow;
        ForgeApplication tiedApplication =
            environment.ResolveApplicationWithSprintStore(store => new TiedTimestampSprintStore(store, tiedInstant));
        SprintTimelineViewModel timeline = new(tiedApplication, catalog);

        TimelineState initial = await timeline.InitializeAsync(
            projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);
        Assert.True(initial.Items.Count >= 2, "The seeded sprint must produce at least two events to prove a tie.");
        Assert.All(initial.Items, item => Assert.Equal(tiedInstant, item.OccurredAt));

        await timeline.MarkAllReadAsync(cancellationToken);
        Assert.Equal(0, timeline.SetFilter(null).UnreadCount);

        // A brand-new event, appended after the watermark was recorded, whose OccurredAt still ties
        // with every already-read item once read back through the same tied clock.
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintSnapshot running = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, running.Version, SprintOrchestrator.CancelSprintKey(running)),
            cancellationToken);

        TimelineState polled = await timeline.LoadMoreAsync(environment.ProjectRoot, cancellationToken);

        Assert.True(
            polled.UnreadCount > 0,
            "A new event tied in OccurredAt with the read watermark must still surface as unread.");
    }

    /// <summary>
    /// PR #99 round-2 review, critical finding: round-1 moved the 15s timeline poll's fetch outside
    /// <c>ShellRenderGate</c>'s mutation guard (correctly, to stop it from dropping a concurrent user
    /// click), but that made this shared, long-lived instance's <c>cursor</c>/<c>loaded</c>
    /// read-fetch-write concurrently reachable from the poll tick, "Load more", and the post-mutation
    /// <c>RefreshAllAsync</c> refresh all calling <see cref="SprintTimelineViewModel.LoadMoreAsync"/>
    /// on the same instance. The reviewer reproduced this against the real projector: two overlapping
    /// <c>LoadMoreAsync</c> calls both fetch from the same starting cursor and both append their page,
    /// duplicating the newest item and double-counting it as unread. <see cref="GatedSprintStore"/>
    /// reproduces the exact interleaving deterministically: the first call is parked mid-fetch while a
    /// second, unblocked call races ahead and applies its page first, then the first call is released
    /// and must discard its now-stale page instead of duplicating it.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentLoadMoreCallsNeverDuplicateItemsOrDoubleCountUnread()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintId) = await SeedSprintAsync(environment, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        GatedSprintStore gated = new(environment.Resolve<ISprintStore>());
        ForgeApplication gatedApplication = environment.ResolveApplicationWithSprintStore(_ => gated);
        SprintTimelineViewModel timeline = new(gatedApplication, catalog);
        TimelineState initial = await timeline.InitializeAsync(
            projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);

        // Determine, via a completely independent (ungated) view over the same journal, exactly how
        // many new items cancelling the sprint appends -- so the assertions below prove "no more, no
        // fewer than one uncontended fetch would produce," not a hardcoded item count.
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintTimelineViewModel reference = new(environment.Application, catalog);
        TimelineState referenceInitial = await reference.InitializeAsync(
            projectId, environment.ProjectRoot, sprintId.Value, cancellationToken);
        SprintSnapshot running = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, running.Version, SprintOrchestrator.CancelSprintKey(running)),
            cancellationToken);
        TimelineState referenceAfterCancel = await reference.LoadMoreAsync(environment.ProjectRoot, cancellationToken);
        int newItemCount = referenceAfterCancel.Items.Count - referenceInitial.Items.Count;
        Assert.True(newItemCount > 0, "Cancelling the sprint must append at least one new timeline item.");

        gated.ArmNextCall();
        Task<TimelineState> slow = timeline.LoadMoreAsync(environment.ProjectRoot, cancellationToken);
        await gated.WaitUntilNextCallEnteredAsync();
        // Races ahead of the parked call above -- not gated, since arming only ever covers the next
        // call.
        TimelineState fast = await timeline.LoadMoreAsync(environment.ProjectRoot, cancellationToken);
        gated.ReleaseNextCall();
        TimelineState slowResult = await slow;

        int expectedCount = initial.Items.Count + newItemCount;
        Assert.Equal(expectedCount, fast.Items.Count);
        // The parked call must self-heal to whatever the winner already established, never duplicate
        // the page it fetched from the now-stale cursor.
        Assert.Equal(expectedCount, slowResult.Items.Count);
        TimelineState finalState = timeline.SetFilter(null);
        Assert.Equal(expectedCount, finalState.Items.Count);
        Assert.Equal(expectedCount, finalState.UnreadCount);
        Assert.Equal(expectedCount, finalState.Items.Select(item => item.Id).Distinct().Count());
    }

    /// <summary>
    /// PR #99 round-2 review, critical finding (second half): navigating to a different sprint while
    /// an old sprint's fetch is still outstanding (the poll never blocks on navigation -- round-1's
    /// own fix) must not let that fetch's completion mix the old sprint's items into the new sprint's
    /// <c>loaded</c> list or, worse, let <see cref="SprintTimelineViewModel.MarkAllReadAsync"/>
    /// persist a watermark computed over that mixed set under the *new* sprint's catalog key. Sprint
    /// navigation reuses this same shared <see cref="SprintTimelineViewModel"/> instance rather than
    /// constructing a fresh one (see <c>WorkspaceShellPage.SprintWorkspace.cs</c>'s single
    /// <c>sprintWorkspace</c> field), so the instance itself -- not just the page -- must refuse to
    /// apply a fetch that no longer belongs to its current sprint.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AStaleFetchForAPreviousSprintNeverCorruptsTheCurrentlyDisplayedSprintOrItsWatermark()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid projectId, SprintId sprintA) = await SeedSprintAsync(environment, cancellationToken);
        SprintId sprintB = await SeedAdditionalSprintAsync(environment, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        GatedSprintStore gated = new(environment.Resolve<ISprintStore>());
        ForgeApplication gatedApplication = environment.ResolveApplicationWithSprintStore(_ => gated);
        SprintTimelineViewModel timeline = new(gatedApplication, catalog);
        await timeline.InitializeAsync(projectId, environment.ProjectRoot, sprintA.Value, cancellationToken);

        // A brand-new event on sprint A, appended *after* this instance's own baseline fetch above --
        // so the stale poll this test provokes below has a real, non-empty page to (wrongly) apply,
        // exactly like the reviewer's reproduction. Without this, sprint A's stale fetch would
        // legitimately return zero new items and the corruption below would be invisible to the
        // item-count assertions even on the pre-fix code.
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintSnapshot runningA = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintA, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintA, runningA.Version, SprintOrchestrator.CancelSprintKey(runningA)),
            cancellationToken);

        // Ground truth for both sprints, established independently (via completely separate,
        // ungated instances) before the race, so the assertions below prove "exactly B, nothing
        // more/less/mixed in" and "exactly A's real post-cancellation state," not just "non-empty."
        SprintTimelineViewModel reference = new(environment.Application, catalog);
        TimelineState referenceB = await reference.InitializeAsync(
            projectId, environment.ProjectRoot, sprintB.Value, cancellationToken);
        Assert.NotEmpty(referenceB.Items);
        SprintTimelineViewModel referenceAfterCancel = new(environment.Application, catalog);
        TimelineState referenceA = await referenceAfterCancel.InitializeAsync(
            projectId, environment.ProjectRoot, sprintA.Value, cancellationToken);

        gated.ArmNextCall();
        // Simulates the 15s poll's fetch for sprint A still being in flight -- this call's cursor was
        // captured from this instance's own state before the cancellation above, so it now has sprint
        // A's brand-new event to (wrongly) fetch and apply.
        Task<TimelineState> stalePollForA = timeline.LoadMoreAsync(environment.ProjectRoot, cancellationToken);
        await gated.WaitUntilNextCallEnteredAsync();

        // Simulates the user navigating to sprint B while sprint A's poll fetch is still outstanding
        // -- not gated, since arming only ever covers the next call.
        TimelineState bState = await timeline.InitializeAsync(
            projectId, environment.ProjectRoot, sprintB.Value, cancellationToken);

        gated.ReleaseNextCall();
        TimelineState stalePollResult = await stalePollForA;

        TimelineState finalState = timeline.SetFilter(null);
        Assert.Equal(referenceB.Items.Count, bState.Items.Count);
        Assert.Equal(referenceB.Items.Count, finalState.Items.Count);
        Assert.Equal(
            [.. referenceB.Items.Select(item => item.Id).OrderBy(id => id)],
            [.. finalState.Items.Select(item => item.Id).OrderBy(id => id)]);
        // The stale call's own return value must also reflect the current (B) state, never A's --
        // proving it was discarded rather than merged.
        Assert.Equal(referenceB.Items.Count, stalePollResult.Items.Count);

        await timeline.MarkAllReadAsync(cancellationToken);

        SprintTimelineViewModel freshB = new(environment.Application, catalog);
        TimelineState freshBState = await freshB.InitializeAsync(
            projectId, environment.ProjectRoot, sprintB.Value, cancellationToken);
        Assert.Equal(0, freshBState.UnreadCount);

        // Sprint A's own watermark must remain exactly what it was before the race (still fully
        // unread, since A's own new event was never itself marked read) -- untouched by both the
        // discarded stale fetch and by marking B read.
        SprintTimelineViewModel freshA = new(environment.Application, catalog);
        TimelineState freshAState = await freshA.InitializeAsync(
            projectId, environment.ProjectRoot, sprintA.Value, cancellationToken);
        Assert.Equal(referenceA.Items.Count, freshAState.UnreadCount);
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
