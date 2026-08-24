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
    private static readonly JsonSchema TestWorkSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.test-work-result.schema.json");
    private static readonly JsonSchema ExecutionProfileSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.execution-profile.schema.json");
    private static readonly JsonSchema ReviewIterationSchema =
        SchemaValidation.LoadEmbedded("Forge.Application.Schemas.review-iteration.schema.json");
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
            Revision = result.Revision.Value,
            SupersededAtRevision = result.Superseded?.AtRevision.Value,
            SupersededAt = result.Superseded?.RecordedAt,
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
            NodeId = finding.NodeId?.Value,
            Revision = finding.Revision.Value,
            SupersededAtRevision = finding.Superseded?.AtRevision.Value,
            SupersededAt = finding.Superseded?.RecordedAt,
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
            Revision = handoff.Revision.Value,
            SupersededAtRevision = handoff.Superseded?.AtRevision.Value,
            SupersededAt = handoff.Superseded?.RecordedAt,
            Sequence = handoff.Sequence,
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
            RecordedAt = confirmation.RecordedAt,
            Revision = confirmation.Revision.Value,
            SupersededAtRevision = confirmation.Superseded?.AtRevision.Value,
            SupersededAt = confirmation.Superseded?.RecordedAt,
        };
        SchemaValidation.Validate(
            JsonSerializer.SerializeToElement(wire, JsonOptions), ConfirmationSchema, "confirmation result");
    }

    public static void ValidateTestWork(TestWorkArtifact testWork)
    {
        ArgumentNullException.ThrowIfNull(testWork);
        WireTestWork wire = new()
        {
            TestWorkId = testWork.TestWorkId.ToString("D"),
            SprintId = testWork.SprintId.Value.ToString("D"),
            NodeId = testWork.NodeId.Value,
            Outcome = WorkflowStateNames.ToSnakeCase(testWork.Outcome),
            Justification = testWork.Justification,
            RecordedAt = testWork.RecordedAt,
            Revision = testWork.Revision.Value,
            SupersededAtRevision = testWork.Superseded?.AtRevision.Value,
            SupersededAt = testWork.Superseded?.RecordedAt,
        };
        SchemaValidation.Validate(
            JsonSerializer.SerializeToElement(wire, JsonOptions), TestWorkSchema, "test-work result");
    }

    public static void ValidateExecutionProfile(ExecutionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        WireExecutionProfile wire = new()
        {
            Phase = WorkflowStateNames.ToSnakeCase(profile.Phase),
            Provider = profile.Provider,
            Model = profile.Model,
            Effort = profile.Effort,
            SandboxPolicy = profile.SandboxPolicy,
            PermissionPolicy = profile.PermissionPolicy,
            CapabilityAllowlist = [.. profile.CapabilityAllowlist],
            SessionDeadlineSeconds = profile.SessionDeadlineSeconds,
            IdleDeadlineSeconds = profile.IdleDeadlineSeconds,
            Lineage = profile.Lineage is { } lineage
                ? new()
                {
                    ImplementationProvider = lineage.ImplementationProvider,
                    ImplementationModel = lineage.ImplementationModel,
                    AchievedIndependence = lineage.AchievedIndependence,
                }
                : null,
        };
        SchemaValidation.Validate(
            JsonSerializer.SerializeToElement(wire, JsonOptions), ExecutionProfileSchema, "execution profile");
    }

    public static void ValidateReviewIteration(ReviewIterationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        WireReviewIteration wire = new()
        {
            ReviewIterationId = record.ReviewIterationId.ToString("D"),
            SprintId = record.SprintId.Value.ToString("D"),
            NodeId = record.NodeId.Value,
            Dimension = WorkflowStateNames.ToSnakeCase(record.Dimension),
            ReviewerKind = WorkflowStateNames.ToSnakeCase(record.ReviewerKind),
            Iteration = record.Iteration,
            Outcome = WorkflowStateNames.ToSnakeCase(record.Outcome),
            ExternalFindings = [.. record.ExternalFindings.Select(
                item => new WireNormalizedFindingKey
                {
                    File = item.File,
                    Line = item.Line,
                    Rule = item.Rule,
                    MessageFingerprint = item.MessageFingerprint,
                })],
            Coverage = record.Coverage is { } coverage
                ? new()
                {
                    ScopedFiles = [.. coverage.ScopedFiles],
                    RubricItemIds = [.. coverage.RubricItemIds],
                    CoveredFiles = [.. coverage.CoveredFiles],
                    CoveredRubricItemIds = [.. coverage.CoveredRubricItemIds],
                }
                : null,
            RecordedAt = record.RecordedAt,
        };
        SchemaValidation.Validate(
            JsonSerializer.SerializeToElement(wire, JsonOptions), ReviewIterationSchema, "review iteration");
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

        public int Revision { get; set; }

        public int? SupersededAtRevision { get; set; }

        public DateTimeOffset? SupersededAt { get; set; }
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

        public string? NodeId { get; set; }

        public int Revision { get; set; }

        public int? SupersededAtRevision { get; set; }

        public DateTimeOffset? SupersededAt { get; set; }
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

        public int Revision { get; set; }

        public int? SupersededAtRevision { get; set; }

        public DateTimeOffset? SupersededAt { get; set; }

        public long Sequence { get; set; }
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

        public DateTimeOffset RecordedAt { get; set; }

        public int Revision { get; set; }

        public int? SupersededAtRevision { get; set; }

        public DateTimeOffset? SupersededAt { get; set; }
    }

    private sealed class WireEvidence
    {
        public string Kind { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }

    private sealed class WireTestWork
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string TestWorkId { get; set; } = string.Empty;

        public string SprintId { get; set; } = string.Empty;

        public string NodeId { get; set; } = string.Empty;

        public string Outcome { get; set; } = string.Empty;

        public string Justification { get; set; } = string.Empty;

        public DateTimeOffset RecordedAt { get; set; }

        public int Revision { get; set; }

        public int? SupersededAtRevision { get; set; }

        public DateTimeOffset? SupersededAt { get; set; }
    }

    private sealed class WireExecutionProfile
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string Phase { get; set; } = string.Empty;

        public string Provider { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string Effort { get; set; } = string.Empty;

        public string SandboxPolicy { get; set; } = string.Empty;

        public string PermissionPolicy { get; set; } = string.Empty;

        public List<string> CapabilityAllowlist { get; set; } = [];

        public int SessionDeadlineSeconds { get; set; }

        public int IdleDeadlineSeconds { get; set; }

        public WireLineage? Lineage { get; set; }
    }

    private sealed class WireLineage
    {
        public string ImplementationProvider { get; set; } = string.Empty;

        public string ImplementationModel { get; set; } = string.Empty;

        public bool AchievedIndependence { get; set; }
    }

    private sealed class WireReviewIteration
    {
        public string SchemaVersion { get; set; } = "1.0.0";

        public string ReviewIterationId { get; set; } = string.Empty;

        public string SprintId { get; set; } = string.Empty;

        public string NodeId { get; set; } = string.Empty;

        public string Dimension { get; set; } = string.Empty;

        public string ReviewerKind { get; set; } = string.Empty;

        public int Iteration { get; set; }

        public string Outcome { get; set; } = string.Empty;

        public List<WireNormalizedFindingKey> ExternalFindings { get; set; } = [];

        public WireCoverageLedger? Coverage { get; set; }

        public DateTimeOffset RecordedAt { get; set; }
    }

    private sealed class WireNormalizedFindingKey
    {
        public string? File { get; set; }

        public int? Line { get; set; }

        public string Rule { get; set; } = string.Empty;

        public string MessageFingerprint { get; set; } = string.Empty;
    }

    private sealed class WireCoverageLedger
    {
        public List<string> ScopedFiles { get; set; } = [];

        public List<string> RubricItemIds { get; set; } = [];

        public List<string> CoveredFiles { get; set; } = [];

        public List<string> CoveredRubricItemIds { get; set; } = [];
    }
}
