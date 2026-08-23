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
public sealed record SprintTimelineItem(
    Guid Id,
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
/// stream, never a cross-sprint merge, so a single watermark is exact.</summary>
public sealed record SprintTimelineCursor(string Version, long Watermark)
{
    public const string CurrentVersion = "1.0.0";

    public static SprintTimelineCursor Empty { get; } = new(CurrentVersion, -1);
}

/// <summary>Encodes/decodes <see cref="SprintTimelineCursor"/> as an opaque base64 token -- same
/// fail-safe decode contract as <see cref="ControlEventsCursorCodec"/>: a malformed, foreign, or
/// future-versioned token decodes to <see cref="SprintTimelineCursor.Empty"/> with
/// <see langword="false"/>, never a silent rebaseline.</summary>
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

    public static bool TryDecode(string? token, out SprintTimelineCursor cursor)
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
            if (decoded is null || decoded.Version != SprintTimelineCursor.CurrentVersion)
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
    public const string ContractVersion = "1.0.0";

    public static SprintTimelinePage Empty(Guid sprintId, string? requestedCursor, string diagnosticCode)
    {
        bool valid = SprintTimelineCursorCodec.TryDecode(requestedCursor, out SprintTimelineCursor decoded);
        return new(
            ContractVersion,
            sprintId,
            [],
            SprintTimelineCursorCodec.Encode(valid ? decoded : SprintTimelineCursor.Empty),
            valid ? diagnosticCode : DiagnosticCodes.ControlCursorStale);
    }
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
        if (!SprintTimelineCursorCodec.TryDecode(cursorToken, out SprintTimelineCursor cursor))
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
            SprintTimelineCursorCodec.Encode(new(SprintTimelineCursor.CurrentVersion, nextWatermark));
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
