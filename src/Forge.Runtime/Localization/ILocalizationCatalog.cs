using System.Globalization;

namespace Forge.Localization;

public interface ILocalizationCatalog
{
    string Resolve(string key, CultureInfo? culture = null);

    IReadOnlyCollection<string> SupportedCultures { get; }
}

public static class MessageKeys
{
    public const string AppDescription = "AppDescription";
    public const string AppTitle = "AppTitle";
    public const string StatusDescription = "StatusDescription";
    public const string StatusReady = "StatusReady";
    public const string InstallDescription = "InstallDescription";
    public const string InstallCompleted = "InstallCompleted";
    public const string InstallFailed = "InstallFailed";
    public const string UpdateDescription = "UpdateDescription";
    public const string UpdateCompleted = "UpdateCompleted";
    public const string UpdateFailed = "UpdateFailed";
    public const string DoctorDescription = "DoctorDescription";
    public const string InitDescription = "InitDescription";
    public const string NextDescription = "NextDescription";
    public const string ConfigDescription = "ConfigDescription";
    public const string StartupChecksTitle = "StartupChecksTitle";
    public const string StartupReady = "StartupReady";
    public const string StartupBlocked = "StartupBlocked";
    public const string StartupFailed = "StartupFailed";
    public const string ProjectRootLabel = "ProjectRootLabel";
    public const string ProjectInitialized = "ProjectInitialized";
    public const string ProjectNotInitialized = "ProjectNotInitialized";
    public const string InitConfirmationRequired = "InitConfirmationRequired";
    public const string InitCompleted = "InitCompleted";
    public const string InitAlreadyInitialized = "InitAlreadyInitialized";
    public const string InitFailed = "InitFailed";
    public const string SuggestedActionsTitle = "SuggestedActionsTitle";
    public const string NoSuggestedActions = "NoSuggestedActions";
    public const string ConfigurationTitle = "ConfigurationTitle";
    public const string ConfigurationUpdated = "ConfigurationUpdated";
    public const string ConfigurationRejected = "ConfigurationRejected";
    public const string RefreshAction = "RefreshAction";
    public const string InitializeAction = "InitializeAction";
    public const string ConfigurationSetAction = "ConfigurationSetAction";
    public const string CancelAction = "CancelAction";
    public const string RecoverStartupRationale = "next.recover_startup.rationale";
    public const string InitializeProjectRationale = "next.initialize_project.rationale";
}
