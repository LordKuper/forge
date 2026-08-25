using Forge.Application;

namespace Forge.Desktop.Presentation;

/// <summary>One <see cref="ScrollPositionPersistCoordinator.FlushAsync"/> call's outcome.
/// <see cref="Applied"/> is <see langword="false"/> when nothing needed writing (no pending value, or
/// the pending value already matches what was last persisted) or when the write was superseded by a
/// newer one that already completed (PR #105 review finding 4a) -- in both cases the caller has
/// nothing new to report.</summary>
public readonly record struct ScrollPersistOutcome(bool Succeeded, bool Applied, string DiagnosticCode)
{
    public static ScrollPersistOutcome NoOp { get; } = new(true, false, DiagnosticCodes.None);
}

/// <summary>
/// Plan 12.1 final-sweep gap 2, PR #105 review findings 3/4 (round 1) and 1/2 (round 2): the
/// ordering/success-guarantee half of the sprint workspace's scroll-position persistence, factored
/// out of <c>WorkspaceShellPage.SprintWorkspace.cs</c> into this neutral, portable class (AGENTS.md's
/// OS-adapter boundary -- <c>Forge.Desktop.Presentation</c> has no MAUI dependency, unlike
/// <c>Forge.Desktop</c>) so it is unit-testable without a live MAUI page. Three responsibilities:
///
/// 1. <see cref="RecordScroll"/> just remembers the latest in-memory offset per sprint. Combined with
/// the caller debouncing *when* it flushes (an <c>IDispatcherTimer</c> restarted on every
/// <c>ScrollView.Scrolled</c> event in <c>Forge.Desktop</c> -- MAUI-specific scheduling this class
/// does not need to know about), a rapid sequence of scroll events before the debounce fires ends up
/// persisting exactly once, with the last recorded value (round 1 finding 3).
///
/// 2. <see cref="FlushAsync"/> calls are not guaranteed to complete in the order they were issued (a
/// debounce firing and an explicit navigate-away flush can race, and the underlying
/// <see cref="ProjectCatalogStore"/> write takes a <see cref="System.Threading.SemaphoreSlim"/> that
/// makes no FIFO promise). Every flush is stamped with a per-sprint sequence number when it is
/// issued, not when it completes, and a write is only applied -- <see cref="LastPersistedPositions"/>
/// updated -- if its stamp is still the newest one applied for that sprint; a stale write that
/// completes late is dropped instead of being allowed to overwrite a fresher persisted value (round 1
/// finding 4a). <see cref="LastPersistedPositions"/> is only updated once the write is confirmed to
/// have succeeded, never optimistically before the call returns (round 1 finding 4b).
///
/// 3. That stamp comparison alone only orders THIS coordinator's own in-memory bookkeeping -- it
/// never stopped a stale write's actual <c>saveAsync</c> call from physically completing, and writing
/// to disk, after a fresher one already did (the store's write semaphore has no FIFO promise, per
/// point 2 above). Round 2 finding 1: when a completed write turns out to be stale, this coordinator
/// now re-issues the currently-known-durable value as a fresh, freshly stamped write of its own
/// (<see cref="IssueWriteAsync"/> recurses), so the file converges on whatever this coordinator
/// believes is durable instead of being left holding the stale write's own value. The recursive write
/// goes through the exact same staleness check, so it is itself immune to yet another concurrent
/// write racing ahead of it.
/// </summary>
public sealed class ScrollPositionPersistCoordinator(
    Func<Guid, Guid, double, CancellationToken, Task<ProjectCatalogResult>> saveAsync)
{
    private readonly Func<Guid, Guid, double, CancellationToken, Task<ProjectCatalogResult>> saveAsync =
        saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
    private readonly Dictionary<Guid, double> pendingPositions = [];
    private readonly Dictionary<Guid, double> lastPersistedPositions = [];
    private readonly Dictionary<Guid, long> nextSequence = [];
    private readonly Dictionary<Guid, long> highestAppliedSequence = [];

    /// <summary>Guards all four dictionaries above (round 2 finding 2): every
    /// <see cref="FlushAsync"/>/<see cref="IssueWriteAsync"/> continuation resumes on a thread-pool
    /// thread after its <c>ConfigureAwait(false)</c> save call -- concurrently with
    /// <see cref="RecordScroll"/>/<see cref="TryGetPending"/> on the caller's UI thread and with each
    /// other, since two overlapping flushes for the same sprint is the exact case point 2 above
    /// exists to handle, not a rare edge case. Plain <see cref="Dictionary{TKey,TValue}"/> supports no
    /// concurrent writer -- an unsynchronized insert race here can tear the table's internal
    /// structure, corrupting it or hanging a later lookup. Held only for the handful of synchronous
    /// statements that read or write these dictionaries, never across an <c>await</c>.</summary>
    private readonly object syncRoot = new();

    /// <summary>Every sprint's most recently confirmed-durable offset -- only ever updated by a
    /// successful, non-superseded <see cref="FlushAsync"/> (round 1 finding 4b). Snapshotted under
    /// <see cref="syncRoot"/> so a caller enumerating or indexing the result never races a concurrent
    /// flush's own write to the backing dictionary.</summary>
    public IReadOnlyDictionary<Guid, double> LastPersistedPositions
    {
        get
        {
            lock (syncRoot)
            {
                return new Dictionary<Guid, double>(lastPersistedPositions);
            }
        }
    }

    /// <summary>Seeds both the pending and last-persisted state for a sprint from a value already
    /// known to be durable -- the catalog's own value, read back on the first render of a sprint since
    /// the app started. Prevents the very next <see cref="FlushAsync"/> from re-writing a value that
    /// is already exactly what is on disk.</summary>
    public void Seed(Guid sprintId, double position)
    {
        lock (syncRoot)
        {
            pendingPositions[sprintId] = position;
            lastPersistedPositions[sprintId] = position;
        }
    }

    /// <summary>Records the latest in-memory scroll offset for <paramref name="sprintId"/>. Cheap and
    /// synchronous -- called on every single <c>ScrollView.Scrolled</c> event, never gated by the
    /// debounce itself.</summary>
    public void RecordScroll(Guid sprintId, double position)
    {
        lock (syncRoot)
        {
            pendingPositions[sprintId] = position;
        }
    }

    /// <summary>The in-session cached offset for <paramref name="sprintId"/>, if any -- used to
    /// restore scroll position on in-session navigation back to an already-visited sprint without a
    /// catalog round-trip.</summary>
    public bool TryGetPending(Guid sprintId, out double position)
    {
        lock (syncRoot)
        {
            return pendingPositions.TryGetValue(sprintId, out position);
        }
    }

    /// <summary>Issues a durable write of <paramref name="sprintId"/>'s current pending offset,
    /// applying the ordering/success guarantees described on the type itself. A no-op (no write
    /// issued at all) when nothing is pending or the pending value already matches what was last
    /// persisted.</summary>
    public Task<ScrollPersistOutcome> FlushAsync(Guid projectId, Guid sprintId, CancellationToken cancellationToken)
    {
        double position;
        lock (syncRoot)
        {
            if (!pendingPositions.TryGetValue(sprintId, out position))
            {
                return Task.FromResult(ScrollPersistOutcome.NoOp);
            }

            if (lastPersistedPositions.TryGetValue(sprintId, out double alreadyPersisted) && alreadyPersisted == position)
            {
                return Task.FromResult(ScrollPersistOutcome.NoOp);
            }
        }

        return IssueWriteAsync(projectId, sprintId, position, cancellationToken);
    }

    private async Task<ScrollPersistOutcome> IssueWriteAsync(
        Guid projectId, Guid sprintId, double position, CancellationToken cancellationToken)
    {
        long stamp;
        lock (syncRoot)
        {
            stamp = nextSequence.TryGetValue(sprintId, out long current) ? current + 1 : 1;
            nextSequence[sprintId] = stamp;
        }

        ProjectCatalogResult result = await saveAsync(projectId, sprintId, position, cancellationToken).ConfigureAwait(false);

        bool stale;
        double correctiveValue = position;
        lock (syncRoot)
        {
            long highest = highestAppliedSequence.TryGetValue(sprintId, out long existingHighest) ? existingHighest : 0;
            stale = stamp <= highest;
            if (!stale)
            {
                if (result.Succeeded)
                {
                    highestAppliedSequence[sprintId] = stamp;
                    lastPersistedPositions[sprintId] = position;
                }
            }
            else if (lastPersistedPositions.TryGetValue(sprintId, out double persisted))
            {
                // A newer flush for this sprint already applied while this one was in flight -- this
                // offset is stale even if the write itself succeeded, so it must never be recorded as
                // the persisted value (round 1 finding 4a).
                correctiveValue = persisted;
            }
        }

        if (stale)
        {
            if (result.Succeeded && correctiveValue != position)
            {
                // Round 2 finding 1: this write's own saveAsync call just succeeded -- meaning it
                // physically reached disk -- AFTER a fresher one already had, since the store's write
                // semaphore makes no FIFO promise. Left alone, the file would now hold this stale
                // `position` as its last word even though every other piece of state here already
                // moved on. Re-issuing the currently-known-durable value (never this stale one)
                // through the same staleness-checked path corrects the file instead of merely the
                // in-memory bookkeeping. A failed write never reaches disk at all (ProjectCatalogStore
                // returns without writing on any validation/read failure), so no correction is needed
                // when result.Succeeded is false.
                await IssueWriteAsync(projectId, sprintId, correctiveValue, cancellationToken).ConfigureAwait(false);
            }

            return new(result.Succeeded, false, result.DiagnosticCode);
        }

        return new(result.Succeeded, true, result.DiagnosticCode);
    }
}
