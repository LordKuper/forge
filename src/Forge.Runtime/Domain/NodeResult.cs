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
/// store puts them. <paramref name="Revision"/> is the stage revision this result was recorded
/// under (plan section 8.4); <paramref name="Superseded"/> is set once a rewind whose target is at
/// or upstream of <see cref="NodeId"/> invalidates it -- excluded from every prerequisite check
/// from that point on, but never deleted or rewritten otherwise (ADR 0045).
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
    IReadOnlyList<NodeDiagnostic> Diagnostics,
    StageRevision Revision = default,
    SupersededBy? Superseded = null);
