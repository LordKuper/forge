namespace Forge.Domain;

/// <summary>Matches `state` in docs/contracts/v1/schemas/node-result.schema.json.</summary>
public enum NodeOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record NodeDiagnostic(
    string Code,
    string Category,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments);

/// <summary>
/// The durable, content-addressed record of one attempt's conclusion. Digests, not raw content,
/// are what gets persisted here — the actual bytes live wherever the (not-yet-built) artifact
/// store puts them.
/// </summary>
public sealed record NodeResult(
    SprintId SprintId,
    NodeId NodeId,
    AttemptId AttemptId,
    NodeOutcome State,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string InputDigest,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<NodeDiagnostic> Diagnostics);
