namespace Forge.Application;

/// <summary>Stable machine-readable diagnostic codes shared by both surfaces.</summary>
public static class DiagnosticCodes
{
    public const string None = "none";
    public const string InternalError = "internal_error";
    public const string PlatformNotSupported = "platform_not_supported";
    public const string UpdateCheckDeferred = "update_check_deferred";
    public const string ProviderPreflightPending = "provider_preflight_pending";
    public const string ProviderUpdateFailed = "provider_update_failed";

    /// <summary>ADR 0008: "Missing authentication blocks model work with
    /// `provider_authentication_required`." Shares its literal value with
    /// <c>Forge.Providers.ProviderDiagnosticCodes.AuthenticationRequired</c> — same underlying
    /// cause, surfaced at both the per-provider and aggregate startup-check level.</summary>
    public const string ProviderAuthenticationRequired = "provider_authentication_required";

    /// <summary>ADR 0008: "a probe failure uses `provider_authentication_check_failed`." See
    /// <see cref="ProviderAuthenticationRequired"/>.</summary>
    public const string ProviderAuthenticationCheckFailed = "provider_authentication_check_failed";
    public const string ConfigurationInvalid = "configuration_invalid";
    public const string ConfigurationScopeViolation = "configuration_scope_violation";
    public const string ConfigurationKeyUnknown = "configuration_key_unknown";
    public const string ProjectRootNotAbsolute = "project_root_not_absolute";
    public const string ProjectRootMissing = "project_root_missing";
    public const string ProjectNotInitialized = "project_not_initialized";
    public const string ProjectDirectoryUnknown = "project_directory_unknown";
    public const string ProjectAlreadyInitialized = "project_already_initialized";
    public const string ConfirmationRequired = "confirmation_required";
    public const string SuggestionStale = "suggestion_stale";
    public const string SprintNotFound = "sprint_not_found";
    public const string SprintTransitionInvalid = "sprint_transition_invalid";
    public const string WorkflowEventConflict = "workflow_event_conflict";
    public const string RepositoryHeadUnavailable = "repository_head_unavailable";

    /// <summary>A new sprint's `HEAD` was resolvable (<see cref="RepositoryHeadUnavailable"/>
    /// covers that separately) but detached — no branch name to freeze as
    /// <c>SprintDefinition.DefaultBranch</c>, which finalization later needs to identify where a
    /// sprint's real changes land. Sprint creation fails closed rather than freezing a
    /// <see langword="null"/> the sprint could never finalize.</summary>
    public const string RepositoryDetachedHead = "repository_detached_head";

    /// <summary>Finalization refuses to merge into the project's own working directory
    /// (<c>projectRoot</c> itself, never a worktree) while it has uncommitted changes — the same
    /// "never touch a dirty tree" discipline every worktree-scoped git primitive already follows,
    /// applied here for the first time to the main checkout itself.</summary>
    public const string RepositoryDirty = "repository_dirty";

    /// <summary>Finalization refuses to check out a different branch in the project's own working
    /// directory — it only ever fast-forward-merges into whatever branch is already checked out,
    /// and only when that already matches the sprint's frozen <c>DefaultBranch</c>.</summary>
    public const string RepositoryBranchMismatch = "repository_branch_mismatch";

    /// <summary>Finalization has nothing to merge into: this sprint's own
    /// `SprintDefinition.DefaultBranch` is <see langword="null"/> (frozen before this field
    /// existed). Checked before the finalization attempt even starts, rather than starting one that
    /// could never succeed.</summary>
    public const string SprintDefaultBranchUnavailable = "sprint_default_branch_unavailable";
    public const string SprintDependencyInvalid = "sprint_dependency_invalid";
    public const string SprintDependencyNotPublished = "sprint_dependency_not_published";
    public const string SprintGraphInvalid = "sprint_graph_invalid";

    /// <summary>ADR 0008: "Routing candidates are the ordered intersection of the frozen project
    /// profile and the user-enabled set... An empty intersection blocks execution with a stable
    /// diagnostic rather than silently selecting another provider."</summary>
    public const string SprintProviderCandidatesEmpty = "sprint_provider_candidates_empty";
    public const string SprintNotRunning = "sprint_not_running";
    public const string NodeNotFound = "node_not_found";

    /// <summary>The named node exists but is not the <see cref="Forge.Domain.NodeKind"/> or
    /// <see cref="Forge.Domain.NodeRole"/> the requested operation requires (e.g. starting an
    /// attempt on a <see cref="Forge.Domain.NodeKind.HumanGate"/>, or recording a confirmation
    /// against a node not tagged <see cref="Forge.Domain.NodeRole.Confirmation"/>).</summary>
    public const string NodeKindMismatch = "node_kind_mismatch";
    public const string NodeTransitionInvalid = "node_transition_invalid";

    /// <summary>Category 11 (docs/contracts/v1/README.md): "Durable workflow cannot safely
    /// advance." Returned when a caller tries to start a <see cref="Forge.Domain.NodeRole.TestWork"/>
    /// node whose <see cref="Forge.Domain.NodeRole.Confirmation"/> dependency has not recorded a
    /// `Confirmed` <see cref="Forge.Domain.ConfirmationArtifact"/> yet.</summary>
    public const string WorkflowBlocked = "workflow_blocked";

    /// <summary>Category 11: "Review requires a human convergence decision." Recording a review
    /// iteration whose new iteration count would exceed the cumulative severity-floor budget
    /// (ADR 0006: "an iteration-limit human gate before iteration 15") blocks the sprint with this
    /// code instead of silently applying an ever-rising floor.</summary>
    public const string ReviewIterationLimit = "review_iteration_limit";

    /// <summary>Category 11: "External review repeated an identical normalized finding set."
    /// ADR 0006: "Two consecutive identical [sets] by file, location, rule, and message
    /// fingerprint create a review-convergence human gate."</summary>
    public const string ReviewRepeatedFindings = "review_repeated_findings";
    public const string AttemptOwnershipMismatch = "attempt_ownership_mismatch";
    public const string AttemptTerminal = "attempt_terminal";

    /// <summary>Category 11 (plan section 7.3): `StopCurrentOperation` rejects a stop request when
    /// the sprint has no active operation at all -- it is not <see cref="Forge.Domain.SprintState.Running"/>,
    /// so no attempt can be the sprint's exact active operation.</summary>
    public const string NoActiveOperation = "no_active_operation";

    /// <summary>Category 11 (plan section 7.3): "the active attempt changed before validation" --
    /// the requested attempt is no longer (or never was) the node's current, running attempt, so
    /// stopping it would either do nothing or, worse, target the wrong operation.</summary>
    public const string ActiveOperationChanged = "active_operation_changed";
    public const string FindingNotFound = "finding_not_found";
    public const string WorkflowRecordInvalid = "workflow_record_invalid";
    public const string WorkflowTransitionInvalid = "workflow_transition_invalid";
    public const string WorkflowStoreBusy = "workflow_store_busy";
    public const string WorkflowLogCorrupted = "workflow_log_corrupted";
    public const string WorktreeCreateFailed = "worktree_create_failed";
    public const string WorktreeResetFailed = "worktree_reset_failed";
    public const string WorktreeIntegrationDiverged = "worktree_integration_diverged";
    public const string WorktreeRebaseConflict = "worktree_rebase_conflict";
    public const string WorktreeBaseMismatch = "worktree_base_mismatch";
    public const string WorktreeCommitInvalid = "worktree_commit_invalid";
    public const string WorktreeCommitFailed = "worktree_commit_failed";

    /// <summary>The review node executor's own read-only diff primitive
    /// (<see cref="Forge.Application.IWorktreeManager.DiffAsync"/>) failed — `git diff` itself
    /// returned a non-zero exit, distinct from <see cref="WorktreeCommitInvalid"/> (a malformed
    /// commit-id argument, checked before any process ever runs).</summary>
    public const string WorktreeDiffFailed = "worktree_diff_failed";

    public const string WorktreeUnavailable = "worktree_unavailable";

    /// <summary>The implementation node executor's own outcome: the provider run succeeded (a
    /// schema-valid terminal result, no timeout, no failure) but left the attempt worktree exactly
    /// as clean as it started — nothing to commit for a role whose whole job is producing an edit.
    /// Distinct from every <c>ProviderDiagnosticCodes</c> value because the provider itself never
    /// reported failing.</summary>
    public const string ImplementationNoChanges = "implementation_no_changes";

    public const string ControlCursorStale = "control_cursor_stale";

    /// <summary>ADR 0006's durable rate-limit wait (Stage 11, P11.48-P11.55):
    /// <c>RoutingLedger.DecideAsync</c> found the node's provider/model/surface key still
    /// unroutable through a previously-recorded `resume_not_before`.</summary>
    public const string RoutingDeferred = "routing_deferred";

    /// <summary>The sprint's shared routing retry budget (<c>RoutingLedger.DefaultRetryBudget</c>)
    /// is exhausted — never guessed into a delay; this blocks and requires normal recovery, per ADR
    /// 0006's "quota exhaustion without a safe retry time... is not guessed into a delay."</summary>
    public const string RoutingBudgetExhausted = "routing_budget_exhausted";

    /// <summary>The node's provider/model/surface key's circuit breaker
    /// (<c>RoutingLedger.GetCircuitBreakerAsync</c>) is currently open.</summary>
    public const string RoutingCircuitOpen = "routing_circuit_open";

    /// <summary>ADR 0006's bounded supersession-instruction artifact exceeded its maximum length.</summary>
    public const string SupersessionInstructionTooLong = "supersession_instruction_too_long";

    /// <summary>The supersession instruction source (a file path, or standard input) could not be
    /// read at all — missing file, permission denied, or an invalid path — distinct from
    /// <see cref="SupersessionInstructionTooLong"/>, which means it *was* read but exceeded the
    /// bound.</summary>
    public const string SupersessionInstructionUnreadable = "supersession_instruction_unreadable";

    /// <summary>ADR 0006's bounded supersession-instruction artifact was empty or whitespace-only —
    /// the whole point of a human-initiated supersession is recording why, so an instruction with no
    /// actual content is rejected the same way an over-length one is.</summary>
    public const string SupersessionInstructionRequired = "supersession_instruction_required";

    /// <summary>ADR 0054's bounded user-message timeline artifact exceeded
    /// <see cref="SprintScheduler.MaxUserMessageLength"/>.</summary>
    public const string UserMessageTooLong = "user_message_too_long";

    /// <summary>ADR 0054's bounded user-message timeline artifact was empty or whitespace-only.</summary>
    public const string UserMessageRequired = "user_message_required";

    /// <summary>ADR 0005: the Host owns every project mutation; a client that cannot reach or
    /// start one reports this instead of ever falling back to mutating `.forge/` locally.</summary>
    public const string HostUnavailable = "host_unavailable";

    /// <summary>ADR 0053: the connected Host's own handshake-advertised capability set does not
    /// include the one this request needs -- rejected client-side before the request ever reaches
    /// the wire, distinct from <see cref="HostUnavailable"/> (no Host reachable at all).</summary>
    public const string CapabilityNotSupported = "capability_not_supported";

    /// <summary>`forge confirm`'s <c>--evidence-kind</c> was not one of <c>inspection</c>/
    /// <c>execution</c>/<c>existing-check</c> (matching `confirmation-result.schema.json`'s own
    /// vocabulary).</summary>
    public const string ConfirmationEvidenceKindInvalid = "confirmation_evidence_kind_invalid";

    /// <summary>`forge confirm`'s <c>--definition-of-done</c>/<c>--evidence</c> was empty or
    /// whitespace-only. Checked before <see cref="SprintScheduler.ConfirmNodeAsync"/>
    /// starts the node's attempt, not only by `confirmation-result.schema.json`'s own `minLength: 1`
    /// afterward -- the schema check alone would still leave the attempt started (and thus the node
    /// `running`) before rejecting the record.</summary>
    public const string ConfirmationTextRequired = "confirmation_text_required";

    /// <summary>`forge test-work`'s <c>--justification</c> was empty or whitespace-only. Checked
    /// before <see cref="SprintScheduler.RecordTestWorkAsync"/> starts the node's attempt -- the
    /// same reason <see cref="ConfirmationTextRequired"/> is checked CLI-side rather than left to
    /// `test-work-result.schema.json`'s own `minLength: 1` alone.</summary>
    public const string TestWorkJustificationRequired = "test_work_justification_required";

    /// <summary>ADR 0011: the project's resolved `artifacts.language.agent_facing` is not in
    /// <c>ILocalizationCatalog.SupportedCultures</c>; integration generation/install/remove refuses
    /// rather than silently falling back to English.</summary>
    public const string IntegrationLanguageUnsupported = "integration_language_unsupported";

    /// <summary>ADR 0011: install or remove completed for every Forge-owned artifact, but at least
    /// one enabled provider's target file exists and is not Forge-owned (no recognizable ownership
    /// marker) — left untouched rather than overwritten or deleted. See the per-artifact
    /// <c>IntegrationArtifactOutcome.Refused</c> entries for which one(s).</summary>
    public const string IntegrationPartiallyRefused = "integration_partially_refused";

    /// <summary>ADR 0023: `forge gate approve|reject` and `forge attempt supersede` refuse to run
    /// when standard output is not an interactive terminal — the first real technical control behind
    /// ADR 0005/0019's "human-only" requirement, previously enforced by mandatory confirmation alone.
    /// Reserved since Stage 0 (`docs/contracts/v1/README.md`'s `permission_denied`/exit 8) and
    /// unimplemented until this ADR.</summary>
    public const string PermissionDenied = "permission_denied";

    /// <summary>ADR 0042: a frozen provider's <c>ILlmProvider.DefaultModel</c> is not listed in the
    /// project's configured <c>models.allowed_models</c> policy for that provider. Checked in
    /// <see cref="SprintOrchestrator.CreateSprintAsync"/> immediately after <c>frozenProviders</c> is
    /// resolved, before any event is appended -- the same fail-closed placement
    /// <see cref="SprintProviderCandidatesEmpty"/> already uses for the adjacent "no routable
    /// provider" case.</summary>
    public const string ModelPolicyViolation = "model_policy_violation";

    /// <summary>ADR 0042 (round 1 review of PR #87): a `models.allowed_models` entry names a
    /// provider id that matches none of the project's enabled providers -- a likely typo or a
    /// stale entry for a provider that was since renamed or disabled. Reported only by `forge
    /// eval`'s <see cref="EvaluationArea.ModelPolicy"/> area; <see cref="ModelPolicyGate.IsAllowed"/>
    /// and the <c>SprintOrchestrator.CreateSprintAsync</c> gate are unaffected -- an unmatched entry
    /// still enforces no restriction there, by design.</summary>
    public const string ModelPolicyProviderUnknown = "model_policy_provider_unknown";

    /// <summary>Plan section 8.4 point 1: a rewind's bounded operator reason was empty or
    /// whitespace-only. Checked before <c>StageTransitionCoordinator.MoveAsync</c> commits anything
    /// -- the same "invalid input, nothing recorded" placement <see cref="TestWorkJustificationRequired"/>
    /// and <see cref="ConfirmationTextRequired"/> already use for their own mandatory text fields.
    /// Never applies to an advance, which plan section 8.3 does not require a reason for.</summary>
    public const string StageTransitionReasonRequired = "stage_transition_reason_required";

    /// <summary>Round 2 review of PR #96 (critical): the sprint carries an unconverged rewind
    /// (<see cref="Forge.Domain.SprintSnapshot.PendingRewindTargetStageId"/> is set -- a Host crashed
    /// partway through <c>StageTransitionCoordinator.CommitRewindAsync</c>, after its step 2 recorded
    /// the revision but before its final convergence marker landed). <c>AssessStageTransition</c>
    /// reports this instead of silently misclassifying <see cref="Forge.Domain.StageTransitionDirection"/>
    /// from now-drifted node state; <c>SprintScheduler.CompleteSprintAsync</c> refuses to finalize
    /// while it holds, since a sprint can reach `ready_to_finalize` while the rewound stages have done
    /// zero real work. Cleared automatically the next time any `MoveSprintToStage`/`AssessStageTransition`
    /// call resumes and converges the in-flight rewind -- no separate recovery action exists or is
    /// needed.</summary>
    public const string StageTransitionRewindInProgress = "stage_transition_rewind_in_progress";

    /// <summary>Plan section 6.1: `forge project add` targeting a root whose manifest `project_id`
    /// is already cataloged. Never thrown from a relink/alias/select/remove call, which key on an
    /// already-known id instead.</summary>
    public const string ProjectCatalogEntryExists = "project_catalog_entry_exists";

    /// <summary>Plan section 6.1: `forge project remove|relink|alias|select` naming a project id the
    /// local catalog has no row for.</summary>
    public const string ProjectCatalogEntryNotFound = "project_catalog_entry_not_found";

    /// <summary>Plan section 6.1: "a moved project can be relinked after its manifest project ID is
    /// verified" -- the new root's own manifest `project_id` does not match the catalog entry being
    /// relinked.</summary>
    public const string ProjectCatalogRelinkMismatch = "project_catalog_relink_mismatch";

    /// <summary>`forge project alias` given a display alias longer than
    /// <see cref="ProjectCatalogStore.MaxAliasLength"/>.</summary>
    public const string ProjectCatalogAliasTooLong = "project_catalog_alias_too_long";

    /// <summary>`forge project select --route` given a route string longer than
    /// <see cref="ProjectCatalogStore.MaxRouteLength"/>.</summary>
    public const string ProjectCatalogRouteTooLong = "project_catalog_route_too_long";

    /// <summary>The local `catalog.json` (and its `.previous` recovery copy, if one exists) are both
    /// unreadable -- a corrupt or malformed file that neither parses nor recovers. Reported instead
    /// of letting the underlying parse failure propagate as an unhandled exception out of every
    /// `forge project *`/`forge workspace summary` command (round 1 review of PR #97).</summary>
    public const string ProjectCatalogUnreadable = "project_catalog_unreadable";

    /// <summary>Slice 6's sprint-workspace draft preservation given text longer than
    /// <see cref="ProjectCatalogStore.MaxDraftLength"/>.</summary>
    public const string ProjectCatalogDraftTooLong = "project_catalog_draft_too_long";
}

public enum StartupState
{
    Ready,
    Blocked,
    Failed,
}

public enum StartupCheckId
{
    UserConfiguration,
    Language,
    Platform,
    UpdateStrategy,
    Release,
    Providers,
    ProjectRoot,
    ProjectConfiguration,
}

public enum StartupCheckState
{
    Passed,
    Skipped,
    Blocked,
    Failed,
}

public sealed record StartupCheck(StartupCheckId Id, StartupCheckState State, string DiagnosticCode)
{
    public static StartupCheck Passed(StartupCheckId id) =>
        new(id, StartupCheckState.Passed, DiagnosticCodes.None);
}

public sealed record LanguageSelection(string Ui, string Interaction, string Llm)
{
    public static LanguageSelection Fallback { get; } = new("en", "en", "en");
}

public sealed record ProjectRootStatus(
    string Root,
    bool Exists,
    bool Initialized,
    bool Unknown,
    string DiagnosticCode);

public sealed record StartupStatus(
    StartupState State,
    IReadOnlyList<StartupCheck> Checks,
    LanguageSelection Language,
    ProjectRootStatus Project,
    string SchemaVersion)
{
    /// <summary>The versioned `startup-check.schema.json` contract this record's JSON
    /// serialization (<see cref="StatusJson"/>) satisfies.</summary>
    public const string ContractVersion = "1.0.0";

    /// <summary>Sprint work is fail-closed while any startup check is unresolved.</summary>
    public bool AllowsSprintWork => State == StartupState.Ready;

    /// <summary>A failed check leaves recovery as the only safe action; mutations are refused.</summary>
    public bool AllowsProjectMutation => State != StartupState.Failed;

    public StartupCheck? FirstFailure =>
        Checks.FirstOrDefault(check => check.State == StartupCheckState.Failed);
}

public sealed record PlatformPreflightResult(
    string OperatingSystem,
    string Architecture,
    bool StrategyResolved,
    string DiagnosticCode);

/// <summary>Reports the resolved platform and update strategy without mutating anything.</summary>
public interface IPlatformPreflight
{
    PlatformPreflightResult Check();
}

/// <summary>Used when no platform composition is registered; keeps startup fail-closed.</summary>
public sealed class UnsupportedPlatformPreflight : IPlatformPreflight
{
    public PlatformPreflightResult Check() =>
        new("unknown", "unknown", false, DiagnosticCodes.PlatformNotSupported);
}
