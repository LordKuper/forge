namespace Forge.Domain;

/// <summary>The two independently counted review dimensions ADR 0006 requires: "One engine runs
/// design and implementation review with independent durable counters." Nothing in the built-in
/// graph (`Forge.Compiler.ImplementationCriticalGraphBuilder`) triggers one dimension over the
/// other yet — that decision belongs to whatever executor eventually drives the single `review`
/// node; this enum only gives the two counters a stable identity to be tracked by.</summary>
public enum ReviewDimension
{
    Design,
    Implementation,
}

/// <summary>Matches ADR 0006's distinct coverage/convergence rules: an internal reviewer emits a
/// coverage ledger and is re-dispatched once in the same iteration if it's incomplete; an external
/// reviewer's finding set is what the repeated-normalized-finding convergence gate watches.</summary>
public enum ReviewerKind
{
    Internal,
    External,
}

public enum ReviewOutcome
{
    Approved,
    ChangesRequested,
}

/// <summary>
/// The four components ADR 0006 names for cross-iteration finding-set comparison — "by file,
/// location, rule, and message fingerprint" — never derived from a <see cref="Finding.FindingId"/>
/// (opaque and per-recording) or <see cref="Finding.Fingerprint"/> alone (a sprint-scoped identity
/// hash that excludes location, built for a different purpose in
/// <c>SprintScheduler.RecordFindingAsync</c>). <see cref="Rule"/> is a finding's
/// <see cref="Finding.MessageKey"/> — the closest existing concept to "the rule that fired" this
/// codebase has, localization keys already being per-check by construction. Built by
/// <c>SprintScheduler.RecordReviewIterationAsync</c> from each <see cref="ReviewFindingDraft"/>,
/// reusing the same sprint-scoped fingerprint hash <see cref="Finding.Fingerprint"/> already uses.
/// </summary>
public sealed record NormalizedFindingKey(string? File, int? Line, string Rule, string MessageFingerprint);

/// <summary>
/// One internal reviewer's coverage claim: every file the sprint scoped it against, and every
/// rubric item (see <see cref="BuiltInRubric"/>) applicable to that scope. ADR 0006: "An incomplete
/// ledger invalidates that verdict and causes one fresh re-dispatch in the same iteration" — see
/// <c>ReviewConvergencePolicy.IsCoverageComplete</c>.
/// </summary>
public sealed record CoverageLedger(
    IReadOnlyList<string> ScopedFiles,
    IReadOnlyList<string> RubricItemIds,
    IReadOnlyList<string> CoveredFiles,
    IReadOnlyList<string> CoveredRubricItemIds);

/// <summary>
/// One reviewer's recorded verdict for one iteration of one <see cref="ReviewDimension"/>,
/// matching <c>review-iteration.schema.json</c>. <see cref="Iteration"/> is derived, not
/// caller-supplied — the count of prior records for the same (sprint, node, dimension) plus one,
/// the same pattern <c>SprintScheduler.StartAttemptAsync</c> already uses for attempt numbers.
/// <see cref="ExternalFindings"/> is populated only for <see cref="ReviewerKind.External"/> (an
/// internal reviewer's coverage claim, not its finding set, is what ADR 0006 tracks for it); this
/// item deliberately models one combined verdict per iteration rather than per-reviewer
/// aggregation within an iteration — see ADR 0015's "what stays deferred."
/// </summary>
public sealed record ReviewIterationRecord(
    Guid ReviewIterationId,
    SprintId SprintId,
    NodeId NodeId,
    ReviewDimension Dimension,
    ReviewerKind ReviewerKind,
    int Iteration,
    ReviewOutcome Outcome,
    IReadOnlyList<NormalizedFindingKey> ExternalFindings,
    CoverageLedger? Coverage,
    DateTimeOffset RecordedAt);

/// <summary>
/// One finding a review iteration reports, before it is known whether it lands above or below the
/// iteration's severity floor — the input shape <c>SprintScheduler.RecordReviewIterationAsync</c>
/// takes, mirroring <c>RecordFindingAsync</c>'s own raw-argument shape rather than requiring a
/// caller to pre-build a full <see cref="Finding"/> (which needs a sprint-scoped fingerprint only
/// the store can compute).
/// </summary>
public sealed record ReviewFindingDraft(
    FindingSeverity Severity,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments,
    IReadOnlyList<string> Evidence,
    FindingLocation? Location = null);
