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
    public const string SprintDependencyInvalid = "sprint_dependency_invalid";
    public const string SprintDependencyNotPublished = "sprint_dependency_not_published";
    public const string SprintGraphInvalid = "sprint_graph_invalid";

    /// <summary>ADR 0008: "Routing candidates are the ordered intersection of the frozen project
    /// profile and the user-enabled set... An empty intersection blocks execution with a stable
    /// diagnostic rather than silently selecting another provider."</summary>
    public const string SprintProviderCandidatesEmpty = "sprint_provider_candidates_empty";
    public const string SprintNotRunning = "sprint_not_running";
    public const string NodeNotFound = "node_not_found";
    public const string NodeKindMismatch = "node_kind_mismatch";
    public const string NodeTransitionInvalid = "node_transition_invalid";
    public const string AttemptOwnershipMismatch = "attempt_ownership_mismatch";
    public const string AttemptTerminal = "attempt_terminal";
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
    public const string WorktreeUnavailable = "worktree_unavailable";
    public const string ControlCursorStale = "control_cursor_stale";

    /// <summary>ADR 0005: the Host owns every project mutation; a client that cannot reach or
    /// start one reports this instead of ever falling back to mutating `.forge/` locally.</summary>
    public const string HostUnavailable = "host_unavailable";
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
