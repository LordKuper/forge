namespace Forge.Domain;

/// <summary>Matches `outcome` in docs/contracts/v1/schemas/confirmation-result.schema.json.</summary>
public enum ConfirmationOutcome
{
    Confirmed,
    NotConfirmed,
}

/// <summary>
/// Matches `evidence[].kind`. Mirrors AGENTS.md's Quality rule: "confirm it against its definition
/// of done through inspection and execution" plus "existing checks may support confirmation".
/// </summary>
public enum ConfirmationEvidenceKind
{
    Inspection,
    Execution,
    ExistingCheck,
}

public sealed record ConfirmationEvidence(ConfirmationEvidenceKind Kind, string Description);

/// <summary>
/// A confirmation node's recorded judgment that an implementation does or does not meet its
/// definition of done or the user's stated expectations, matching
/// docs/contracts/v1/schemas/confirmation-result.schema.json. This is the "valid artifact" the
/// plan's Stage 11 item requires before a dependent test-work node is eligible to run — see
/// <c>SprintScheduler.AdvanceGraphAsync</c>'s gating on <see cref="Outcome"/> for nodes tagged
/// <see cref="NodeRole.Confirmation"/>. What a test-work node then does with that eligibility
/// (select tests, or record a justified no-new-test decision) is not modeled here — nothing
/// constructs a test-work node's own result yet, the same "shape now, producer later" gap ADR
/// 0009 left for <c>Handoff</c>.
/// </summary>
/// <summary>
/// <see cref="RecordedAt"/> makes confirmations for the same <see cref="NodeId"/> orderable: a
/// confirmation node can be re-attempted (a rejected human gate, a retried node), so more than one
/// artifact can exist for it, and only the most recently recorded one governs eligibility — an
/// earlier `Confirmed` artifact must never outlive a later `NotConfirmed` one. See
/// <c>SprintScheduler.IsTestWorkEligibleAsync</c>.
/// </summary>
/// <summary><paramref name="Revision"/>/<paramref name="Superseded"/> follow the same rewind
/// supersession rule as <c>Forge.Domain.NodeResult</c> (plan section 8.4, ADR 0045) --
/// <c>SprintScheduler.IsTestWorkEligibleAsync</c> excludes a superseded artifact from "the latest
/// recorded one" regardless of its own <see cref="RecordedAt"/>.</summary>
public sealed record ConfirmationArtifact(
    Guid ConfirmationId,
    SprintId SprintId,
    NodeId NodeId,
    ConfirmationOutcome Outcome,
    string DefinitionOfDone,
    IReadOnlyList<ConfirmationEvidence> Evidence,
    DateTimeOffset RecordedAt,
    StageRevision Revision = default,
    SupersededBy? Superseded = null);
