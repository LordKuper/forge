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
/// Plan 12.1 final-sweep gap 2, PR #105 review findings 3/4: the ordering/success-guarantee half of
/// the sprint workspace's scroll-position persistence, factored out of
/// <c>WorkspaceShellPage.SprintWorkspace.cs</c> into this neutral, portable class (AGENTS.md's
/// OS-adapter boundary -- <c>Forge.Desktop.Presentation</c> has no MAUI dependency, unlike
/// <c>Forge.Desktop</c>) so it is unit-testable without a live MAUI page. Two responsibilities:
///
/// 1. <see cref="RecordScroll"/> just remembers the latest in-memory offset per sprint. Combined with
/// the caller debouncing *when* it flushes (an <c>IDispatcherTimer</c> restarted on every
/// <c>ScrollView.Scrolled</c> event in <c>Forge.Desktop</c> -- MAUI-specific scheduling this class
/// does not need to know about), a rapid sequence of scroll events before the debounce fires ends up
/// persisting exactly once, with the last recorded value (review finding 3).
///
/// 2. <see cref="FlushAsync"/> calls are not guaranteed to complete in the order they were issued (a
/// debounce firing and an explicit navigate-away flush can race, and the underlying
/// <see cref="ProjectCatalogStore"/> write takes a <see cref="System.Threading.SemaphoreSlim"/> that
/// makes no FIFO promise). Every flush is stamped with a per-sprint sequence number when it is
/// issued, not when it completes, and a write is only applied -- <see cref="LastPersistedPositions"/>
/// updated -- if its stamp is still the newest one applied for that sprint; a stale write that
/// completes late is dropped instead of being allowed to overwrite a fresher persisted value (review
/// finding 4a).
/// <see cref="LastPersistedPositions"/> is only updated once the write is confirmed to have succeeded,
/// never optimistically before the call returns (review finding 4b).
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

    /// <summary>Every sprint's most recently confirmed-durable offset -- only ever updated by a
    /// successful, non-superseded <see cref="FlushAsync"/> (review finding 4b).</summary>
    public IReadOnlyDictionary<Guid, double> LastPersistedPositions => lastPersistedPositions;

    /// <summary>Seeds both the pending and last-persisted state for a sprint from a value already
    /// known to be durable -- the catalog's own value, read back on the first render of a sprint since
    /// the app started. Prevents the very next <see cref="FlushAsync"/> from re-writing a value that
    /// is already exactly what is on disk.</summary>
    public void Seed(Guid sprintId, double position)
    {
        pendingPositions[sprintId] = position;
        lastPersistedPositions[sprintId] = position;
    }

    /// <summary>Records the latest in-memory scroll offset for <paramref name="sprintId"/>. Cheap and
    /// synchronous -- called on every single <c>ScrollView.Scrolled</c> event, never gated by the
    /// debounce itself.</summary>
    public void RecordScroll(Guid sprintId, double position) => pendingPositions[sprintId] = position;

    /// <summary>The in-session cached offset for <paramref name="sprintId"/>, if any -- used to
    /// restore scroll position on in-session navigation back to an already-visited sprint without a
    /// catalog round-trip.</summary>
    public bool TryGetPending(Guid sprintId, out double position) => pendingPositions.TryGetValue(sprintId, out position);

    /// <summary>Issues a durable write of <paramref name="sprintId"/>'s current pending offset,
    /// applying the ordering/success guarantees described on the type itself. A no-op (no write
    /// issued at all) when nothing is pending or the pending value already matches what was last
    /// persisted.</summary>
    public async Task<ScrollPersistOutcome> FlushAsync(Guid projectId, Guid sprintId, CancellationToken cancellationToken)
    {
        if (!pendingPositions.TryGetValue(sprintId, out double position))
        {
            return ScrollPersistOutcome.NoOp;
        }

        if (lastPersistedPositions.TryGetValue(sprintId, out double alreadyPersisted) && alreadyPersisted == position)
        {
            return ScrollPersistOutcome.NoOp;
        }

        long stamp = nextSequence.TryGetValue(sprintId, out long current) ? current + 1 : 1;
        nextSequence[sprintId] = stamp;

        ProjectCatalogResult result = await saveAsync(projectId, sprintId, position, cancellationToken).ConfigureAwait(false);

        long highest = highestAppliedSequence.TryGetValue(sprintId, out long existingHighest) ? existingHighest : 0;
        if (stamp <= highest)
        {
            // A newer flush for this sprint already applied while this one was in flight -- this
            // offset is stale even if the write itself succeeded, so it must never overwrite the
            // fresher persisted value (review finding 4a).
            return new(result.Succeeded, false, result.DiagnosticCode);
        }

        if (result.Succeeded)
        {
            highestAppliedSequence[sprintId] = stamp;
            lastPersistedPositions[sprintId] = position;
        }

        return new(result.Succeeded, true, result.DiagnosticCode);
    }
}
