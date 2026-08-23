namespace Forge.Domain;

/// <summary>
/// Plan section 8.1's three possible relationships between a sprint's current stage and an
/// assessed target: <see cref="Same"/> means the target already is the sprint's current stage
/// (nothing to commit); <see cref="Advance"/> means the target has not yet produced a result in
/// this revision; <see cref="Rewind"/> means the target already reached a terminal outcome
/// (`succeeded`/`failed`/`skipped`/`cancelled`) in this revision and moving there again would
/// reopen it. Both directions share one assessment contract (ADR 0046) rather than two.
/// </summary>
public enum StageTransitionDirection
{
    Same,
    Advance,
    Rewind,
}

/// <summary>
/// One named prerequisite category's evaluated result (ADR 0046's ten-category list). Never carries
/// the underlying predicate logic itself -- only its already-evaluated outcome, a localized message
/// key, and structured arguments a presentation layer can render without recreating the rule (plan
/// section 8.2: "The UI may explain these checks but may not calculate or override them").
/// </summary>
public sealed record StagePrerequisite(
    string Id,
    bool Satisfied,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments);

/// <summary>Fixed, stable prerequisite category ids (ADR 0046's ten-category list, mapped one for
/// one where the underlying codebase already records the fact, per that ADR's own reuse
/// requirement). Never presentation-facing on its own -- only <see cref="StagePrerequisite.Id"/>'s
/// value paired with a localized <see cref="StagePrerequisite.MessageKey"/>.</summary>
public static class StagePrerequisiteIds
{
    /// <summary>Every required (non-<see cref="NodeDefinition.Optional"/>) transitive predecessor of
    /// the target already has a successful, non-superseded result in the current revision. Emitted
    /// once per unmet predecessor.</summary>
    public const string PredecessorSuccess = "predecessor_success";

    /// <summary>A dependency tagged <see cref="NodeRole.TestWork"/> reachable through the target's
    /// predecessor closure has every <see cref="NodeRole.Confirmation"/> dependency of its own
    /// recorded as the latest, non-superseded <see cref="ConfirmationOutcome.Confirmed"/>.</summary>
    public const string ImplementationConfirmed = "implementation_confirmed";

    /// <summary>A dependency tagged <see cref="NodeRole.Review"/> reachable through the target's
    /// predecessor closure has its own <see cref="NodeRole.TestWork"/> dependency already
    /// `succeeded`/`skipped` and non-superseded.</summary>
    public const string TestWorkRecorded = "test_work_recorded";

    /// <summary>A dependency tagged <see cref="NodeRole.HumanApproval"/> or
    /// <see cref="NodeRole.Finalization"/> reachable through the target's predecessor closure has
    /// its own <see cref="NodeRole.Review"/> dependency already `succeeded` and non-superseded.
    /// </summary>
    public const string ReviewConverged = "review_converged";

    /// <summary>A dependency tagged <see cref="NodeRole.Finalization"/> reachable through the
    /// target's predecessor closure has its own <see cref="NodeRole.HumanApproval"/> dependency
    /// already `succeeded` and non-superseded.</summary>
    public const string HumanApproved = "human_approved";

    /// <summary>No <see cref="FindingStatus.Open"/>, non-superseded finding exists anywhere in the
    /// sprint -- reuses exactly the rule <c>SprintScheduler.EvaluateCompletionAsync</c> already
    /// applies to the completion gate.</summary>
    public const string NoBlockingFindings = "no_blocking_findings";

    /// <summary>The target's frozen execution profile (if its role has a model phase) still
    /// satisfies the project's *current* <c>models.allowed_models</c> policy -- reuses
    /// <c>ModelPolicyGate.IsAllowed</c> rather than recomputing it.</summary>
    public const string ProviderModelPolicy = "provider_model_policy";

    /// <summary>Every required predecessor's handoff (when one was recorded) is itself
    /// non-superseded -- there is no artifact store yet (<c>Forge.Application.IArtifactStore</c> is
    /// an empty marker), so this checks presence and supersession, not real digest resolution.
    /// </summary>
    public const string HandoffArtifacts = "handoff_artifacts";

    /// <summary>The sprint's integration worktree is clean (reuses
    /// <c>IWorktreeManager.IsDirtyAsync</c>), when a Git-isolated attempt is applicable.</summary>
    public const string GitIsolation = "git_isolation";

    /// <summary>The target's routed provider/model/surface key (if any) is not rate-limit-deferred,
    /// has retry budget remaining, and its circuit is not open -- reuses
    /// <c>RoutingLedger.GetResumeNotBeforeAsync</c>/<c>GetRetryBudgetAsync</c>/
    /// <c>GetCircuitBreakerAsync</c>.</summary>
    public const string RetryBudget = "retry_budget";

    /// <summary>No node currently has a live, non-terminal attempt. Never blocks a rewind on its own
    /// (see <see cref="ActiveOperationImpact"/>): the commit stops it first.</summary>
    public const string NoActiveOperation = "no_active_operation";
}

/// <summary>Plan section 8.1's "active-operation impact." <paramref name="StopRequired"/> is
/// <see langword="true"/> only when a rewind's commit would need to stop this operation first (plan
/// section 8.4 point 2); for an advance, a live active operation is instead reported as the
/// <see cref="StagePrerequisiteIds.NoActiveOperation"/> prerequisite, unsatisfied and blocking.
/// </summary>
public sealed record ActiveOperationImpact(
    bool HasActiveOperation,
    string? NodeId,
    Guid? AttemptId,
    bool StopRequired);

/// <summary>Plan section 8.1's "what would be superseded" -- populated only for a
/// <see cref="StageTransitionDirection.Rewind"/> assessment; <see langword="null"/> for
/// <see cref="StageTransitionDirection.Advance"/>/<see cref="StageTransitionDirection.Same"/> (ADR
/// 0046: one shared contract, this section optional/empty for the other directions rather than a
/// second contract).</summary>
public sealed record StageSupersessionSummary(
    IReadOnlyList<string> StageIds,
    IReadOnlyList<Guid> AttemptIds,
    int FindingCount,
    int DecisionCount,
    int ArtifactCount);

/// <summary>
/// Plan section 8.1's read-only `AssessStageTransition(sprintId, targetStageId)` result. Every field
/// is already-evaluated data; no presentation layer recreates the prerequisite policy from it (ADR
/// 0046). <see cref="Found"/> is <see langword="false"/> only when the project is not initialized,
/// the sprint does not exist, or <paramref name="TargetStageId"/> names no node in the sprint's own
/// frozen graph -- every other field is then default/empty and <see cref="DiagnosticCode"/> names
/// why. <see cref="AssessmentToken"/> is bound to the project, sprint, target, current revision, and
/// expected state version (plan section 8.5); <c>Forge.Application.StageTransitionCoordinator</c>
/// recomputes it fresh immediately before a commit and rejects a mismatch without side effects.
/// </summary>
public sealed record StageTransitionAssessment(
    bool Found,
    string DiagnosticCode,
    SprintId SprintId,
    string? SourceStageId,
    string? TargetStageId,
    StageTransitionDirection Direction,
    bool Allowed,
    IReadOnlyList<StagePrerequisite> SatisfiedPrerequisites,
    IReadOnlyList<StagePrerequisite> UnsatisfiedPrerequisites,
    ActiveOperationImpact ActiveOperation,
    StageSupersessionSummary? Supersession,
    bool ConfirmationRequired,
    long ExpectedStateVersion,
    StageRevision CurrentRevision,
    string? AssessmentToken)
{
    public static StageTransitionAssessment NotFound(SprintId sprintId, string diagnosticCode) =>
        new(
            false,
            diagnosticCode,
            sprintId,
            null,
            null,
            StageTransitionDirection.Same,
            false,
            [],
            [],
            new(false, null, null, false),
            null,
            false,
            0,
            default,
            null);
}
