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
            persisted.CausationId);
    }

    private static void Validate(JsonElement element) => SchemaValidation.Validate(element, Schema, "workflow event");

    private sealed class Persisted
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public Guid EventId { get; set; }

        public long Sequence { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string Type { get; set; } = string.Empty;

        public PersistedAggregate Aggregate { get; set; } = new();

        public string MessageKey { get; set; } = string.Empty;

        public Dictionary<string, string?> Arguments { get; set; } = new(StringComparer.Ordinal);

        public Guid? CorrelationId { get; set; }

        public Guid? CausationId { get; set; }
    }

    private sealed class PersistedAggregate
    {
        public string Kind { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public long Version { get; set; }
    }
}
