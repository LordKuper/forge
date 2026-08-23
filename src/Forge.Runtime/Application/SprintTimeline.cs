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
}

/// <summary>
/// One projected timeline entry (plan section 6.3). A pure, redacted view of one durable
/// <see cref="WorkflowEvent"/> -- never a second source of truth. <see cref="Arguments"/> is
/// redacted (see <see cref="SprintTimelineProjector"/>) before this record is ever constructed, and a
/// renderer must redact again before display (plan 12.3's "redact... before persistence and again
/// before rendering"). <see cref="ArtifactReferences"/> is always empty: <c>IArtifactStore</c> remains
/// an empty marker (ADR 0048) -- nothing in this codebase produces a real artifact yet.
/// </summary>
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
    IReadOnlyList<string> ArtifactReferences);

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
    public const string ContractVersion = "1.1.0";

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
/// of pass 1 (<see cref="SprintTimelineProjector.ToItem"/>), applied once inside
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
        };
}

/// <summary>
/// Projects one sprint's existing append-only <see cref="WorkflowEvent"/> journal
/// (<see cref="ISprintStore.GetEventsAsync"/>) into a bounded, cursor-paged
/// <see cref="SprintTimelinePage"/> -- a read projection, never a new source of truth (plan section
/// 6.3/ADR 0043). This slice covers only the system-event half of the timeline: user messages and
/// user-visible agent summaries (plan section 6.3's "separate bounded artifacts") have no durable
/// representation anywhere in this codebase yet, so no item of that kind is ever produced here (see
/// ADR 0049 for the scoping decision).
/// </summary>
public sealed class SprintTimelineProjector(ISprintStore store)
{
    /// <summary>A read-size bound, matching <see cref="ControlEventsReader.MaxEventsPerRead"/>.</summary>
    public const int MaxItemsPerPage = 500;

    /// <summary>Event types that only ever land as a direct consequence of a human-only mutation
    /// (attempt supersession, a stop request, a committed rewind) -- every other event type,
    /// including the convergence markers those same mutations append later, is an ordinary
    /// system-driven workflow consequence.</summary>
    private static readonly HashSet<string> OperatorTriggeredTypes = new(StringComparer.Ordinal)
    {
        WorkflowEvent.AttemptSupersededType,
        WorkflowEvent.AttemptStopRequestedType,
        WorkflowEvent.StageRevisionRecordedType,
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

        List<WorkflowEvent> page =
        [
            .. events
                .Where(item => item.Sequence > cursor.Watermark)
                .OrderBy(item => item.Sequence)
                .Take(MaxItemsPerPage),
        ];
        long nextWatermark = page.Count > 0 ? page[^1].Sequence : cursor.Watermark;
        string nextCursor =
            SprintTimelineCursorCodec.Encode(new(SprintTimelineCursor.CurrentVersion, sprintId, nextWatermark));
        return new(
            SprintTimelinePage.ContractVersion,
            sprintId,
            [.. page.Select(ToItem)],
            nextCursor,
            DiagnosticCodes.None);
    }

    /// <summary>Redaction pass 1 of 2 (plan 12.3): applied once here, before this projected item ever
    /// leaves this method -- so any future cache/persisted view of the timeline never receives
    /// unredacted content in the first place. Reuses <see cref="SecretRedactor"/> (ADR 0039's
    /// chokepoint) rather than a new rule; pass 2 runs again, independently, at render time.</summary>
    private static SprintTimelineItem ToItem(WorkflowEvent item)
    {
        Dictionary<string, object?> raw = item.Arguments.ToDictionary(
            entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal);
        IReadOnlyDictionary<string, object?> redacted = SecretRedactor.RedactProperties(raw);
        Dictionary<string, string?> arguments = redacted.ToDictionary(
            entry => entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        return new(
            item.EventId,
            item.Sequence,
            item.OccurredAt,
            item.Type,
            OperatorTriggeredTypes.Contains(item.Type) ? TimelineActor.Operator : TimelineActor.System,
            WorkflowStateNames.ToSnakeCase(item.Aggregate.Kind),
            item.Aggregate.Id,
            item.MessageKey,
            arguments,
            item.CorrelationId,
            item.CausationId,
            []);
    }
}
