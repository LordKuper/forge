using Forge.Application;
using Forge.Desktop.Presentation;

namespace Forge.UnitTests;

/// <summary>PR #105 review findings 3/4: <see cref="ScrollPositionPersistCoordinator"/> carries the
/// debounce/ordering/success-guarantee logic for the sprint workspace's scroll-position persistence,
/// factored out of <c>WorkspaceShellPage.SprintWorkspace.cs</c> -- a MAUI page this suite cannot
/// instantiate (see <c>SurfaceParityTests</c>'s own remarks on why Desktop UI code is pinned via
/// source-text assertions instead) -- specifically so this logic is unit-testable.</summary>
public sealed class ScrollPositionPersistCoordinatorTests
{
    private static ProjectCatalogResult Ok() => new(true, null, DiagnosticCodes.None);

    /// <summary>Review finding 3(a): simulates several <c>ScrollView.Scrolled</c> events landing
    /// before the MAUI-side debounce timer fires -- <see cref="ScrollPositionPersistCoordinator.RecordScroll"/>
    /// is the cheap, synchronous per-event step; only one <see cref="ScrollPositionPersistCoordinator.FlushAsync"/>
    /// call ever follows, simulating the single debounce tick. Exactly one durable write must result,
    /// carrying the LAST recorded value, not the first or a stale intermediate one.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RapidScrollEventsWithinTheDebounceWindowPersistOnlyTheLastValue()
    {
        List<double> savedPositions = [];
        ScrollPositionPersistCoordinator coordinator = new((_, _, position, _) =>
        {
            savedPositions.Add(position);
            return Task.FromResult(Ok());
        });
        Guid projectId = Guid.NewGuid();
        Guid sprintId = Guid.NewGuid();

        coordinator.RecordScroll(sprintId, 10);
        coordinator.RecordScroll(sprintId, 55);
        coordinator.RecordScroll(sprintId, 128.5);

        ScrollPersistOutcome outcome =
            await coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Applied);
        Assert.Equal(128.5, Assert.Single(savedPositions));
        Assert.Equal(128.5, coordinator.LastPersistedPositions[sprintId]);
    }

    /// <summary>Review finding 3(b): the "flush-on-navigate-away" case is just a direct
    /// <see cref="ScrollPositionPersistCoordinator.FlushAsync"/> call issued outside the debounce
    /// timer's own tick -- the coordinator itself never introduces an artificial delay (that
    /// responsibility is entirely the caller's MAUI-side <c>IDispatcherTimer</c>, which this class
    /// deliberately knows nothing about), so a pending value is written the moment it is asked for,
    /// with nothing to wait on.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task NavigatingAwayFlushesImmediatelyWithoutWaitingForADebounce()
    {
        List<double> savedPositions = [];
        ScrollPositionPersistCoordinator coordinator = new((_, _, position, _) =>
        {
            savedPositions.Add(position);
            return Task.FromResult(Ok());
        });
        Guid projectId = Guid.NewGuid();
        Guid sprintId = Guid.NewGuid();
        coordinator.RecordScroll(sprintId, 77);

        ScrollPersistOutcome outcome =
            await coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);

        Assert.True(outcome.Applied);
        Assert.Equal(77, Assert.Single(savedPositions));
    }

    /// <summary>Review finding 4(a): two flushes are issued in order (a "stale" first write, then a
    /// "fresh" second write for the same sprint after a further scroll), but the FIRST write's
    /// underlying save is held open (simulating <see cref="System.Threading.SemaphoreSlim"/>'s lack
    /// of a FIFO guarantee letting it complete after the second) until after the second has already
    /// applied. The first write's late, successful completion must be discarded rather than allowed
    /// to overwrite the fresher persisted value.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AStaleLateCompletingWriteCannotClobberANewerPersistedValue()
    {
        Guid projectId = Guid.NewGuid();
        Guid sprintId = Guid.NewGuid();
        TaskCompletionSource<ProjectCatalogResult> staleWriteGate = new();
        int callCount = 0;
        ScrollPositionPersistCoordinator coordinator = new((_, _, _, _) =>
        {
            callCount++;
            return callCount == 1 ? staleWriteGate.Task : Task.FromResult(Ok());
        });

        coordinator.RecordScroll(sprintId, 10);
        Task<ScrollPersistOutcome> staleFlush =
            coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);

        coordinator.RecordScroll(sprintId, 999);
        ScrollPersistOutcome freshOutcome =
            await coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);
        Assert.True(freshOutcome.Applied);
        Assert.Equal(999, coordinator.LastPersistedPositions[sprintId]);

        // The stale write now completes successfully, after the fresh one already applied.
        staleWriteGate.SetResult(Ok());
        ScrollPersistOutcome staleOutcome = await staleFlush;

        Assert.True(staleOutcome.Succeeded);
        Assert.False(staleOutcome.Applied);
        Assert.Equal(999, coordinator.LastPersistedPositions[sprintId]);
    }

    /// <summary>Review finding 4(b): <see cref="ScrollPositionPersistCoordinator.LastPersistedPositions"/>
    /// must reflect only a CONFIRMED successful write, never be updated optimistically before the
    /// save call returns.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LastPersistedIsOnlyUpdatedAfterAConfirmedSuccessfulWrite()
    {
        Guid projectId = Guid.NewGuid();
        Guid sprintId = Guid.NewGuid();
        ScrollPositionPersistCoordinator coordinator = new((_, _, _, _) =>
            Task.FromResult(ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogUnreadable)));
        coordinator.RecordScroll(sprintId, 42);

        ScrollPersistOutcome outcome =
            await coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Applied);
        Assert.Equal(DiagnosticCodes.ProjectCatalogUnreadable, outcome.DiagnosticCode);
        Assert.False(coordinator.LastPersistedPositions.ContainsKey(sprintId));
    }

    /// <summary>A flush with nothing recorded, or one whose pending value already matches what is
    /// durably persisted (e.g. right after <see cref="ScrollPositionPersistCoordinator.Seed"/>
    /// restores the catalog's own value), must not issue a write at all.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FlushIsANoOpWhenNothingIsPendingOrThePendingValueAlreadyMatchesWhatIsPersisted()
    {
        int saveCallCount = 0;
        ScrollPositionPersistCoordinator coordinator = new((_, _, _, _) =>
        {
            saveCallCount++;
            return Task.FromResult(Ok());
        });
        Guid projectId = Guid.NewGuid();
        Guid sprintId = Guid.NewGuid();

        ScrollPersistOutcome emptyOutcome =
            await coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);
        Assert.False(emptyOutcome.Applied);

        coordinator.Seed(sprintId, 15);
        ScrollPersistOutcome unchangedOutcome =
            await coordinator.FlushAsync(projectId, sprintId, TestContext.Current.CancellationToken);
        Assert.False(unchangedOutcome.Applied);
        Assert.Equal(0, saveCallCount);
    }
}
