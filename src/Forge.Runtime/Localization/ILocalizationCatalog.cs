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
    public const string SprintIdLabel = "SprintIdLabel";
    public const string ConfigurationKeyLabel = "ConfigurationKeyLabel";
    public const string ConfigurationValueLabel = "ConfigurationValueLabel";
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
    public const string SprintsTitle = "SprintsTitle";
    public const string NoSprints = "NoSprints";
    public const string SprintDetailsTitle = "SprintDetailsTitle";
    public const string NodesLabel = "NodesLabel";
    public const string AttemptsLabel = "AttemptsLabel";
    public const string FindingsLabel = "FindingsLabel";
    public const string RoutingLabel = "RoutingLabel";
    public const string EventsDescription = "EventsDescription";
    public const string EventsTitle = "EventsTitle";
    public const string NoEvents = "NoEvents";
    public const string TreeDescription = "TreeDescription";
    public const string SprintDescription = "SprintDescription";
    public const string SprintInspectDescription = "SprintInspectDescription";
    public const string SprintCreateDescription = "SprintCreateDescription";
    public const string SprintCreated = "SprintCreated";
    public const string SprintRunDescription = "SprintRunDescription";
    public const string SprintAdvanced = "SprintAdvanced";
    public const string SprintAdvancedUnknownState = "SprintAdvancedUnknownState";
    public const string SprintResumeDescription = "SprintResumeDescription";
    public const string SprintResumed = "SprintResumed";
    public const string SprintCancelDescription = "SprintCancelDescription";
    public const string SprintCancelled = "SprintCancelled";
    public const string IntegrationHeaderPreamble = "IntegrationHeaderPreamble";
    public const string IntegrationTestingInvariant = "IntegrationTestingInvariant";
    public const string IntegrationDescription = "IntegrationDescription";
    public const string IntegrationSkillDescription = "IntegrationSkillDescription";
    public const string IntegrationGenerateDescription = "IntegrationGenerateDescription";
    public const string IntegrationInstallDescription = "IntegrationInstallDescription";
    public const string IntegrationRemoveDescription = "IntegrationRemoveDescription";
    public const string IntegrationTitle = "IntegrationTitle";
    public const string NoIntegrationArtifacts = "NoIntegrationArtifacts";
    public const string GateDescription = "GateDescription";
    public const string GateApproveDescription = "GateApproveDescription";
    public const string GateRejectDescription = "GateRejectDescription";
    public const string GateResolved = "GateResolved";
    public const string GateResolutionFailed = "GateResolutionFailed";
    public const string GateNodeIdLabel = "GateNodeIdLabel";
    public const string GateApproveAction = "GateApproveAction";
    public const string GateRejectAction = "GateRejectAction";
    public const string GateConfirmationRequired = "GateConfirmationRequired";
    public const string GateActiveSprintPlaceholder = "GateActiveSprintPlaceholder";
    public const string GateSprintAmbiguous = "GateSprintAmbiguous";
    public const string AttemptDescription = "AttemptDescription";
    public const string AttemptSupersedeDescription = "AttemptSupersedeDescription";
    public const string AttemptSuperseded = "AttemptSuperseded";
    public const string AttemptSupersedeFailed = "AttemptSupersedeFailed";
    public const string AttemptIdLabel = "AttemptIdLabel";
    public const string AttemptInstructionLabel = "AttemptInstructionLabel";
    public const string AttemptSupersedeAction = "AttemptSupersedeAction";
    public const string AttemptSupersedeConfirmationRequired = "AttemptSupersedeConfirmationRequired";
    public const string AttemptIdRequired = "AttemptIdRequired";
    public const string AttemptIdMissingPlaceholder = "AttemptIdMissingPlaceholder";
    public const string AttemptSupersedeSprintAmbiguous = "AttemptSupersedeSprintAmbiguous";
    public const string AttemptInstructionRequired = "AttemptInstructionRequired";
    public const string NotificationAwaitingHumanTitle = "NotificationAwaitingHumanTitle";
    public const string NotificationBlockedTitle = "NotificationBlockedTitle";
    public const string NotificationFailedTitle = "NotificationFailedTitle";
    public const string NotificationCompletedTitle = "NotificationCompletedTitle";
    public const string NotificationSprintLabel = "NotificationSprintLabel";
}
