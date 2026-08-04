using System.Globalization;

namespace Forge.Localization;

public interface ILocalizationCatalog
{
    string Resolve(string key, CultureInfo? culture = null);

    IReadOnlyCollection<string> SupportedCultures { get; }
}

/// <summary>
/// Binds the shared catalog to the language resolved during startup so surfaces never depend
/// on the ambient operating-system culture.
/// </summary>
public sealed class SurfaceText(ILocalizationCatalog catalog, CultureInfo culture)
{
    public CultureInfo Culture { get; } = culture;

    public string Resolve(string key) => catalog.Resolve(key, Culture);

    public static SurfaceText For(ILocalizationCatalog catalog, string languageTag) =>
        new(catalog, ToCulture(languageTag));

    /// <summary>Unknown or malformed tags fall back to English rather than failing startup.</summary>
    public static CultureInfo ToCulture(string languageTag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(languageTag);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en");
        }
    }
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
    public const string ModelsDescription = "ModelsDescription";
    public const string ProviderToolchainTitle = "ProviderToolchainTitle";
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
    public const string RecoverAction = "RecoverAction";
    public const string RecoveryCompleted = "RecoveryCompleted";
    public const string RecoveryFailed = "RecoveryFailed";
    public const string RecoveryNotNeeded = "RecoveryNotNeeded";
    public const string DiagnosticsTitle = "DiagnosticsTitle";
    public const string RecoverStartupRationale = "next.recover_startup.rationale";
    public const string InitializeProjectRationale = "next.initialize_project.rationale";
}
