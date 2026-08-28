using System.Globalization;
using Forge.Application;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>One rendered timeline row (plan section 4.3). <see cref="Unread"/> and
/// <see cref="CopyText"/> are computed once here so the page never re-derives either from raw
/// <see cref="SprintTimelineItem"/> fields itself.</summary>
/// <remarks><c>Payload</c> is ADR 0059/0060/0061's already-redacted structured payload, carried
/// through verbatim. <see cref="TimelineCardProjector"/> now turns it into the stat chips and
/// per-file/per-call detail rows the sprint workspace renders (finding D1 of the desktop
/// design-parity review); the three slices that produced the data deliberately shipped without it.
/// <see cref="MessageText"/> still carries the localized one-line summary for an
/// <c>AttemptDiffRecorded</c>, <c>AttemptToolUseRecorded</c>, or <c>AttemptUsageRecorded</c> item,
/// identical to the CLI's, because it resolves that item's own <c>workflow.attempt_*_recorded</c>
/// template from the event's flat arguments -- the card adds the structure that sentence cannot
/// carry, it never replaces it.
/// <para><see cref="Sequence"/> is <see cref="SprintTimelineItem.Sequence"/> -- the same dense
/// <see cref="WorkflowEvent.Sequence"/> ADR 0058's <c>AvailableActionTarget.TimelineSequence</c>
/// points at, so <see cref="TimelineGateLinks"/> can place a gate decision beside the very event
/// that requested it with no second lookup.</para></remarks>
public sealed record TimelineItemView(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    string ActorText,
    string MessageText,
    IReadOnlyDictionary<string, string?> Arguments,
    Guid? CorrelationId,
    Guid? CausationId,
    bool Unread,
    string CopyText,
    WorkflowEventPayload? Payload = null);

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
/// <remarks>
/// PR #99 round-2 review, critical finding: this instance is shared and long-lived (one per
/// <c>SprintWorkspaceViewModel</c>, reused across every sprint the workspace ever opens -- see
/// <c>WorkspaceShellPage.SprintWorkspace.cs</c>'s single <c>sprintWorkspace</c> field), while the 15s
/// timeline poll deliberately runs its Host fetch outside the shell's mutation guard (round-1 finding
/// 1). Without protection, that leaves <see cref="cursor"/>/<see cref="loaded"/>'s read-fetch-write
/// concurrently reachable from the poll tick, "Load more", <c>RefreshAllAsync</c>'s post-mutation
/// refresh, and <see cref="InitializeAsync"/> itself (sprint navigation reuses this same instance
/// rather than constructing a fresh one) -- two overlapping fetches duplicate items and over-count
/// unread, and a fetch that outlives a navigation to a different sprint can append the wrong sprint's
/// items into the new sprint's state and persist a wrong, durable watermark. <see cref="gate"/>
/// serializes only the brief read/write of this type's own fields (never the Host round-trip itself,
/// so fetches still run concurrently and navigation is never blocked waiting on a stale one).
/// <see cref="generation"/> is bumped by every <see cref="InitializeAsync"/> call and snapshotted by
/// every fetch before it starts; a fetch whose generation or starting cursor no longer matches this
/// instance's current state when it completes belongs to a superseded sprint or lost a race to
/// another concurrent fetch, and is discarded without touching any field -- the caller simply
/// receives whatever the winning call (or the newer sprint) already established.
/// </remarks>
public sealed class SprintTimelineViewModel(ForgeApplication application, ProjectCatalogStore catalog, SurfaceText text)
    : IDisposable
{
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly ProjectCatalogStore catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly SurfaceText text = text ?? throw new ArgumentNullException(nameof(text));
    private readonly List<SprintTimelineItem> loaded = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private Guid projectId;
    private Guid sprintId;
    private string? cursor;
    private bool hasMore;
    private long readWatermarkSequence;
    private string? filterType;
    private int generation;

    /// <summary>Resets all paging/unread state for a newly opened sprint and loads the first page.
    /// Must be called once before <see cref="LoadMoreAsync"/>/<see cref="SetFilter"/> for a given
    /// sprint (a route change to a different sprint calls this again).</summary>
    public async Task<TimelineState> InitializeAsync(
        Guid projectIdValue, string? projectRoot, Guid sprintIdValue, CancellationToken cancellationToken)
    {
        int myGeneration;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Bumping generation here immediately invalidates every fetch already in flight for
            // whatever sprint this instance previously represented -- see the type-level remarks.
            myGeneration = ++generation;
            projectId = projectIdValue;
            sprintId = sprintIdValue;
            loaded.Clear();
            cursor = null;
            hasMore = false;
            filterType = null;
        }
        finally
        {
            gate.Release();
        }

        long watermark = await LoadWatermarkAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A second, later InitializeAsync call (e.g. a rapid double navigation) may have already
            // reset this instance again while the watermark read above was outstanding -- only the
            // newest call may ever apply its own state.
            if (myGeneration == generation)
            {
                readWatermarkSequence = watermark;
            }
        }
        finally
        {
            gate.Release();
        }

        return await FetchAsync(projectRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the next page from the current cursor -- used identically for an explicit
    /// "load more" click and for a bounded-interval poll while the page is visible (plan 12.3: "new
    /// items appear without manual refresh").</summary>
    public Task<TimelineState> LoadMoreAsync(string? projectRoot, CancellationToken cancellationToken) =>
        FetchAsync(projectRoot, cancellationToken);

    /// <summary>The single fetch-and-apply step both <see cref="InitializeAsync"/> and
    /// <see cref="LoadMoreAsync"/> use. Snapshots the sprint identity and cursor to fetch from, runs
    /// the Host round-trip with no lock held (so a slow fetch never blocks navigation or another
    /// fetch), then re-validates the snapshot against this instance's current state before applying
    /// the result -- see the type-level remarks for exactly which races this closes.</summary>
    private async Task<TimelineState> FetchAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        int myGeneration;
        Guid mySprintId;
        string? myCursor;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            myGeneration = generation;
            mySprintId = sprintId;
            myCursor = cursor;
        }
        finally
        {
            gate.Release();
        }

        SprintTimelinePage page = await application
            .GetSprintTimelineAsync(projectRoot, mySprintId, myCursor, cancellationToken)
            .ConfigureAwait(false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != myGeneration || !string.Equals(cursor, myCursor, StringComparison.Ordinal))
            {
                // Either a newer InitializeAsync switched this instance to a different sprint (or
                // reloaded the same one), or another concurrent fetch already advanced the cursor
                // first -- applying this page now would duplicate items already appended by the
                // winner, or mix a stale sprint's items into the current one. Discard: BuildState()
                // already reflects whichever fetch actually won.
                return BuildState();
            }

            cursor = page.Cursor;
            // A full page (the projector's own bound) means real, already-known backlog remains
            // beyond what was just loaded; a partial or empty page means this call has caught up to
            // "now" -- the next new item, if any, arrives through a later poll rather than a "load
            // more" click.
            hasMore = page.Items.Count == SprintTimelineProjector.MaxItemsPerPage;
            loaded.AddRange(page.Items);
            return BuildState();
        }
        finally
        {
            gate.Release();
        }
    }

    public TimelineState SetFilter(string? type)
    {
        gate.Wait();
        try
        {
            filterType = string.IsNullOrEmpty(type) ? null : type;
            return BuildState();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Advances the persisted read watermark to the newest loaded item -- a no-op when
    /// nothing loaded is newer than what was already recorded (never rewinds "read" state).</summary>
    public async Task MarkAllReadAsync(CancellationToken cancellationToken)
    {
        Guid myProjectId;
        Guid mySprintId;
        long maxSequence;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loaded.Count == 0)
            {
                return;
            }

            maxSequence = loaded.Max(item => item.Sequence);
            if (maxSequence <= readWatermarkSequence)
            {
                return;
            }

            readWatermarkSequence = maxSequence;
            myProjectId = projectId;
            mySprintId = sprintId;
        }
        finally
        {
            gate.Release();
        }

        // The catalog write itself runs unlocked, after capturing projectId/sprintId/maxSequence as
        // local snapshots -- so even if a concurrent InitializeAsync switches this instance to a
        // different sprint while this write is outstanding, it still durably targets the sprint the
        // user actually clicked "mark all read" for, never whatever sprint is current by the time the
        // write completes.
        await catalog.SetTimelineWatermarkAsync(myProjectId, mySprintId, maxSequence, cancellationToken).ConfigureAwait(false);
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

    /// <summary>ADR 0054's message-composer draft -- a PARALLEL slot to <see cref="LoadDraftAsync"/>'s
    /// rewind-reason draft, not a reuse of it (see <see cref="ProjectCatalogEntry.MessageDrafts"/>'s
    /// own remarks).</summary>
    public async Task<string?> LoadMessageDraftAsync(CancellationToken cancellationToken)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        ProjectCatalogEntry? entry = listing.Entries.FirstOrDefault(candidate => candidate.ProjectId == projectId);
        return entry?.MessageDrafts?.GetValueOrDefault(sprintId.ToString("D"));
    }

    public Task<ProjectCatalogResult> SaveMessageDraftAsync(string? draft, CancellationToken cancellationToken) =>
        catalog.SetSprintMessageDraftAsync(projectId, sprintId, draft, cancellationToken);

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

    /// <summary>Releases <see cref="gate"/>. This instance is owned for the lifetime of its
    /// <c>SprintWorkspaceViewModel</c>, which is itself app-lifetime -- disposal exists only to
    /// satisfy the "owns a disposable field" analysis rule, not because a real leak is reachable in
    /// practice.</summary>
    public void Dispose() => gate.Dispose();

    private TimelineItemView ToView(SprintTimelineItem item)
    {
        string actorText = SurfaceFormatting.Machine(item.Actor);
        // Plan 12.3/12.6: resolved through the same neutral TimelineMessageFormatter CliApplication.
        // WriteTimeline also calls, so both surfaces render identical localized text for the same
        // event (parity) instead of either one showing the raw `workflow.*`/`routing.*` journal key.
        string messageText = TimelineMessageFormatter.Format(text, item.MessageKey, item.Arguments);
        bool unread = item.Sequence > readWatermarkSequence;
        string copyText = string.Create(
            CultureInfo.InvariantCulture,
            $"{item.OccurredAt:O} [{item.Type}/{actorText}] {messageText} " +
                $"({item.TargetKind}:{item.TargetId})");
        return new(
            item.Id, item.Sequence, item.OccurredAt, item.Type, actorText, messageText, item.Arguments,
            item.CorrelationId, item.CausationId, unread, copyText, item.Payload);
    }
}
