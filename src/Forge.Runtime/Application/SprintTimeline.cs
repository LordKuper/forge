using System.Text.Json;
using Forge.Domain;
using Forge.Infrastructure;

namespace Forge.Application;

/// <summary>Plan section 6.3: who a <see cref="SprintTimelineItem"/>'s underlying event originates
/// from. <see cref="WorkflowEvent"/> carries no actor field of its own -- <see cref="Operator"/> is a
/// bounded, closed-form heuristic naming exactly the event types that only ever land as a
/// consequence of a human-only mutation (attempt supersession, a stop request, a committed rewind);
/// every other event type is an ordinary workflow-state consequence.</summary>
public enum TimelineActor
{
    System,
    Operator,

    /// <summary>ADR 0054: a provider-authored summary a node left for whatever runs next
    /// (<see cref="Handoff.Summary"/>), projected as its own timeline item -- neither a workflow-state
    /// consequence (<see cref="System"/>) nor a human-authored one (<see cref="Operator"/>). The
    /// specific provider/model identity is not recorded on <see cref="Handoff"/> today; the item's
    /// <see cref="SprintTimelineItem.TargetKind"/>/<see cref="SprintTimelineItem.TargetId"/> name the
    /// node that produced it instead.</summary>
    Agent,
}

/// <summary>
/// One projected timeline entry (plan section 6.3). A pure, redacted view of one durable
/// <see cref="WorkflowEvent"/> -- never a second source of truth. <see cref="Arguments"/> is
/// redacted (see <see cref="SprintTimelineProjector"/>) before this record is ever constructed, and a
/// renderer must redact again before display (plan 12.3's "redact... before persistence and again
/// before rendering"). <see cref="ArtifactReferences"/> is always empty: <c>IArtifactStore</c> remains
/// an empty marker (ADR 0048) -- nothing in this codebase produces a real artifact yet.
/// </summary>
/// <summary>ADR 0059: <see cref="Payload"/> is the durable <see cref="WorkflowEvent.Payload"/>
/// projected through unchanged — the domain type itself, not a parallel read-model copy, exactly as
/// <see cref="Arguments"/> already reuses its own domain shape. It is redacted by both passes like
/// every other field (see <see cref="SprintTimelineProjector.ToItem"/> and
/// <see cref="SprintTimelineRedaction"/>), so a payload can never bypass the redaction every string
/// field on this record goes through. <see langword="null"/> for every event type that carries no
/// structured payload, which today is all of them except
/// <see cref="WorkflowEvent.AttemptDiffRecordedType"/>.</summary>
/// <summary><see cref="Sequence"/> is the underlying <see cref="WorkflowEvent.Sequence"/> this item
/// was projected from -- a dense, strictly increasing per-sprint counter, unlike
/// <see cref="OccurredAt"/> (<see cref="IClock.UtcNow"/> is not guaranteed to advance between two
/// events appended moments apart, so ties are reachable in practice; see
/// <c>SprintTimelineViewModel</c>'s own remarks). Consumers that need a genuine ordering or
/// watermark comparison must use <see cref="Sequence"/>, never <see cref="OccurredAt"/> alone.
/// </summary>
public sealed record SprintTimelineItem(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    TimelineActor Actor,
    string TargetKind,
    string TargetId,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments,
    Guid? CorrelationId,
    Guid? CausationId,
    IReadOnlyList<string> ArtifactReferences,
    WorkflowEventPayload? Payload = null);

/// <summary>The single-sprint watermark cursor <see cref="SprintTimelineProjector"/> pages with.
/// Deliberately simpler than <see cref="ControlEventsCursor"/>'s per-sprint dictionary: this
/// projection only ever reads one sprint's own already-contiguous <see cref="WorkflowEvent.Sequence"/>
/// stream, never a cross-sprint merge, so a single watermark is exact. <see cref="SprintId"/> binds
/// the token to the exact sprint it was issued for -- each sprint's own <see cref="Watermark"/> is an
/// independent, dense counter, so without this a cursor issued for one sprint would silently (and
/// wrongly) apply to another (round 1 review of PR #97, finding 6).</summary>
public sealed record SprintTimelineCursor(string Version, Guid SprintId, long Watermark)
{
    public const string CurrentVersion = "1.0.0";

    /// <summary><see cref="SprintId"/> is <see cref="Guid.Empty"/> here deliberately: an empty
    /// cursor's <see cref="Watermark"/> of -1 means "start from scratch" regardless of which sprint
    /// is being requested, so no sprint binding is meaningful for it yet -- the next page this
    /// projects always re-encodes the cursor with the actual requested sprint id.</summary>
    public static SprintTimelineCursor Empty { get; } = new(CurrentVersion, Guid.Empty, -1);
}

/// <summary>Encodes/decodes <see cref="SprintTimelineCursor"/> as an opaque base64 token -- same
/// fail-safe decode contract as <see cref="ControlEventsCursorCodec"/>: a malformed,
/// future-versioned, or sprint-foreign token decodes to <see cref="SprintTimelineCursor.Empty"/>
/// with <see langword="false"/>, never a silent rebaseline or a silent misapplication to the wrong
/// sprint.</summary>
public static class SprintTimelineCursorCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Encode(SprintTimelineCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, Options));
    }

    /// <summary><paramref name="sprintId"/> is the sprint this cursor is about to page -- a
    /// successfully decoded token whose own <see cref="SprintTimelineCursor.SprintId"/> does not
    /// match it is treated exactly like a foreign token from a different codec version: rejected,
    /// never silently applied to the wrong sprint's watermark stream.</summary>
    public static bool TryDecode(string? token, Guid sprintId, out SprintTimelineCursor cursor)
    {
        if (string.IsNullOrEmpty(token))
        {
            cursor = SprintTimelineCursor.Empty;
            return true;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            SprintTimelineCursor? decoded = JsonSerializer.Deserialize<SprintTimelineCursor>(bytes, Options);
            if (decoded is null || decoded.Version != SprintTimelineCursor.CurrentVersion ||
                decoded.SprintId != sprintId)
            {
                cursor = SprintTimelineCursor.Empty;
                return false;
            }

            cursor = decoded;
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            cursor = SprintTimelineCursor.Empty;
            return false;
        }
    }
}

/// <summary>Plan section 6.3's versioned, cursor-paged <c>SprintTimelinePage</c>.</summary>
public sealed record SprintTimelinePage(
    string SchemaVersion,
    Guid SprintId,
    IReadOnlyList<SprintTimelineItem> Items,
    string Cursor,
    string DiagnosticCode)
{
    // 1.1.0: SprintTimelineItem gained Sequence (PR #99 review finding 4) -- additive, so a
    // pre-1.1.0 consumer that ignores unknown fields still round-trips every other field unchanged.
    // 1.2.0: SprintTimelineItem gained the optional Payload (ADR 0059) -- additive in the same
    // sense: it is null on every item a pre-1.2.0 consumer has ever seen.
    public const string ContractVersion = "1.2.0";

    public static SprintTimelinePage Empty(Guid sprintId, string? requestedCursor, string diagnosticCode)
    {
        bool valid =
            SprintTimelineCursorCodec.TryDecode(requestedCursor, sprintId, out SprintTimelineCursor decoded);
        return new(
            ContractVersion,
            sprintId,
            [],
            SprintTimelineCursorCodec.Encode(valid ? decoded : SprintTimelineCursor.Empty),
            valid ? diagnosticCode : DiagnosticCodes.ControlCursorStale);
    }
}

/// <summary>
/// Redaction pass 2 of 2 (plan 12.3, ADR 0049): reruns <see cref="SecretRedactor"/> independently
/// of pass 1 (<see cref="SprintTimelineProjector.ToItem(WorkflowEvent)"/>), applied once inside
/// <see cref="ForgeApplication.GetSprintTimelineAsync"/> -- the single method every surface (the CLI's
/// plain-text render, its <c>--json</c> output, and the Host's wire response) calls to obtain a
/// timeline page -- so a redaction gap in either pass alone still cannot leak a raw secret to any
/// rendered surface, regardless of output shape. Also closes pass 1's field-coverage gap: pass 1 only
/// ever redacts <see cref="SprintTimelineItem.Arguments"/>; this pass covers every free-text field an
/// item carries, including the ones pass 1 does not touch at all
/// (<see cref="SprintTimelineItem.MessageKey"/>, <see cref="SprintTimelineItem.Type"/>,
/// <see cref="SprintTimelineItem.TargetKind"/>, <see cref="SprintTimelineItem.TargetId"/>).
/// </summary>
public static class SprintTimelineRedaction
{
    public static SprintTimelinePage Apply(SprintTimelinePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page with { Items = [.. page.Items.Select(Apply)] };
    }

    private static SprintTimelineItem Apply(SprintTimelineItem item) =>
        item with
        {
            Type = SecretRedactor.Redact(item.Type),
            TargetKind = SecretRedactor.Redact(item.TargetKind),
            TargetId = SecretRedactor.Redact(item.TargetId),
            MessageKey = SecretRedactor.Redact(item.MessageKey),
            Arguments = item.Arguments.ToDictionary(
                entry => entry.Key,
                entry => entry.Value is null ? null : SecretRedactor.Redact(entry.Value),
                StringComparer.Ordinal),
            Payload = RedactPayload(item.Payload),
        };

    /// <summary>ADR 0059. <see cref="SecretRedactor.RedactProperties"/> cannot be used for this:
    /// handed a typed record it falls through to its own `RedactSerializable` arm, which returns an
    /// untyped dictionary rather than a <see cref="WorkflowEventPayload"/>, so the redacted result
    /// could not be projected back onto the strongly typed contract. This walks the payload's own
    /// string fields explicitly instead -- and is called by BOTH redaction passes
    /// (<see cref="SprintTimelineProjector.ToItem"/> is pass 1, <see cref="Apply(SprintTimelinePage)"/>
    /// is pass 2), so the payload is never the one field that reaches a surface through only one of
    /// them. Every new string field added to a payload sub-object MUST be added here; the two passes
    /// share this helper deliberately (they are independent in field *coverage*, not in which
    /// redactor they use -- both already share <see cref="SecretRedactor"/> itself).</summary>
    internal static WorkflowEventPayload? RedactPayload(WorkflowEventPayload? payload) =>
        payload?.Diff is not { } diff
            ? payload
            : payload with
            {
                Diff = diff with
                {
                    Files =
                    [
                        .. diff.Files.Select(file => file with
                        {
                            Path = SecretRedactor.Redact(file.Path),
                            ChangeKind = SecretRedactor.Redact(file.ChangeKind),
                        }),
                    ],
                },
            };
}

/// <summary>
/// Projects one sprint's existing append-only <see cref="WorkflowEvent"/> journal
/// (<see cref="ISprintStore.GetEventsAsync"/>) into a bounded, cursor-paged
/// <see cref="SprintTimelinePage"/> -- a read projection, never a new source of truth (plan section
/// 6.3/ADR 0043). ADR 0054 (post-release timeline gap closure) closes the two gaps ADR 0049 left
/// open: user messages are their own <see cref="WorkflowEvent.UserMessagePostedType"/> entries and
/// agent summaries are their own <see cref="WorkflowEvent.AgentSummaryRecordedType"/> entries, both in
/// this SAME journal -- so both get a dense <see cref="WorkflowEvent.Sequence"/> for free, assigned
/// atomically at append time, with no second cursor to merge and no borrowed/anchored sequence to get
/// wrong (PR #104 review, finding 1: the original design instead stamped a borrowed sequence onto the
/// separate <see cref="Handoff"/> record and anchored to "the nearest event at or before" it, which
/// could not survive a cursor advancing between the transition landing and the handoff write). The
/// <see cref="Handoff"/> store (<see cref="ISprintStore.GetHandoffsAsync"/>) is still consulted here,
/// but only for its mutable <see cref="Handoff.Superseded"/> flag -- see <see cref="MergeAndPage"/>.
/// </summary>
/// <param name="store">The sprint's durable event/handoff storage.</param>
/// <param name="maxItemsPerPage">The per-page item bound -- defaults to the production
/// <see cref="MaxItemsPerPage"/>. Overridable so a test can force a real, small page boundary (PR
/// #104 review, finding 4) without waiting for a sprint to accumulate <see cref="MaxItemsPerPage"/>
/// items.</param>
public sealed class SprintTimelineProjector(ISprintStore store, int maxItemsPerPage = SprintTimelineProjector.MaxItemsPerPage)
{
    /// <summary>A read-size bound, matching <see cref="ControlEventsReader.MaxEventsPerRead"/> --
    /// the production default for this projector's own page-size constructor parameter. Every event
    /// (including an agent summary) now carries its own real, dense, globally-unique
    /// <see cref="WorkflowEvent.Sequence"/>, so unlike the pre-redesign bound, two candidates can
    /// never tie on it and no page ever needs a tie-break to avoid a skip.</summary>
    public const int MaxItemsPerPage = 500;

    /// <summary>ADR 0054: the projected <see cref="SprintTimelineItem.Type"/> for an agent-authored
    /// summary (<see cref="Handoff.Summary"/>) -- an alias of <see cref="WorkflowEvent.AgentSummaryRecordedType"/>,
    /// kept as this projector's own public constant since it was already part of this contract before
    /// the redesign that made it a real event type.</summary>
    public const string AgentSummaryRecordedType = WorkflowEvent.AgentSummaryRecordedType;

    /// <summary>Event types that only ever land as a direct consequence of a human-only mutation
    /// (attempt supersession, a stop request, a committed rewind, a posted message) -- every other
    /// event type, including the convergence markers those same mutations append later, is an
    /// ordinary system-driven workflow consequence.</summary>
    private static readonly HashSet<string> OperatorTriggeredTypes = new(StringComparer.Ordinal)
    {
        WorkflowEvent.AttemptSupersededType,
        WorkflowEvent.AttemptStopRequestedType,
        WorkflowEvent.StageRevisionRecordedType,
        WorkflowEvent.UserMessagePostedType,
    };

    public async Task<SprintTimelinePage> CreateAsync(
        string projectRoot, Guid sprintId, string? cursorToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!SprintTimelineCursorCodec.TryDecode(cursorToken, sprintId, out SprintTimelineCursor cursor))
        {
            return SprintTimelinePage.Empty(sprintId, cursorToken, DiagnosticCodes.ControlCursorStale);
        }

        SprintId id = new(sprintId);
        IReadOnlyList<WorkflowEvent> events =
            await store.GetEventsAsync(projectRoot, id, cancellationToken).ConfigureAwait(false);
        if (events.Count == 0)
        {
            return SprintTimelinePage.Empty(sprintId, cursorToken, DiagnosticCodes.SprintNotFound);
        }

        IReadOnlyList<Handoff> handoffs =
            await store.GetHandoffsAsync(projectRoot, id, cancellationToken).ConfigureAwait(false);
        List<SprintTimelineItem> page = MergeAndPage(events, handoffs, cursor.Watermark, maxItemsPerPage);
        long nextWatermark = page.Count > 0 ? page[^1].Sequence : cursor.Watermark;
        string nextCursor =
            SprintTimelineCursorCodec.Encode(new(SprintTimelineCursor.CurrentVersion, sprintId, nextWatermark));
        return new(SprintTimelinePage.ContractVersion, sprintId, page, nextCursor, DiagnosticCodes.None);
    }

    /// <summary>Bounds the candidate set to at most <paramref name="maxItemsPerPage"/> items BEFORE
    /// the expensive per-item work (<see cref="ToItem"/>'s projection and
    /// <see cref="SecretRedactor.RedactProperties"/> redaction pass) ever runs -- restores the
    /// `Where -&gt; OrderBy -&gt; Take -&gt; Select` shape this had before agent summaries existed (PR
    /// #104 review, finding 3: the interim design projected and redacted every candidate above the
    /// watermark before applying the page bound, an unbounded-work regression on what is supposed to
    /// be a bounded read). Restoring that shape is only safe because finding 1's redesign removed the
    /// separate handoff-merge step entirely: every candidate is now a single <see cref="WorkflowEvent"/>
    /// from <paramref name="events"/>, each with its own real, dense, globally-unique
    /// <see cref="WorkflowEvent.Sequence"/> -- there is no second source to merge and no same-sequence
    /// tie that could ever split across a page boundary, so the old tie-break logic is gone too. The
    /// <see cref="Handoff"/> store is still read, but only to build <paramref name="handoffs"/>' set of
    /// superseded ids: a superseded handoff's summary must never appear (a rewind invalidated it,
    /// matching how a superseded artifact is already excluded from
    /// <c>SprintScheduler.IsTestWorkEligibleAsync</c>), and <see cref="Handoff.Superseded"/> is mutable
    /// state the immutable, already-landed <see cref="WorkflowEvent.AgentSummaryRecordedType"/> event
    /// cannot itself carry.</summary>
    private static List<SprintTimelineItem> MergeAndPage(
        IReadOnlyList<WorkflowEvent> events, IReadOnlyList<Handoff> handoffs, long watermark, int maxItemsPerPage)
    {
        HashSet<Guid> supersededHandoffIds =
            [.. handoffs.Where(item => item.Superseded is not null).Select(item => item.HandoffId)];

        return
        [
            .. events
                .Where(item => item.Sequence > watermark)
                .Where(item => item.Type != WorkflowEvent.AgentSummaryRecordedType ||
                    !IsSupersededSummary(item, supersededHandoffIds))
                .OrderBy(item => item.Sequence)
                .Take(maxItemsPerPage)
                .Select(ToItem),
        ];
    }

    /// <summary>An <see cref="WorkflowEvent.AgentSummaryRecordedType"/> event's own
    /// <see cref="WorkflowEvent.CorrelationId"/> carries the exact <see cref="Handoff.HandoffId"/> it
    /// was recorded for (see <see cref="ISprintStore.AppendAgentSummaryRecordedAsync"/>) -- this is
    /// the only place that correlation is ever read back.</summary>
    private static bool IsSupersededSummary(WorkflowEvent item, HashSet<Guid> supersededHandoffIds) =>
        item.CorrelationId is { } handoffId && supersededHandoffIds.Contains(handoffId);

    /// <summary>Redaction pass 1 of 2 (plan 12.3): applied once here, before this projected item ever
    /// leaves this method -- so any future cache/persisted view of the timeline never receives
    /// unredacted content in the first place. Reuses <see cref="SecretRedactor"/> (ADR 0039's
    /// chokepoint) rather than a new rule; pass 2 runs again, independently, at render time. Handles
    /// every event type generically, including <see cref="WorkflowEvent.AgentSummaryRecordedType"/>
    /// since finding 1's redesign: its summary text lives in <see cref="WorkflowEvent.Arguments"/>
    /// like any other bounded free-text argument, and its <see cref="TimelineActor.Agent"/> actor and
    /// node <see cref="SprintTimelineItem.TargetKind"/>/<see cref="SprintTimelineItem.TargetId"/> come
    /// straight from the event's own <see cref="WorkflowEvent.Type"/>/<see cref="WorkflowEvent.Aggregate"/>
    /// -- no second, handoff-specific projection method is needed any more.</summary>
    private static SprintTimelineItem ToItem(WorkflowEvent item)
    {
        Dictionary<string, object?> raw = item.Arguments.ToDictionary(
            entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal);
        IReadOnlyDictionary<string, object?> redacted = SecretRedactor.RedactProperties(raw);
        Dictionary<string, string?> arguments = redacted.ToDictionary(
            entry => entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        TimelineActor actor = item.Type == WorkflowEvent.AgentSummaryRecordedType
            ? TimelineActor.Agent
            : OperatorTriggeredTypes.Contains(item.Type) ? TimelineActor.Operator : TimelineActor.System;
        return new(
            item.EventId,
            item.Sequence,
            item.OccurredAt,
            item.Type,
            actor,
            WorkflowStateNames.ToSnakeCase(item.Aggregate.Kind),
            item.Aggregate.Id,
            item.MessageKey,
            arguments,
            item.CorrelationId,
            item.CausationId,
            [],
            // ADR 0059: redacted here in pass 1 too -- SecretRedactor.RedactProperties above only
            // ever sees Arguments, and a future persisted/cached view of this projection must never
            // receive an unredacted payload in the first place.
            SprintTimelineRedaction.RedactPayload(item.Payload));
    }
}
