using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;
using Json.Schema;

namespace Forge.Application;

/// <summary>Serializes a <see cref="WorkflowEvent"/> to/from one line conforming to event.schema.json.</summary>
internal static class WorkflowEventCodec
{
    private static readonly JsonSchema Schema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.event.schema.json");
    private static readonly JsonSerializerOptions JsonOptions = ConfigurationSchemaCodec.SerializerOptions;
    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

    public static string Serialize(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        Persisted persisted = new()
        {
            EventId = workflowEvent.EventId,
            Sequence = workflowEvent.Sequence,
            OccurredAt = workflowEvent.OccurredAt,
            Type = workflowEvent.Type,
            Aggregate = new()
            {
                Kind = WorkflowStateNames.ToSnakeCase(workflowEvent.Aggregate.Kind),
                Id = workflowEvent.Aggregate.Id,
                Version = workflowEvent.Aggregate.Version,
            },
            MessageKey = workflowEvent.MessageKey,
            Arguments = new(workflowEvent.Arguments, StringComparer.Ordinal),
            CorrelationId = workflowEvent.CorrelationId,
            CausationId = workflowEvent.CausationId,
            Payload = ToPersisted(workflowEvent.Payload),
        };
        JsonElement element = JsonSerializer.SerializeToElement(persisted, JsonOptions);
        Validate(element);
        return JsonSerializer.Serialize(element, CompactOptions);
    }

    public static WorkflowEvent Deserialize(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        Validate(document.RootElement);
        Persisted persisted = document.RootElement.Deserialize<Persisted>(JsonOptions) ??
            throw new InvalidDataException("A workflow event line is empty.");
        return new(
            persisted.EventId,
            persisted.Sequence,
            persisted.OccurredAt,
            persisted.Type,
            new(
                WorkflowStateNames.Parse<AggregateKind>(persisted.Aggregate.Kind),
                persisted.Aggregate.Id,
                persisted.Aggregate.Version),
            persisted.MessageKey,
            persisted.Arguments,
            persisted.CorrelationId,
            persisted.CausationId,
            FromPersisted(persisted.Payload));
    }

    private static void Validate(JsonElement element) => SchemaValidation.Validate(element, Schema, "workflow event");

    /// <summary>ADR 0059/0060. <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> is
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/> on
    /// <see cref="JsonOptions"/>, so a <see langword="null"/> payload (every event type except
    /// <see cref="WorkflowEvent.AttemptDiffRecordedType"/> and
    /// <see cref="WorkflowEvent.AttemptToolUseRecordedType"/>) is omitted from the line entirely
    /// rather than written as `"payload": null` — which the envelope's own
    /// `additionalProperties: false`/typed `payload` object would reject. The same condition omits
    /// whichever families a given event does not carry, so a diff-only line stays byte-identical to
    /// what ADR 0059 wrote.
    ///
    /// Deliberately a per-family check rather than the either/or `payload?.Diff is not { }` shape ADR
    /// 0059 could get away with while `diff` was the only family: once two exist, an early return
    /// keyed on one of them would silently discard the other.</summary>
    private static PersistedPayload? ToPersisted(WorkflowEventPayload? payload)
    {
        if (payload is null || (payload.Diff is null && payload.ToolUse is null))
        {
            return null;
        }

        return new()
        {
            Diff = payload.Diff is not { } diff
                ? null
                : new()
                {
                    FilesChanged = diff.FilesChanged,
                    Insertions = diff.Insertions,
                    Deletions = diff.Deletions,
                    Files =
                    [
                        .. diff.Files.Select(file => new PersistedDiffFile
                        {
                            Path = file.Path,
                            Added = file.Added,
                            Deleted = file.Deleted,
                            ChangeKind = file.ChangeKind,
                        }),
                    ],
                    ElidedFiles = diff.ElidedFiles,
                },
            ToolUse = payload.ToolUse is not { } toolUse
                ? null
                : new()
                {
                    ToolCalls = toolUse.ToolCalls,
                    Commands = toolUse.Commands,
                    Edits = toolUse.Edits,
                    Calls =
                    [
                        .. toolUse.Calls.Select(call => new PersistedToolCall
                        {
                            Kind = call.Kind,
                            Target = call.Target,
                            DurationMs = call.DurationMilliseconds,
                            ExitCode = call.ExitCode,
                            Succeeded = call.Succeeded,
                        }),
                    ],
                    ElidedCalls = toolUse.ElidedCalls,
                    UnmappedItems = toolUse.UnmappedItems,
                },
        };
    }

    private static WorkflowEventPayload? FromPersisted(PersistedPayload? payload)
    {
        if (payload is null || (payload.Diff is null && payload.ToolUse is null))
        {
            return null;
        }

        DiffPayload? diff = payload.Diff is not { } persistedDiff
            ? null
            : new(
                persistedDiff.FilesChanged,
                persistedDiff.Insertions,
                persistedDiff.Deletions,
                [
                    .. persistedDiff.Files.Select(
                        file => new DiffFileStat(file.Path, file.Added, file.Deleted, file.ChangeKind)),
                ],
                persistedDiff.ElidedFiles);
        ToolUsePayload? toolUse = payload.ToolUse is not { } persistedToolUse
            ? null
            : new(
                persistedToolUse.ToolCalls,
                persistedToolUse.Commands,
                persistedToolUse.Edits,
                [
                    .. persistedToolUse.Calls.Select(
                        call => new ToolCallStat(
                            call.Kind, call.Target, call.DurationMs, call.ExitCode, call.Succeeded)),
                ],
                persistedToolUse.ElidedCalls,
                persistedToolUse.UnmappedItems);
        return new(diff, toolUse);
    }

    private sealed class Persisted
    {
        /// <summary>ADR 0059 raised this from `1.0.0` to `1.1.0` (the optional `payload` object);
        /// ADR 0060 raises it again to `1.2.0` (`payload.tool_use`, a second family beside `diff`).
        /// The schema accepts ALL THREE values -- lines already on disk carry `1.0.0` or `1.1.0` and
        /// are re-validated on every read, so a bare bump would invalidate every existing journal.
        /// New lines are stamped `1.2.0` unconditionally, payload or not: the version describes the
        /// envelope this producer writes to, not whether one particular optional field happens to be
        /// populated.</summary>
        public string SchemaVersion { get; set; } = "1.2.0";

        public Guid EventId { get; set; }

        public long Sequence { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string Type { get; set; } = string.Empty;

        public PersistedAggregate Aggregate { get; set; } = new();

        public string MessageKey { get; set; } = string.Empty;

        public Dictionary<string, string?> Arguments { get; set; } = new(StringComparer.Ordinal);

        public Guid? CorrelationId { get; set; }

        public Guid? CausationId { get; set; }

        public PersistedPayload? Payload { get; set; }
    }

    private sealed class PersistedAggregate
    {
        public string Kind { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public long Version { get; set; }
    }

    private sealed class PersistedPayload
    {
        public PersistedDiff? Diff { get; set; }

        public PersistedToolUse? ToolUse { get; set; }
    }

    private sealed class PersistedDiff
    {
        public int FilesChanged { get; set; }

        public int Insertions { get; set; }

        public int Deletions { get; set; }

        public List<PersistedDiffFile> Files { get; set; } = [];

        public int ElidedFiles { get; set; }
    }

    private sealed class PersistedDiffFile
    {
        public string Path { get; set; } = string.Empty;

        public int Added { get; set; }

        public int Deleted { get; set; }

        public string ChangeKind { get; set; } = string.Empty;
    }

    private sealed class PersistedToolUse
    {
        public int ToolCalls { get; set; }

        public int Commands { get; set; }

        public int Edits { get; set; }

        public List<PersistedToolCall> Calls { get; set; } = [];

        public int ElidedCalls { get; set; }

        public int UnmappedItems { get; set; }
    }

    /// <summary>Every member except <see cref="Kind"/> is nullable, and
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/> omits it from the line rather than writing
    /// an explicit null -- which is why `event.schema.json`'s `tool_use_payload` requires only
    /// `kind` per row and declares the rest as plain typed properties.</summary>
    private sealed class PersistedToolCall
    {
        public string Kind { get; set; } = string.Empty;

        public string? Target { get; set; }

        public int? DurationMs { get; set; }

        public int? ExitCode { get; set; }

        public bool? Succeeded { get; set; }
    }
}
