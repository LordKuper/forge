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
/// <summary><paramref name="Sequence"/> (ADR 0054, post-release timeline gap closure): the sprint
/// journal's own <c>WorkflowEvent.Sequence</c> watermark at the moment this handoff was recorded --
/// <c>SprintScheduler.RecordHandoffAsync</c> is always called immediately after the node's own
/// completing transition already landed, so this is exactly that transition's sequence, not an
/// approximation. Lets <c>SprintTimelineProjector</c> place this handoff's projected summary item in
/// the same dense per-sprint order as every system event, without a second cursor/watermark to merge.
/// Defaults to <c>0</c> for a handoff recorded before this field existed; the projector treats an
/// unresolvable anchor defensively (never throws), see its own remarks.</summary>
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
    SupersededBy? Superseded = null,
    long Sequence = 0);
