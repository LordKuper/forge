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
    public const string SprintDependencyNotTerminal = "sprint_dependency_not_terminal";
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
    ProjectRootStatus Project)
{
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
