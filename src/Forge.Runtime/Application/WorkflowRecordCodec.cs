using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;
using Json.Schema;

namespace Forge.Application;

/// <summary>
/// Validates node results, findings, and handoffs against their frozen v1 schemas before they are
/// persisted. Storage uses a more compact, per-sprint-scoped shape (schema_version and sprint_id
/// are implied by the containing sprint directory, not repeated in every file); this codec
/// reconstructs the full wire shape purely to validate it against contract.
/// </summary>
internal static class WorkflowRecordCodec
{
    private static readonly JsonSchema NodeResultSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.node-result.schema.json");
    private static readonly JsonSchema FindingSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.finding.schema.json");
    private static readonly JsonSchema HandoffSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.handoff.schema.json");
    private static readonly JsonSchema ConfirmationSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.confirmation-result.schema.json");
    private static readonly JsonSerializerOptions JsonOptions = ConfigurationSchemaCodec.SerializerOptions;

    public static void ValidateNodeResult(NodeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        WireNodeResult wire = new()
        {
            SprintId = result.SprintId.Value.ToString("D"),
            NodeId = result.NodeId.Value,
            AttemptId = result.AttemptId.Value.ToString("D"),
            State = WorkflowStateNames.ToSnakeCase(result.State),
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            InputDigest = result.InputDigest,
            Outputs = [.. result.Outputs],
            Diagnostics = [.. result.Diagnostics.Select(
                item => new WireDiagnostic
                {
                    Code = item.Code,
                    Category = item.Category,
                    MessageKey = item.MessageKey,
                    Arguments = new(item.Arguments, StringComparer.Ordinal),
                })],
        };
        SchemaValidation.Validate(
            JsonSerializer.SerializeToElement(wire, JsonOptions),
            NodeResultSchema,
            "node result");
    }

    public static void ValidateFinding(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        WireFinding wire = new()
        {
            FindingId = finding.FindingId.ToString("D"),
            SprintId = finding.SprintId.Value.ToString("D"),
            Fingerprint = finding.Fingerprint,
            Severity = WorkflowStateNames.ToSnakeCase(finding.Severity),
            Status = WorkflowStateNames.ToSnakeCase(finding.Status),
            MessageKey = finding.MessageKey,
            Arguments = new(finding.Arguments, StringComparer.Ordinal),
            Evidence = [.. finding.Evidence],
            Location = finding.Location is { } location
                ? new() { Path = location.Path, Line = location.Line }
                : null,
        };
        SchemaValidation.Validate(JsonSerializer.SerializeToElement(wire, JsonOptions), FindingSchema, "finding");
    }

    public static void ValidateHandoff(Handoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        WireHandoff wire = new()
        {
            HandoffId = handoff.HandoffId.ToString("D"),
            SprintId = handoff.SprintId.Value.ToString("D"),
            NodeId = handoff.NodeId.Value,
            BaseSha = handoff.BaseSha,
            Summary = handoff.Summary,
            Decisions = [.. handoff.Decisions],
            Artifacts = [.. handoff.Artifacts.Select(
                item => new WireArtifact
                {
                    Digest = item.Digest,
                    MediaType = item.MediaType,
                    Audience = WorkflowStateNames.ToSnakeCase(item.Audience),
                    Language = item.Language,
                    PolicySnapshotHash = item.PolicySnapshotHash,
                    GeneratorVersion = item.GeneratorVersion,
                })],
            OpenRisks = [.. handoff.OpenRisks],
            NextNodeIds = handoff.NextNodeIds is null ? null : [.. handoff.NextNodeIds],
        };
        SchemaValidation.Validate(JsonSerializer.SerializeToElement(wire, JsonOptions), HandoffSchema, "handoff");
    }

    public static void ValidateConfirmation(ConfirmationArtifact confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        WireConfirmation wire = new()
        {
            ConfirmationId = confirmation.ConfirmationId.ToString("D"),
            SprintId = confirmation.SprintId.Value.ToString("D"),
            NodeId = confirmation.NodeId.Value,
            Outcome = WorkflowStateNames.ToSnakeCase(confirmation.Outcome),
            DefinitionOfDone = confirmation.DefinitionOfDone,
            Evidence = [.. confirmation.Evidence.Select(
                item => new WireEvidence
                {
                    Kind = WorkflowStateNames.ToSnakeCase(item.Kind),
                    Description = item.Description,
                })],
        };
        SchemaValidation.Validate(
            JsonSerializer.SerializeToElement(wire, JsonOptions), ConfirmationSchema, "confirmation result");
    }

    private sealed class WireNodeResult
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string SprintId { get; set; } = string.Empty;

        public string NodeId { get; set; } = string.Empty;

        public string AttemptId { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset CompletedAt { get; set; }

        public string InputDigest { get; set; } = string.Empty;

        public List<string> Outputs { get; set; } = [];

        public List<WireDiagnostic> Diagnostics { get; set; } = [];
    }

    private sealed class WireDiagnostic
    {
        public string Code { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string MessageKey { get; set; } = string.Empty;

        public Dictionary<string, string?> Arguments { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class WireFinding
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string FindingId { get; set; } = string.Empty;

        public string SprintId { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public string Severity { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string MessageKey { get; set; } = string.Empty;

        public Dictionary<string, string?> Arguments { get; set; } = new(StringComparer.Ordinal);

        public List<string> Evidence { get; set; } = [];

        public WireLocation? Location { get; set; }
    }

    private sealed class WireLocation
    {
        public string Path { get; set; } = string.Empty;

        public int? Line { get; set; }
    }

    private sealed class WireHandoff
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string HandoffId { get; set; } = string.Empty;

        public string SprintId { get; set; } = string.Empty;

        public string NodeId { get; set; } = string.Empty;

        public string BaseSha { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public List<string> Decisions { get; set; } = [];

        public List<WireArtifact> Artifacts { get; set; } = [];

        public List<string> OpenRisks { get; set; } = [];

        public List<string>? NextNodeIds { get; set; }
    }

    private sealed class WireArtifact
    {
        public string Digest { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string Audience { get; set; } = string.Empty;

        public string? Language { get; set; }

        public string PolicySnapshotHash { get; set; } = string.Empty;

        public string GeneratorVersion { get; set; } = string.Empty;
    }

    private sealed class WireConfirmation
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string ConfirmationId { get; set; } = string.Empty;

        public string SprintId { get; set; } = string.Empty;

        public string NodeId { get; set; } = string.Empty;

        public string Outcome { get; set; } = string.Empty;

        public string DefinitionOfDone { get; set; } = string.Empty;

        public List<WireEvidence> Evidence { get; set; } = [];
    }

    private sealed class WireEvidence
    {
        public string Kind { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }
}
