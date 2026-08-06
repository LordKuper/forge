namespace Forge.Domain;

/// <summary>Matches `severity` in docs/contracts/v1/schemas/finding.schema.json.</summary>
public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Matches `status`. Only `Open` findings block a sprint's completion gate.</summary>
public enum FindingStatus
{
    Open,
    Accepted,
    Resolved,
    Dismissed,
}

public sealed record FindingLocation(string Path, int? Line = null);

public sealed record Finding(
    Guid FindingId,
    SprintId SprintId,
    string Fingerprint,
    FindingSeverity Severity,
    FindingStatus Status,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments,
    IReadOnlyList<string> Evidence,
    FindingLocation? Location = null);
