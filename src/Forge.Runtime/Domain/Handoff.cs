namespace Forge.Domain;

/// <summary>Matches `artifacts[].audience` in docs/contracts/v1/schemas/handoff.schema.json.</summary>
public enum ArtifactAudience
{
    UserFacing,
    AgentFacing,
    Machine,
}

/// <summary>
/// A published, content-addressed artifact reference. Only the digest and its governing policy
/// snapshot are recorded here; nothing in Stage 6 produces one yet (the artifact store lands with
/// the `.forge/` compiler and memory work), so <see cref="Handoff.Artifacts"/> stays empty until it
/// does — the shape exists so a handoff can carry them once it can.
/// </summary>
public sealed record HandoffArtifact(
    string Digest,
    string MediaType,
    ArtifactAudience Audience,
    string? Language,
    string PolicySnapshotHash,
    string GeneratorVersion);

/// <summary>
/// The structured context one node leaves for whatever runs next, matching
/// docs/contracts/v1/schemas/handoff.schema.json. Unlike events and findings, `Summary`,
/// `Decisions`, and `OpenRisks` are free text by contract — a handoff is written for the next
/// node's model to read, not rendered as localized UI.
/// </summary>
/// <summary><paramref name="Revision"/>/<paramref name="Superseded"/> follow the same rewind
/// supersession rule as <see cref="NodeResult"/> (plan section 8.4, ADR 0045).</summary>
public sealed record Handoff(
    Guid HandoffId,
    SprintId SprintId,
    NodeId NodeId,
    string BaseSha,
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<HandoffArtifact> Artifacts,
    IReadOnlyList<string> OpenRisks,
    IReadOnlyList<string>? NextNodeIds = null,
    StageRevision Revision = default,
    SupersededBy? Superseded = null);
