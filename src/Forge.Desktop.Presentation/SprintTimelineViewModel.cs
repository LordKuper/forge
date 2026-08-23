using System.Globalization;
using Forge.Application;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>One rendered timeline row (plan section 4.3). <see cref="Unread"/> and
/// <see cref="CopyText"/> are computed once here so the page never re-derives either from raw
/// <see cref="SprintTimelineItem"/> fields itself.</summary>
public sealed record TimelineItemView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Type,
    string ActorText,
    string MessageText,
    IReadOnlyDictionary<string, string?> Arguments,
    Guid? CorrelationId,
    Guid? CausationId,
    bool Unread,
    string CopyText);

/// <summary>The timeline pane's full render state: the filtered/ordered rows, whether a genuinely
/// larger backlog exists beyond what is loaded (<see cref="HasMore"/>), the unread count over every
/// loaded item regardless of the active filter, and the filter options actually observed so far.
/// </summary>
public sealed record TimelineState(
    IReadOnlyList<TimelineItemView> Items,
    bool HasMore,
    int UnreadCount,
    string? ActiveFilterType,
    IReadOnlyList<string> AvailableFilterTypes);

/// <summary>
/// Plan section 4.3/12.3's timeline: incremental cursor-based loading, unread position tracking,
/// filtering by item type, copy-to-clipboard text, and technical-detail data -- all sourced from
/// Slice 4's already-redacted, already-bounded <see cref="SprintTimelineItem"/> contract. A "load
/// more" click and this page's own periodic poll (while visible) are the same operation: both call
/// <see cref="LoadMoreAsync"/> against the same advancing cursor, so a poll simply picks up whatever
/// is newly appended since the last call -- there is no separate "live" code path to keep in sync.
/// </summary>
/// <remarks>
/// Unread tracking persists the maximum <see cref="SprintTimelineItem.Sequence"/> the user has
/// acknowledged -- the underlying journal's own dense, strictly increasing per-sprint counter (PR
/// #99 review finding 4). An earlier revision compared <see cref="SprintTimelineItem.OccurredAt"/>
/// (UTC ticks) instead, on the premise that every event's timestamp is itself strictly increasing;
/// that premise does not hold (<see cref="IClock.UtcNow"/> is not guaranteed to advance between two
/// events appended moments apart -- ties are already documented as reachable in this codebase,
/// <c>SprintScheduler.cs</c>'s own remarks on <c>RecordedAt</c>), so a tie could leave a genuinely new
/// item born already-read. <see cref="SprintTimelineItem.Sequence"/> has no such gap: it is the exact
/// per-item value <see cref="SprintTimelineCursor"/>'s own opaque watermark already advances by.
/// </remarks>
public sealed class SprintTimelineViewModel(ForgeApplication application, ProjectCatalogStore catalog)
{
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly ProjectCatalogStore catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly List<SprintTimelineItem> loaded = [];
    private Guid projectId;
    private Guid sprintId;
    private string? cursor;
    private bool hasMore;
    private long readWatermarkSequence;
    private string? filterType;

    /// <summary>Resets all paging/unread state for a newly opened sprint and loads the first page.
    /// Must be called once before <see cref="LoadMoreAsync"/>/<see cref="SetFilter"/> for a given
    /// sprint (a route change to a different sprint calls this again).</summary>
    public async Task<TimelineState> InitializeAsync(
        Guid projectIdValue, string? projectRoot, Guid sprintIdValue, CancellationToken cancellationToken)
    {
        projectId = projectIdValue;
        sprintId = sprintIdValue;
        loaded.Clear();
        cursor = null;
        hasMore = false;
        filterType = null;
        readWatermarkSequence = await LoadWatermarkAsync(cancellationToken).ConfigureAwait(false);
        return await LoadMoreAsync(projectRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the next page from the current cursor -- used identically for an explicit
    /// "load more" click and for a bounded-interval poll while the page is visible (plan 12.3: "new
    /// items appear without manual refresh").</summary>
    public async Task<TimelineState> LoadMoreAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        SprintTimelinePage page = await application
            .GetSprintTimelineAsync(projectRoot, sprintId, cursor, cancellationToken)
            .ConfigureAwait(false);
        cursor = page.Cursor;
        // A full page (the projector's own bound) means real, already-known backlog remains beyond
        // what was just loaded; a partial or empty page means this call has caught up to "now" --
        // the next new item, if any, arrives through a later poll rather than a "load more" click.
        hasMore = page.Items.Count == SprintTimelineProjector.MaxItemsPerPage;
        loaded.AddRange(page.Items);
        return BuildState();
    }

    public TimelineState SetFilter(string? type)
    {
        filterType = string.IsNullOrEmpty(type) ? null : type;
        return BuildState();
    }

    /// <summary>Advances the persisted read watermark to the newest loaded item -- a no-op when
    /// nothing loaded is newer than what was already recorded (never rewinds "read" state).</summary>
    public async Task MarkAllReadAsync(CancellationToken cancellationToken)
    {
        if (loaded.Count == 0)
        {
            return;
        }

        long maxSequence = loaded.Max(item => item.Sequence);
        if (maxSequence <= readWatermarkSequence)
        {
            return;
        }

        readWatermarkSequence = maxSequence;
        await catalog.SetTimelineWatermarkAsync(projectId, sprintId, maxSequence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plan section 11 Slice 6 item 4's rewind-reason draft, restored on
    /// <see cref="InitializeAsync"/>'s caller (the sprint-workspace page reads this once after
    /// initializing the timeline, since both share the same catalog entry lookup).</summary>
    public async Task<string?> LoadDraftAsync(CancellationToken cancellationToken)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        ProjectCatalogEntry? entry = listing.Entries.FirstOrDefault(candidate => candidate.ProjectId == projectId);
        return entry?.SprintDrafts?.GetValueOrDefault(sprintId.ToString("D"));
    }

    /// <summary>Returns the full <see cref="ProjectCatalogResult"/> (not just a discarded
    /// <see cref="Task"/>) so a caller can actually surface a save failure -- notably
    /// <see cref="DiagnosticCodes.ProjectCatalogDraftTooLong"/>, which was otherwise unreachable by a
    /// user (PR #99 review finding 10).</summary>
    public Task<ProjectCatalogResult> SaveDraftAsync(string? draft, CancellationToken cancellationToken) =>
        catalog.SetSprintDraftAsync(projectId, sprintId, draft, cancellationToken);

    /// <summary>Reads the persisted watermark, an opaque <see langword="long"/> compared against
    /// <see cref="SprintTimelineItem.Sequence"/>. This field (<c>ProjectCatalogEntry.
    /// TimelineReadWatermarks</c>) was introduced in this same PR and never shipped storing
    /// <see cref="SprintTimelineItem.OccurredAt"/> ticks (PR #99 review finding 4 corrected the
    /// scheme before release) -- no migration or dual-format handling is needed. The "never read
    /// anything" default is <c>-1</c>, matching <see cref="SprintTimelineCursor.Empty"/>'s own
    /// "start from scratch" sentinel -- <c>Sequence</c> starts at <c>0</c> for a sprint's very first
    /// event, so a default of <c>0</c> would wrongly treat that item as already read.</summary>
    private async Task<long> LoadWatermarkAsync(CancellationToken cancellationToken)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        ProjectCatalogEntry? entry = listing.Entries.FirstOrDefault(candidate => candidate.ProjectId == projectId);
        return entry?.TimelineReadWatermarks?.GetValueOrDefault(sprintId.ToString("D"), -1) ?? -1;
    }

    private TimelineState BuildState()
    {
        List<string> availableTypes = [.. loaded.Select(item => item.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        IEnumerable<SprintTimelineItem> filtered = filterType is null
            ? loaded
            : loaded.Where(item => string.Equals(item.Type, filterType, StringComparison.Ordinal));
        List<TimelineItemView> items =
        [
            .. filtered
                .OrderBy(item => item.Sequence)
                .Select(item => ToView(item)),
        ];
        int unread = loaded.Count(item => item.Sequence > readWatermarkSequence);
        return new(items, hasMore, unread, filterType, availableTypes);
    }

    private TimelineItemView ToView(SprintTimelineItem item)
    {
        string actorText = SurfaceFormatting.Machine(item.Actor);
        // Matches CliApplication.WriteTimeline exactly (plan 12.6 parity): the message key is
        // rendered as the same raw machine text on both surfaces, never resolved through
        // SurfaceText.Resolve. None of the ~30 workflow.* message keys the journal actually emits
        // are registered in Messages.resx today -- resolving one would throw
        // MissingManifestResourceException, and authoring localized prose for every workflow event
        // type is a separable content task (see this slice's ADR), not something Desktop can silently
        // diverge from the CLI to work around.
        string messageText = item.MessageKey;
        bool unread = item.Sequence > readWatermarkSequence;
        string copyText = string.Create(
            CultureInfo.InvariantCulture,
            $"{item.OccurredAt:O} [{item.Type}/{actorText}] {messageText} " +
                $"({item.TargetKind}:{item.TargetId})");
        return new(
            item.Id, item.OccurredAt, item.Type, actorText, messageText, item.Arguments, item.CorrelationId,
            item.CausationId, unread, copyText);
    }
}
