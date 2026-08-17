using System.Text.Json.Serialization;

namespace Forge.Domain;

/// <summary>
/// The `execution-profile.schema.json` contract Stage 11 (P11.13-P11.20) freezes onto every
/// sprint for its planning, implementation, and review phases (ADR 0006), including the reviewer
/// provider/model lineage-independence evidence ADR 0008 assigns to a review verdict. See
/// <see cref="SprintDefinition.ExecutionProfiles"/> for where a sprint's three frozen profiles are
/// recorded, and ADR 0014 for how they are resolved.
/// </summary>
public sealed record ExecutionProfile(
    string SchemaVersion,
    ExecutionPhase Phase,
    string Provider,
    string Model,
    string Effort,
    string SandboxPolicy,
    string PermissionPolicy,
    IReadOnlyList<string> CapabilityAllowlist,
    int SessionDeadlineSeconds,
    int IdleDeadlineSeconds,
    // Absent (not null) outside review: lineage independence is a review-verdict concept and does
    // not apply to planning/implementation phases, matching ProjectSnapshot.Details' precedent for
    // an optional section with no `null` schema variant.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionLineage? Lineage = null)
{
    public const string ContractVersion = "1.0.0";
}

public enum ExecutionPhase
{
    Planning,
    Implementation,
    Review,
}

/// <summary>Best-effort reviewer/implementation provider-model separation evidence (ADR 0006:
/// "Forge records whether provider/model separation was achieved; reduced separation is
/// diagnostic metadata, not a human gate").</summary>
public sealed record ExecutionLineage(
    string ImplementationProvider,
    string ImplementationModel,
    bool AchievedIndependence);
