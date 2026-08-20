namespace Forge.Domain;

/// <summary>Matches `outcome` in docs/contracts/v1/schemas/test-work-result.schema.json. Mirrors
/// AGENTS.md's Quality rule: "identify the smallest risk-based set of new tests that protects the
/// scope... A no-new-test decision is allowed only when the change adds no behavior or existing
/// checks cover every material risk; justify it."</summary>
public enum TestWorkOutcome
{
    TestsAdded,
    NoNewTestsJustified,
}

/// <summary>
/// A test-work node's recorded decision: either new tests were added to protect the scope, or a
/// justified decision was made that none were needed. Matches
/// docs/contracts/v1/schemas/test-work-result.schema.json. Unlike <see cref="ConfirmationArtifact"/>,
/// review's own graph dependency on this node (<c>ImplementationCriticalGraphBuilder</c>) is
/// satisfied by the node reaching a terminal state alone — no artifact-content eligibility gate
/// reads this record the way <c>SprintScheduler.IsTestWorkEligibleAsync</c> reads a
/// <see cref="ConfirmationArtifact"/>; recording one is this node's whole job, not a precondition
/// for something downstream.
/// </summary>
public sealed record TestWorkArtifact(
    Guid TestWorkId,
    SprintId SprintId,
    NodeId NodeId,
    TestWorkOutcome Outcome,
    string Justification,
    DateTimeOffset RecordedAt);
