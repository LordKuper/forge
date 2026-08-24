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
/// <remarks>ADR 0054 briefly added a <c>Sequence</c> field here so <c>SprintTimelineProjector</c>
/// could anchor a projected summary into the journal's own order by borrowing the sprint's current
/// watermark at record time. Round of PR #104 review (finding 1) found that borrowing unsound (the
/// borrowed value could belong to an unrelated later event, and a cursor already past it could never
/// see the handoff once written) and redesigned it: the summary is now its own real
/// <see cref="Forge.Domain.WorkflowEvent.AgentSummaryRecordedType"/> journal entry with its own real
/// <see cref="Forge.Domain.WorkflowEvent.Sequence"/>, so <see cref="Handoff"/> needs no sequence of
/// its own at all -- removed rather than kept as unused debt (finding 2 confirmed nothing else read
/// it once the redesign lands).</remarks>
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
