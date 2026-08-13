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
            return new([], ControlEventsCursorCodec.Encode(ControlEventsCursor.Empty), DiagnosticCodes.ControlCursorStale);
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

        Dictionary<string, long> watermarks = new(cursor.Watermarks, StringComparer.Ordinal);
        foreach ((Guid sprintId, WorkflowEvent item, _) in page)
        {
            string key = sprintId.ToString("D", CultureInfo.InvariantCulture);
            if (!watermarks.TryGetValue(key, out long current) || item.Sequence > current)
            {
                watermarks[key] = item.Sequence;
            }
        }

        string nextCursor = ControlEventsCursorCodec.Encode(new(ControlEventsCursor.CurrentVersion, watermarks));
        return new(
            [.. page.Select(item => new ControlEventRecord(item.SprintId, item.Event))],
            nextCursor,
            DiagnosticCodes.None);
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
