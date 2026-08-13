using System.Globalization;
using System.Text.Json;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// The opaque cursor ADR 0005 assigns to `ReadControlEvents`: per-sprint sequence watermarks plus
/// its own format version, so an old or foreign cursor is rejected rather than misread. Never
/// constructed or inspected by a caller — only round-tripped through
/// <see cref="ControlEventsCursorCodec"/>.
/// </summary>
public sealed record ControlEventsCursor(string Version, IReadOnlyDictionary<string, long> Watermarks)
{
    public const string CurrentVersion = "1.0.0";

    public static ControlEventsCursor Empty { get; } = new(CurrentVersion, new Dictionary<string, long>(StringComparer.Ordinal));
}

/// <summary>Encodes/decodes <see cref="ControlEventsCursor"/> as an opaque base64 token. Decoding
/// never throws: a malformed, foreign, or future-versioned token decodes to
/// <see cref="ControlEventsCursor.Empty"/> with <see langword="false"/>, so a caller fails loudly
/// with a fresh safe anchor instead of silently rebaselining (ADR 0005).</summary>
public static class ControlEventsCursorCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Encode(ControlEventsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, Options));
    }

    public static bool TryDecode(string? token, out ControlEventsCursor cursor)
    {
        if (string.IsNullOrEmpty(token))
        {
            cursor = ControlEventsCursor.Empty;
            return true;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            ControlEventsCursor? decoded = JsonSerializer.Deserialize<ControlEventsCursor>(bytes, Options);
            if (decoded is null || decoded.Version != ControlEventsCursor.CurrentVersion || decoded.Watermarks is null)
            {
                cursor = ControlEventsCursor.Empty;
                return false;
            }

            cursor = decoded;
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            cursor = ControlEventsCursor.Empty;
            return false;
        }
    }
}

/// <summary>One journal record merged across sprints, addressed back to its owning sprint since a
/// raw <see cref="WorkflowEvent"/> only names an aggregate id, not a sprint.</summary>
public sealed record ControlEventRecord(Guid SprintId, WorkflowEvent Event);

/// <summary>One bounded `ReadControlEvents` read: the merged, newly visible events plus the cursor
/// that continues from exactly where this page stopped.</summary>
public sealed record ControlEventsPage(
    IReadOnlyList<ControlEventRecord> Events,
    string Cursor,
    string DiagnosticCode)
{
    /// <summary>Used both for an uninitialized project (nothing to read yet) and for a rejected
    /// cursor: <paramref name="requestedCursor"/> is preserved verbatim when it was itself valid
    /// (just moot), otherwise the caller gets a fresh anchor and <see cref="DiagnosticCodes.ControlCursorStale"/>.</summary>
    public static ControlEventsPage Empty(string? requestedCursor)
    {
        bool valid = ControlEventsCursorCodec.TryDecode(requestedCursor, out ControlEventsCursor decoded);
        return new([], ControlEventsCursorCodec.Encode(valid ? decoded : ControlEventsCursor.Empty),
            valid ? DiagnosticCodes.None : DiagnosticCodes.ControlCursorStale);
    }
}

/// <summary>
/// Implements `ReadControlEvents`: reads every sprint's append-only journal instead of a separate
/// event store, merges records unseen by the caller's cursor deterministically by occurrence time,
/// sprint creation order, event sequence, and event id, and returns a bounded page with a cursor
/// that resumes exactly where this page stopped (ADR 0005). Discovers new sprints automatically —
/// a sprint absent from the cursor's watermarks starts at -1, so every one of its events is unseen.
/// </summary>
public sealed class ControlEventsReader(ISprintStore store)
{
    /// <summary>A read-size bound, matching every other bounded read in this codebase; a client that
    /// wants more polls again with the returned cursor rather than this reader ever loading an
    /// unbounded merge into memory.</summary>
    public const int MaxEventsPerRead = 500;

    public async Task<ControlEventsPage> ReadAsync(
        string projectRoot,
        string? cursorToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!ControlEventsCursorCodec.TryDecode(cursorToken, out ControlEventsCursor cursor))
        {
            return ControlEventsPage.Empty(cursorToken);
        }

        IReadOnlyList<SprintJournalEntry> journal =
            await SprintJournal.LoadAllAsync(store, projectRoot, cancellationToken).ConfigureAwait(false);
        List<(Guid SprintId, WorkflowEvent Event, int CreationRank)> pending = [];
        for (int rank = 0; rank < journal.Count; rank++)
        {
            SprintJournalEntry entry = journal[rank];
            long watermark = cursor.Watermarks.GetValueOrDefault(entry.Id.Value.ToString("D", CultureInfo.InvariantCulture), -1);
            foreach (WorkflowEvent item in entry.Events)
            {
                if (item.Sequence > watermark)
                {
                    pending.Add((entry.Id.Value, item, rank));
                }
            }
        }

        List<(Guid SprintId, WorkflowEvent Event, int CreationRank)> page =
        [
            .. pending
                .OrderBy(item => item.Event.OccurredAt)
                .ThenBy(item => item.CreationRank)
                .ThenBy(item => item.Event.Sequence)
                .ThenBy(item => item.Event.EventId)
                .Take(MaxEventsPerRead),
        ];

        Dictionary<string, long> watermarks =
            AdvanceWatermarks(cursor.Watermarks, page.Select(item => (item.SprintId, item.Event)));
        string nextCursor = ControlEventsCursorCodec.Encode(new(ControlEventsCursor.CurrentVersion, watermarks));

        // Deliver only the events each sprint's watermark actually advanced through. Without this,
        // an event stranded past a gap (Sequence <= a later item that made the page, but excluded by
        // this same cut) would still be handed to the caller now — and, since the watermark cannot
        // advance past it either, handed to the caller *again* once the gap-filling predecessor
        // arrives on a later read. Withholding it here instead means it is delivered exactly once,
        // in order, once its predecessor closes the gap.
        List<(Guid SprintId, WorkflowEvent Event, int CreationRank)> deliverable =
        [
            .. page.Where(item => item.Event.Sequence <=
                watermarks[item.SprintId.ToString("D", CultureInfo.InvariantCulture)]),
        ];
        return new(
            [.. deliverable.Select(item => new ControlEventRecord(item.SprintId, item.Event))],
            nextCursor,
            DiagnosticCodes.None);
    }

    /// <summary>
    /// Advances each sprint's watermark only through a gap-free run starting right after its
    /// previous value — never to the bare maximum returned sequence. A sprint's events past its old
    /// watermark are always contiguous in append (sequence) order; the only way a lower sequence
    /// could be excluded from <paramref name="page"/> while a higher one for the same sprint is
    /// included is a merge-and-cut that placed them out of relative order — e.g. because a
    /// non-monotonic system clock (see <see cref="WorkflowEvent.OccurredAt"/>) sorted the higher
    /// sequence earlier. A <c>max(sequence)</c> watermark would then mark the excluded lower
    /// sequence as already seen, permanently skipping it on every future read — the "never silently
    /// rebaseline" cursor invariant this type's own doc comment promises. Advancing only through the
    /// contiguous run makes that impossible: the sprint simply doesn't advance past the gap until a
    /// later page returns it. Exposed as a pure static method so this exact invariant is
    /// unit-testable without a real store or a page large enough to trigger
    /// <see cref="MaxEventsPerRead"/>'s cut.
    /// </summary>
    public static Dictionary<string, long> AdvanceWatermarks(
        IReadOnlyDictionary<string, long> currentWatermarks,
        IEnumerable<(Guid SprintId, WorkflowEvent Event)> page)
    {
        Dictionary<string, long> watermarks = new(currentWatermarks, StringComparer.Ordinal);
        foreach (IGrouping<Guid, WorkflowEvent> group in page.GroupBy(item => item.SprintId, item => item.Event))
        {
            string key = group.Key.ToString("D", CultureInfo.InvariantCulture);
            HashSet<long> included = [.. group.Select(item => item.Sequence)];
            long newWatermark = watermarks.GetValueOrDefault(key, -1);
            while (included.Contains(newWatermark + 1))
            {
                newWatermark++;
            }

            watermarks[key] = newWatermark;
        }

        return watermarks;
    }
}

/// <summary>Culture-invariant machine representation of <see cref="ControlEventsPage"/>, reusing
/// <see cref="WorkflowEventCodec"/>'s already schema-validated per-event shape rather than
/// duplicating it.</summary>
public static class ControlEventsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static string Serialize(ControlEventsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Persisted persisted = new()
        {
            Cursor = page.Cursor,
            DiagnosticCode = page.DiagnosticCode,
            Events = [.. page.Events.Select(ToPersisted)],
        };
        return JsonSerializer.Serialize(persisted, Options);
    }

    private static PersistedEntry ToPersisted(ControlEventRecord record)
    {
        using JsonDocument document = JsonDocument.Parse(WorkflowEventCodec.Serialize(record.Event));
        return new() { SprintId = record.SprintId, Event = document.RootElement.Clone() };
    }

    private sealed class Persisted
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public List<PersistedEntry> Events { get; set; } = [];

        public string Cursor { get; set; } = string.Empty;

        public string DiagnosticCode { get; set; } = DiagnosticCodes.None;
    }

    private sealed class PersistedEntry
    {
        public Guid SprintId { get; set; }

        public JsonElement Event { get; set; }
    }
}
