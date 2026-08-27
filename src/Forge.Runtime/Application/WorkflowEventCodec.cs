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

    /// <summary>ADR 0059. <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> is
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/> on
    /// <see cref="JsonOptions"/>, so a <see langword="null"/> payload (every event type except
    /// <see cref="WorkflowEvent.AttemptDiffRecordedType"/>) is omitted from the line entirely rather
    /// than written as `"payload": null` — which the envelope's own
    /// `additionalProperties: false`/typed `payload` object would reject.</summary>
    private static PersistedPayload? ToPersisted(WorkflowEventPayload? payload) =>
        payload?.Diff is not { } diff
            ? null
            : new()
            {
                Diff = new()
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
            };

    private static WorkflowEventPayload? FromPersisted(PersistedPayload? payload) =>
        payload?.Diff is not { } diff
            ? null
            : new(new DiffPayload(
                diff.FilesChanged,
                diff.Insertions,
                diff.Deletions,
                [.. diff.Files.Select(file => new DiffFileStat(file.Path, file.Added, file.Deleted, file.ChangeKind))],
                diff.ElidedFiles));

    private sealed class Persisted
    {
        /// <summary>ADR 0059 raised this from `1.0.0` to `1.1.0` (the optional `payload` object).
        /// The schema accepts BOTH values -- every line already on disk for every existing sprint
        /// carries `1.0.0` and is re-validated on every read, so a bare bump would have invalidated
        /// every existing journal. New lines are stamped `1.1.0` unconditionally, payload or
        /// not: the version describes the envelope this producer writes to, not whether one
        /// particular optional field happens to be populated.</summary>
        public string SchemaVersion { get; set; } = "1.1.0";

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
}
