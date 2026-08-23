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

/// <summary><paramref name="NodeId"/> names the node this finding concerns, when the caller knows
/// one -- currently only <c>SprintScheduler.RecordReviewIterationAsync</c>, since review is the one
/// caller that already has a node in scope when it raises a finding. <see langword="null"/> for
/// every other caller (an operator-authored or ad hoc finding with no single owning node).
/// <paramref name="Revision"/> is the sprint's stage revision this finding was recorded under (plan
/// section 8.4); <paramref name="Superseded"/> is set once a later rewind invalidates it -- a
/// superseded finding remains readable history but is excluded from every prerequisite check (see
/// <c>SprintScheduler.EvaluateCompletionAsync</c>/<c>Forge.Application.StageTransitionAssessor</c>).
/// A rewind supersedes a finding whose <paramref name="NodeId"/> falls at or downstream of its
/// target, or whose <paramref name="NodeId"/> is <see langword="null"/> (unattributed, so treated
/// conservatively as affected) -- see <c>StageTransitionCoordinator</c>'s own remarks.</summary>
public sealed record Finding(
    Guid FindingId,
    SprintId SprintId,
    string Fingerprint,
    FindingSeverity Severity,
    FindingStatus Status,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments,
    IReadOnlyList<string> Evidence,
    FindingLocation? Location = null,
    NodeId? NodeId = null,
    StageRevision Revision = default,
    SupersededBy? Superseded = null);
