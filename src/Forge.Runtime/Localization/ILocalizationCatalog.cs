using System.Globalization;
using Forge.Domain;

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
    public const string EvalDescription = "EvalDescription";
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
    public const string ConfirmDescription = "ConfirmDescription";
    public const string ConfirmConfirmedDescription = "ConfirmConfirmedDescription";
    public const string ConfirmNotConfirmedDescription = "ConfirmNotConfirmedDescription";
    public const string ConfirmRecorded = "ConfirmRecorded";
    public const string TestWorkDescription = "TestWorkDescription";
    public const string TestWorkAddedDescription = "TestWorkAddedDescription";
    public const string TestWorkNoNewTestsDescription = "TestWorkNoNewTestsDescription";
    public const string TestWorkRecorded = "TestWorkRecorded";
    public const string FinalizeDescription = "FinalizeDescription";
    public const string SprintFinalized = "SprintFinalized";
    public const string ConfirmFailed = "ConfirmFailed";
    public const string ConfirmNodeIdLabel = "ConfirmNodeIdLabel";
    public const string ConfirmDefinitionOfDoneLabel = "ConfirmDefinitionOfDoneLabel";
    public const string ConfirmEvidenceKindLabel = "ConfirmEvidenceKindLabel";
    public const string ConfirmEvidenceLabel = "ConfirmEvidenceLabel";
    public const string ConfirmConfirmedAction = "ConfirmConfirmedAction";
    public const string ConfirmNotConfirmedAction = "ConfirmNotConfirmedAction";
    public const string ConfirmConfirmationRequired = "ConfirmConfirmationRequired";
    public const string ConfirmSprintAmbiguous = "ConfirmSprintAmbiguous";
    public const string ConfirmDefinitionOfDoneRequired = "ConfirmDefinitionOfDoneRequired";
    public const string ConfirmEvidenceRequired = "ConfirmEvidenceRequired";
    public const string TestWorkFailed = "TestWorkFailed";
    public const string TestWorkNodeIdLabel = "TestWorkNodeIdLabel";
    public const string TestWorkJustificationLabel = "TestWorkJustificationLabel";
    public const string TestWorkAddedAction = "TestWorkAddedAction";
    public const string TestWorkNoNewTestsAction = "TestWorkNoNewTestsAction";
    public const string TestWorkConfirmationRequired = "TestWorkConfirmationRequired";
    public const string TestWorkSprintAmbiguous = "TestWorkSprintAmbiguous";
    public const string TestWorkJustificationRequired = "TestWorkJustificationRequired";
    public const string FinalizeFailed = "FinalizeFailed";
    public const string FinalizeNodeIdLabel = "FinalizeNodeIdLabel";
    public const string FinalizeAction = "FinalizeAction";
    public const string FinalizeConfirmationRequired = "FinalizeConfirmationRequired";
    public const string FinalizeSprintAmbiguous = "FinalizeSprintAmbiguous";
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
    public const string AttemptStopDescription = "AttemptStopDescription";
    public const string AttemptStopped = "AttemptStopped";
    public const string SprintAssessStageDescription = "SprintAssessStageDescription";
    public const string SprintMoveStageDescription = "SprintMoveStageDescription";
    public const string SprintStageMoved = "SprintStageMoved";
    public const string NotificationAwaitingHumanTitle = "NotificationAwaitingHumanTitle";
    public const string NotificationBlockedTitle = "NotificationBlockedTitle";
    public const string NotificationFailedTitle = "NotificationFailedTitle";
    public const string NotificationCompletedTitle = "NotificationCompletedTitle";
    public const string NotificationSprintLabel = "NotificationSprintLabel";
    public const string EventsPollAction = "EventsPollAction";
    public const string IntegrationGenerateAction = "IntegrationGenerateAction";
    public const string IntegrationInstallAction = "IntegrationInstallAction";
    public const string IntegrationRemoveAction = "IntegrationRemoveAction";
    public const string SprintCreateAction = "SprintCreateAction";
    public const string SprintRunAction = "SprintRunAction";
    public const string SprintResumeAction = "SprintResumeAction";
    public const string SprintCancelAction = "SprintCancelAction";
    public const string SprintCancelConfirmationRequired = "SprintCancelConfirmationRequired";
    public const string SprintManageFailed = "SprintManageFailed";
    public const string SprintManageSprintAmbiguous = "SprintManageSprintAmbiguous";

    /// <summary>ADR 0057: the create-sprint title field -- the CLI's `--title` description and the
    /// Desktop create-sprint entry's placeholder/accessible name.</summary>
    public const string SprintTitleLabel = "SprintTitleLabel";

    /// <summary>ADR 0057: the presentation-only display fallback for a sprint with no frozen title
    /// (see <c>Forge.Desktop.Presentation.SprintDisplayTitle</c>). `{0}` is the sprint's
    /// creation sequence number.</summary>
    public const string SprintUntitledFallback = "SprintUntitledFallback";

    /// <summary>The workspace redesign's sprint-status label for <see
    /// cref="Forge.Domain.SprintState.Paused"/> (plan section 4.1: "Every status has text and an
    /// accessible icon"). Not yet rendered anywhere -- no surface displays sprint state through a
    /// per-state label today (CLI/Desktop render the raw snake_case value); this key exists so the
    /// later slice that adds one has an already-reviewed, parity-checked label to reuse.</summary>
    public const string SprintStatePaused = "SprintStatePaused";

    public const string WorkspaceDescription = "WorkspaceDescription";
    public const string WorkspaceSummaryDescription = "WorkspaceSummaryDescription";
    public const string WorkspaceActionsDescription = "WorkspaceActionsDescription";
    public const string SprintTimelineDescription = "SprintTimelineDescription";

    /// <summary>Post-release timeline gap closure (ADR 0054): `forge sprint message` and its
    /// Desktop composer counterpart.</summary>
    public const string SprintMessageDescription = "SprintMessageDescription";
    public const string SprintMessagePosted = "SprintMessagePosted";
    public const string SprintMessagePostFailed = "SprintMessagePostFailed";
    public const string ProjectDescription = "ProjectDescription";
    public const string ProjectAddDescription = "ProjectAddDescription";
    public const string ProjectRemoveDescription = "ProjectRemoveDescription";
    public const string ProjectRelinkDescription = "ProjectRelinkDescription";
    public const string ProjectAliasDescription = "ProjectAliasDescription";
    public const string ProjectListDescription = "ProjectListDescription";
    public const string ProjectSelectDescription = "ProjectSelectDescription";
    public const string ProjectAdded = "ProjectAdded";
    public const string ProjectRemoved = "ProjectRemoved";
    public const string ProjectRelinked = "ProjectRelinked";
    public const string ProjectAliasSet = "ProjectAliasSet";
    public const string ProjectSelected = "ProjectSelected";

    /// <summary>The empty-catalog message for `forge project list` and `forge workspace summary`
    /// (round 1 review of PR #97): distinct from <see cref="NoSprints"/>, which is about a sprint
    /// list within one already-identified project, not the zero-projects-in-the-catalog case these
    /// two commands report.</summary>
    public const string NoProjects = "NoProjects";

    // Desktop workspace shell (Slice 5): sidebar, project overview, Forge/project settings, and the
    // sprint-workspace stub route. Gate/confirm/test-work/finalize/supersede/events/sprint-lifecycle
    // controls reuse the existing keys above verbatim -- SprintWorkspaceViewModel only re-scopes
    // MainPageViewModel's own already-localized capabilities, it adds no new interaction text.
    public const string SprintReadyToFinalizeReason = "SprintReadyToFinalizeReason";

    /// <summary>"No verified quota signal exists" (<see cref="Forge.Providers.ProviderQuotaAvailability.Unknown"/>)
    /// -- deliberately not named "Unavailable": <see cref="Forge.Providers.ProviderQuotaAvailability.Unavailable"/>
    /// means "quota is exhausted" (see <see cref="QuotaStatusDepleted"/>), a different concept this
    /// key must never be confused with (PR #100 review).</summary>
    public const string QuotaStatusUnknown = "QuotaStatusUnknown";
    public const string SettingsLanguageUnsupported = "SettingsLanguageUnsupported";
    public const string SettingsUnknownProvider = "SettingsUnknownProvider";
    public const string SettingsTokenBudgetInvalid = "SettingsTokenBudgetInvalid";
    public const string SidebarAddProjectAction = "SidebarAddProjectAction";
    public const string SidebarAddProjectPathLabel = "SidebarAddProjectPathLabel";
    public const string SidebarCollapseAction = "SidebarCollapseAction";
    public const string SidebarExpandAction = "SidebarExpandAction";
    public const string SidebarForgeSettingsAction = "SidebarForgeSettingsAction";
    public const string SidebarHistoryLabel = "SidebarHistoryLabel";
    public const string SidebarRemoveProjectAction = "SidebarRemoveProjectAction";
    public const string SidebarNoProjectsHint = "SidebarNoProjectsHint";

    /// <summary>Plan 12.1 final-sweep gap 1: accessible name for the per-project chevron that
    /// hides/shows that project's active-sprint list -- distinct from
    /// <see cref="SidebarCollapseAction"/>/<see cref="SidebarExpandAction"/>, which govern the whole
    /// sidebar rail instead.</summary>
    public const string SidebarProjectCollapseSprintsAction = "SidebarProjectCollapseSprintsAction";
    public const string SidebarProjectExpandSprintsAction = "SidebarProjectExpandSprintsAction";

    /// <summary>Same shape as <see cref="SidebarCollapseSaveFailed"/>, for a failed write of one
    /// project's own sprint-list disclosure state instead of the whole sidebar's.</summary>
    public const string SidebarProjectSprintsSaveFailed = "SidebarProjectSprintsSaveFailed";

    /// <summary>PR #103 review finding 1: the collapse/expand toggle discarded
    /// <c>Forge.Application.ConfigurationWriteResult</c> and silently re-rendered the unchanged
    /// (pre-toggle) state on a failed write. Same shape as <see cref="ProjectAddFailed"/>/
    /// <see cref="ProjectRemoveFailed"/> -- resolved through <c>WorkspaceShellPage.Message</c> with
    /// the write's <c>DiagnosticCode</c> appended.</summary>
    public const string SidebarCollapseSaveFailed = "SidebarCollapseSaveFailed";

    /// <summary>PR #105 review finding 4c: the sprint workspace's debounced scroll-position write
    /// used to be fire-and-forget with its <c>ProjectCatalogResult</c> silently discarded -- the only
    /// catalog/config write in this shell with no failure notice at all. Same shape as
    /// <see cref="SidebarProjectSprintsSaveFailed"/>/<see cref="SidebarCollapseSaveFailed"/>.</summary>
    public const string SprintScrollPositionSaveFailed = "SprintScrollPositionSaveFailed";
    public const string WorkspaceEmptyStateTitle = "WorkspaceEmptyStateTitle";
    public const string ProjectOverviewTitle = "ProjectOverviewTitle";
    public const string ProjectOverviewActiveSprintsTitle = "ProjectOverviewActiveSprintsTitle";
    public const string ProjectOverviewHistoryTitle = "ProjectOverviewHistoryTitle";
    public const string ForgeSettingsTitle = "ForgeSettingsTitle";
    public const string ForgeSettingsLanguageGroupTitle = "ForgeSettingsLanguageGroupTitle";
    public const string ForgeSettingsSafetyGroupTitle = "ForgeSettingsSafetyGroupTitle";
    public const string ForgeSettingsProvidersGroupTitle = "ForgeSettingsProvidersGroupTitle";
    public const string ForgeSettingsNotificationsGroupTitle = "ForgeSettingsNotificationsGroupTitle";
    public const string ForgeSettingsLanguageUiLabel = "ForgeSettingsLanguageUiLabel";
    public const string ForgeSettingsLanguageInteractionLabel = "ForgeSettingsLanguageInteractionLabel";
    public const string ForgeSettingsLanguageLlmLabel = "ForgeSettingsLanguageLlmLabel";
    public const string ForgeSettingsInheritOption = "ForgeSettingsInheritOption";
    public const string ForgeSettingsConfirmDestructiveLabel = "ForgeSettingsConfirmDestructiveLabel";

    /// <summary>Plan 5.1's required "mandatory-gate disclaimer" for
    /// <see cref="ForgeSettingsConfirmDestructiveLabel"/>'s row (PR #98 review finding 10): human,
    /// stop, and rewind confirmations are never bypassed by this setting.</summary>
    public const string ForgeSettingsConfirmDestructiveDisclaimer = "ForgeSettingsConfirmDestructiveDisclaimer";
    public const string ForgeSettingsProvidersEnabledLabel = "ForgeSettingsProvidersEnabledLabel";
    public const string ForgeSettingsNotificationsEnabledLabel = "ForgeSettingsNotificationsEnabledLabel";
    public const string SettingsSaveAction = "SettingsSaveAction";
    public const string SettingsDiscardAction = "SettingsDiscardAction";
    public const string SettingsSaved = "SettingsSaved";
    public const string SettingsValidationFailed = "SettingsValidationFailed";
    public const string SettingsProvenanceLabel = "SettingsProvenanceLabel";
    public const string ProjectSettingsTitle = "ProjectSettingsTitle";
    public const string ProjectSettingsRootLabel = "ProjectSettingsRootLabel";
    public const string ProjectSettingsProjectIdLabel = "ProjectSettingsProjectIdLabel";
    public const string ProjectSettingsAliasLabel = "ProjectSettingsAliasLabel";
    public const string ProjectSettingsUserFacingLanguageLabel = "ProjectSettingsUserFacingLanguageLabel";
    public const string ProjectSettingsAgentFacingLanguageLabel = "ProjectSettingsAgentFacingLanguageLabel";
    public const string ProjectSettingsTokenBudgetLabel = "ProjectSettingsTokenBudgetLabel";
    public const string ProjectSettingsAllowedModelsLabel = "ProjectSettingsAllowedModelsLabel";
    public const string ProjectSettingsRelinkAction = "ProjectSettingsRelinkAction";
    public const string ProjectSettingsRemoveFromCatalogAction = "ProjectSettingsRemoveFromCatalogAction";
    public const string ProjectSettingsDiagnosticBundleAction = "ProjectSettingsDiagnosticBundleAction";
    public const string SprintWorkspaceTitle = "SprintWorkspaceTitle";

    // PR #98 review round 1: findings 3/4 (catalog-operation outcomes were silent or unconditionally
    // "saved"), 7 (project overview's providers section), and 8 (hardcoded English in
    // SidebarViewModel).
    public const string ProjectAddFailed = "ProjectAddFailed";
    public const string ProjectRemoveFailed = "ProjectRemoveFailed";
    public const string ProjectRelinkFailed = "ProjectRelinkFailed";
    public const string ProjectAliasSetFailed = "ProjectAliasSetFailed";
    public const string ProjectOverviewProvidersTitle = "ProjectOverviewProvidersTitle";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?, object?)"/> template
    /// taking the ready provider count then the enabled provider count, e.g. "{0}/{1} providers
    /// ready".</summary>
    public const string SidebarProvidersReadyStatus = "SidebarProvidersReadyStatus";
    public const string SidebarProvidersReadyAccessible = "SidebarProvidersReadyAccessible";
    public const string SidebarProjectAvailable = "SidebarProjectAvailable";
    public const string SidebarProjectUnavailable = "SidebarProjectUnavailable";
    public const string SidebarActiveSprintsLabel = "SidebarActiveSprintsLabel";
    public const string SidebarAttentionNeededLabel = "SidebarAttentionNeededLabel";

    // Slice 6: sprint workspace status header, timeline, and contextual-action renderer (plan
    // section 4.3, 12.3-12.6).
    public const string SprintStatusHeaderStageLabel = "SprintStatusHeaderStageLabel";
    public const string SprintStatusHeaderProgressLabel = "SprintStatusHeaderProgressLabel";
    public const string SprintStatusHeaderLastActivityLabel = "SprintStatusHeaderLastActivityLabel";
    public const string SprintStatusHeaderProviderModelUnavailable = "SprintStatusHeaderProviderModelUnavailable";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?, object?)"/> template
    /// taking the active attempt's provider id then its model, e.g. "{0} / {1}" -- resolved only
    /// once <see cref="Forge.Domain.AttemptSnapshot.Provider"/>/<c>.Model</c> are both known;
    /// <see cref="SprintStatusHeaderProviderModelUnavailable"/> is used otherwise.</summary>
    public const string SprintStatusHeaderProviderModelText = "SprintStatusHeaderProviderModelText";
    public const string SprintStatusHeaderResumeNotBeforeLabel = "SprintStatusHeaderResumeNotBeforeLabel";
    public const string SprintStatusHeaderDetailsAction = "SprintStatusHeaderDetailsAction";
    public const string TimelineTitle = "TimelineTitle";
    public const string TimelineNoItems = "TimelineNoItems";
    public const string TimelineLoadMoreAction = "TimelineLoadMoreAction";
    public const string TimelineFilterAllOption = "TimelineFilterAllOption";
    public const string TimelineFilterLabel = "TimelineFilterLabel";
    public const string TimelineCopyAction = "TimelineCopyAction";
    public const string TimelineCopiedNotice = "TimelineCopiedNotice";
    public const string TimelineDetailsAction = "TimelineDetailsAction";
    public const string TimelineMarkAllReadAction = "TimelineMarkAllReadAction";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// unread count, e.g. "{0} unread".</summary>
    public const string TimelineUnreadLabel = "TimelineUnreadLabel";
    public const string ActionsTitle = "ActionsTitle";
    public const string ActionsNoneAvailable = "ActionsNoneAvailable";
    public const string ActionsBlockedPrefix = "ActionsBlockedPrefix";
    public const string AttemptStopAction = "AttemptStopAction";
    public const string AttemptStopConfirmationRequired = "AttemptStopConfirmationRequired";
    public const string AttemptStopTargetLabel = "AttemptStopTargetLabel";
    public const string AttemptStopNoLongerActive = "AttemptStopNoLongerActive";
    public const string MoveToStageAction = "MoveToStageAction";
    public const string MoveToStageConfirmationRequired = "MoveToStageConfirmationRequired";
    public const string MoveToStageSourceLabel = "MoveToStageSourceLabel";
    public const string MoveToStageTargetLabel = "MoveToStageTargetLabel";
    public const string MoveToStageDirectionLabel = "MoveToStageDirectionLabel";
    public const string MoveToStageSatisfiedLabel = "MoveToStageSatisfiedLabel";
    public const string MoveToStageUnsatisfiedLabel = "MoveToStageUnsatisfiedLabel";
    public const string MoveToStageConsequencesLabel = "MoveToStageConsequencesLabel";
    public const string MoveToStageConsequencesStagesLabel = "MoveToStageConsequencesStagesLabel";
    public const string MoveToStageConsequencesAttemptsLabel = "MoveToStageConsequencesAttemptsLabel";
    public const string MoveToStageConsequencesArtifactsLabel = "MoveToStageConsequencesArtifactsLabel";
    public const string MoveToStageBlockedCannotProceed = "MoveToStageBlockedCannotProceed";

    /// <summary>PR #101 review finding 3: shown instead of <see cref="MoveToStageBlockedCannotProceed"/>
    /// when the assessment's blocking reason is specifically an unconverged rewind already in
    /// progress -- confirming proceeds and resumes it (<c>StageTransitionCoordinator.MoveAsync</c>'s
    /// own resume path bypasses <c>Allowed</c> entirely for exactly this diagnostic), so the prompt
    /// must not tell the user the move is impossible.</summary>
    public const string MoveToStageResumeRewindPrompt = "MoveToStageResumeRewindPrompt";
    public const string ActionRewindReasonLabel = "ActionRewindReasonLabel";
    public const string ActionRewindReasonRequired = "ActionRewindReasonRequired";
    public const string ActionRewindReasonDraftSaveFailed = "ActionRewindReasonDraftSaveFailed";
    public const string ActionStaleRefreshed = "ActionStaleRefreshed";

    /// <summary>Post-release timeline gap closure (ADR 0054): the sprint workspace's message
    /// composer -- a free-text entry and send action, distinct from <see cref="ActionRewindReasonLabel"/>'s
    /// own draft field (<c>ProjectCatalogEntry.MessageDrafts</c> is a parallel field, not a reused
    /// one -- the rewind-reason draft is specific to that one action).</summary>
    public const string TimelineMessageLabel = "TimelineMessageLabel";
    public const string TimelineMessageSendAction = "TimelineMessageSendAction";
    public const string TimelineMessageDraftSaveFailed = "TimelineMessageDraftSaveFailed";

    /// <summary><c>AvailableActionProjector</c>'s sprint-lifecycle rationale keys, resolved as
    /// descriptive text (the contextual-action renderer's row label uses
    /// <see cref="SprintRunAction"/>/<see cref="SprintResumeAction"/>/<see cref="SprintCancelAction"/>/
    /// <see cref="AttemptStopAction"/> as the button verb instead).</summary>
    public const string WorkspaceActionResumeSprintRationale = "workspace_action.resume_sprint";

    public const string WorkspaceActionRunSprintRationale = "workspace_action.run_sprint";
    public const string WorkspaceActionCancelSprintRationale = "workspace_action.cancel_sprint";
    public const string WorkspaceActionStopCurrentOperationRationale = "workspace_action.stop_current_operation";
    public const string WorkspaceActionMoveToStageAdvanceRationale = "workspace_action.move_to_stage.advance";
    public const string WorkspaceActionMoveToStageRewindRationale = "workspace_action.move_to_stage.rewind";
    public const string WorkspaceActionMoveToStageSameRationale = "workspace_action.move_to_stage.same";

    /// <summary>PR #101 review finding 3: the one row <c>AvailableActionProjector.ForSprintAsync</c>
    /// offers while a rewind has not yet converged -- a resume, not an ordinary fresh move.</summary>
    public const string WorkspaceActionResumeRewindRationale = "workspace_action.move_to_stage.resume_rewind";

    /// <summary>ADR 0058: the approve/reject pair <c>AvailableActionProjector.ForSprintAsync</c> offers
    /// per pending human gate.</summary>
    public const string WorkspaceActionApproveGateRationale = "workspace_action.approve_gate";

    public const string WorkspaceActionRejectGateRationale = "workspace_action.reject_gate";

    // Slice 7: `provider.quota_status` (plan section 6.5, ADR 0043/0052). `QuotaStatusUnknown`
    // (defined above, Slice 5) is the only state this codebase currently produces -- the remaining
    // four exist so the sidebar/CLI rendering is complete for every state the plan requires, not
    // only the one ADR 0052 found verifiable evidence for.
    public const string ModelsQuotaDescription = "ModelsQuotaDescription";
    public const string ModelsQuotaTitle = "ModelsQuotaTitle";
    public const string QuotaStatusUnknownAccessible = "QuotaStatusUnknownAccessible";
    public const string QuotaStatusReady = "QuotaStatusReady";
    public const string QuotaStatusReadyAccessible = "QuotaStatusReadyAccessible";
    public const string QuotaStatusLimited = "QuotaStatusLimited";
    public const string QuotaStatusLimitedAccessible = "QuotaStatusLimitedAccessible";
    public const string QuotaStatusDepleted = "QuotaStatusDepleted";
    public const string QuotaStatusDepletedAccessible = "QuotaStatusDepletedAccessible";
    public const string QuotaStatusStale = "QuotaStatusStale";
    public const string QuotaStatusStaleAccessible = "QuotaStatusStaleAccessible";

    /// <summary>ADR 0053: shown instead of a mutation's ordinary failure text when the connected
    /// Host's handshake-advertised capability set is missing the one the request needed -- ADR
    /// 0053's client-side capability gate rejected it before it ever reached the wire.</summary>
    public const string CapabilityNotSupported = "CapabilityNotSupported";

    // Plan section 12.3/12.6 closure: localized timeline item text. Each constant's value is the
    // exact durable `workflow.*`/`routing.*` journal message key it resolves (see
    // WorkflowEvent.MessageKey producing call sites in FileSprintEventLog/SprintScheduler/
    // SprintOrchestrator/StageTransitionCoordinator/StopOperationCoordinator) -- reused verbatim as
    // the resx key itself, matching the existing `next.*`/`workspace_action.*` rationale-key
    // convention above rather than inventing a parallel naming scheme. Resolved through
    // TimelineMessageFormatter.Format, never text.Resolve directly, since a few of these carry a
    // durable argument the raw key alone would lose (see that type's own remarks).
    public const string WorkflowSprintCreated = "workflow.sprint_created";
    public const string WorkflowSprintAdvanced = "workflow.sprint_advanced";
    public const string WorkflowSprintCancelled = "workflow.sprint_cancelled";
    public const string WorkflowSprintResumed = "workflow.sprint_resumed";
    public const string WorkflowSprintReady = "workflow.sprint_ready";
    public const string WorkflowSprintReadyToFinalize = "workflow.sprint_ready_to_finalize";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// blocked reason code ("node"/"finding"/"gate"/"confirmation"/"review_convergence"/"rewind" --
    /// see SprintScheduler/StageTransitionCoordinator's own <c>BlockedBy*</c> constants), resolved to
    /// a localized label by <see cref="TimelineMessageFormatter.Format"/> before substitution (PR
    /// #107 review finding 3) rather than interpolated as the raw machine code.</summary>
    public const string WorkflowSprintBlocked = "workflow.sprint_blocked";

    /// <summary>PR #107 review finding 2: not emitted by any producing call site today, but a
    /// literal `messageKey` argument this test suite already exercises against
    /// <c>ISprintStore.AppendTransitionAsync</c> (<c>NotificationProjectorTests.cs</c>) -- since that
    /// parameter accepts an arbitrary string with no closed-set validation (see the finding 1 crash
    /// this PR fixes), a future producing call site could use it without warning unless it is
    /// already registered.</summary>
    public const string WorkflowSprintFailed = "workflow.sprint_failed";

    public const string WorkflowSprintCompleted = "workflow.sprint_completed";
    public const string WorkflowSprintRunning = "workflow.sprint_running";
    public const string WorkflowSprintAwaitingHuman = "workflow.sprint_awaiting_human";
    public const string WorkflowSprintGateResumed = "workflow.sprint_gate_resumed";
    public const string WorkflowSprintPaused = "workflow.sprint_paused";
    public const string WorkflowNodeCreated = "workflow.node_created";
    public const string WorkflowNodeReady = "workflow.node_ready";
    public const string WorkflowNodeRunning = "workflow.node_running";
    public const string WorkflowNodeSucceeded = "workflow.node_succeeded";
    public const string WorkflowNodeFailed = "workflow.node_failed";
    public const string WorkflowNodeRejected = "workflow.node_rejected";
    public const string WorkflowNodeSkipped = "workflow.node_skipped";
    public const string WorkflowNodeRetrying = "workflow.node_retrying";
    public const string WorkflowNodeRetried = "workflow.node_retried";
    public const string WorkflowNodeAwaitingHuman = "workflow.node_awaiting_human";
    public const string WorkflowNodeSuperseded = "workflow.node_superseded";
    public const string WorkflowNodeStopped = "workflow.node_stopped";
    public const string WorkflowNodeRearmed = "workflow.node_rearmed";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// stage revision number (<see cref="WorkflowEvent.RevisionArgument"/>).</summary>
    public const string WorkflowNodeReopened = "workflow.node_reopened";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// stage revision number (<see cref="WorkflowEvent.RevisionArgument"/>).</summary>
    public const string WorkflowNodeInvalidated = "workflow.node_invalidated";
    public const string WorkflowNodeRewindInterrupted = "workflow.node_rewind_interrupted";
    public const string WorkflowAttemptCreated = "workflow.attempt_created";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// attempt's target state (<see cref="WorkflowEvent.ToStateArgument"/>), resolved to a localized
    /// <see cref="Domain.AttemptState"/> label by <see cref="TimelineMessageFormatter.Format"/> before
    /// substitution (PR #107 review finding 4) rather than interpolated as the raw snake_case
    /// value.</summary>
    public const string WorkflowAttemptTransitioned = "workflow.attempt_transitioned";

    // PR #107 review finding 2: same "not emitted today, but a literal messageKey argument the test
    // suite already exercises against ISprintStore.AppendTransitionAsync directly
    // (SprintEventStoreTests.cs)" reasoning as WorkflowSprintFailed above.
    public const string WorkflowAttemptCancelled = "workflow.attempt_cancelled";

    public const string WorkflowAttemptPreparing = "workflow.attempt_preparing";
    public const string WorkflowAttemptRunning = "workflow.attempt_running";
    public const string WorkflowAttemptValidating = "workflow.attempt_validating";
    public const string WorkflowAttemptStopped = "workflow.attempt_stopped";
    public const string WorkflowAttemptSuperseded = "workflow.attempt_superseded";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// operator's bounded free-text supersession instruction
    /// (<see cref="WorkflowEvent.SupersessionInstructionArgument"/>).</summary>
    public const string WorkflowAttemptSupersededInstruction = "workflow.attempt_superseded_instruction";
    public const string WorkflowAttemptActivity = "workflow.attempt_activity";
    public const string WorkflowAttemptStopRequested = "workflow.attempt_stop_requested";
    public const string WorkflowAttemptStopConverged = "workflow.attempt_stop_converged";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?, object?, object?)"/>
    /// template taking the rewind's target stage id
    /// (<see cref="WorkflowEvent.TargetStageIdArgument"/>), the operator's bounded free-text reason
    /// (<see cref="WorkflowEvent.RewindReasonArgument"/>), then the new revision number
    /// (<see cref="WorkflowEvent.RevisionArgument"/>).</summary>
    public const string WorkflowStageRevisionRecorded = "workflow.stage_revision_recorded";
    public const string WorkflowStageTransitionConverged = "workflow.stage_transition_converged";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// user's bounded free-text message (<see cref="WorkflowEvent.UserMessageTextArgument"/>).
    /// </summary>
    public const string WorkflowUserMessagePosted = "workflow.user_message_posted";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?)"/> template taking the
    /// agent's summary text (<see cref="WorkflowEvent.AgentSummaryTextArgument"/>).</summary>
    public const string WorkflowAgentSummaryRecorded = "workflow.agent_summary_recorded";

    /// <summary>ADR 0059. A <see cref="string.Format(IFormatProvider?, string, object?, object?, object?)"/>
    /// template taking the attempt's changed-file count
    /// (<see cref="WorkflowEvent.DiffFilesChangedArgument"/>), then its added and deleted line totals
    /// (<see cref="WorkflowEvent.DiffInsertionsArgument"/>/<see cref="WorkflowEvent.DiffDeletionsArgument"/>).
    /// All three are pure numbers, so no closed-set code needs a localized label of its own here --
    /// unlike <see cref="WorkflowSprintBlocked"/> or <see cref="WorkflowAttemptTransitioned"/>. The
    /// per-file breakdown rides on the event's structured payload, not this sentence.</summary>
    public const string WorkflowAttemptDiffRecorded = "workflow.attempt_diff_recorded";

    /// <summary>ADR 0060. A <see cref="string.Format(IFormatProvider?, string, object?, object?, object?)"/>
    /// template taking the attempt's total tool-call count
    /// (<see cref="WorkflowEvent.ToolCallsArgument"/>), then its command and edit counts
    /// (<see cref="WorkflowEvent.ToolCommandsArgument"/>/<see cref="WorkflowEvent.ToolEditsArgument"/>).
    /// All three are pure numbers, so nothing here needs a localized closed-set label -- no tool kind
    /// is ever named in this sentence. The per-call detail rides on the event's structured payload.
    /// </summary>
    public const string WorkflowAttemptToolUseRecorded = "workflow.attempt_tool_use_recorded";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?, object?, object?)"/>
    /// template taking the routed provider id, model id verbatim (proper identifiers, never
    /// translated), then a localized <see cref="Domain.RouteOutcome"/> label resolved by
    /// <see cref="TimelineMessageFormatter.Format"/> before substitution (PR #107 review finding 5)
    /// rather than the raw snake_case outcome.</summary>
    public const string RoutingDecisionRecorded = "routing.decision_recorded";

    // PR #107 review finding 3: localized labels for workflow.sprint_blocked's blocked_reason
    // argument (SprintScheduler/StageTransitionCoordinator's own BlockedBy* constants) -- see
    // TimelineMessageFormatter.BlockedReasonLabel.
    public const string SprintBlockedReasonNode = "SprintBlockedReasonNode";
    public const string SprintBlockedReasonFinding = "SprintBlockedReasonFinding";
    public const string SprintBlockedReasonGate = "SprintBlockedReasonGate";
    public const string SprintBlockedReasonConfirmation = "SprintBlockedReasonConfirmation";
    public const string SprintBlockedReasonReviewConvergence = "SprintBlockedReasonReviewConvergence";
    public const string SprintBlockedReasonRewind = "SprintBlockedReasonRewind";

    // PR #107 review finding 4: localized labels for workflow.attempt_transitioned's to_state
    // argument (Domain.AttemptState) -- see TimelineMessageFormatter.AttemptStateLabel. Distinct
    // from the not-yet-used SprintStatePaused-style family above (SprintState and AttemptState are
    // different enums with different member sets).
    public const string AttemptStateCreated = "AttemptStateCreated";
    public const string AttemptStatePreparing = "AttemptStatePreparing";
    public const string AttemptStateRunning = "AttemptStateRunning";
    public const string AttemptStateValidating = "AttemptStateValidating";
    public const string AttemptStateSucceeded = "AttemptStateSucceeded";
    public const string AttemptStateFailed = "AttemptStateFailed";
    public const string AttemptStateCancelled = "AttemptStateCancelled";

    // PR #107 review finding 5: localized labels for routing.decision_recorded's outcome argument
    // (Domain.RouteOutcome) -- see TimelineMessageFormatter.RoutingOutcomeLabel.
    public const string RoutingOutcomeRouted = "RoutingOutcomeRouted";
    public const string RoutingOutcomeSucceeded = "RoutingOutcomeSucceeded";
    public const string RoutingOutcomeFailed = "RoutingOutcomeFailed";
    public const string RoutingOutcomeCircuitOpen = "RoutingOutcomeCircuitOpen";
    public const string RoutingOutcomeBudgetExhausted = "RoutingOutcomeBudgetExhausted";
    public const string RoutingOutcomeExcluded = "RoutingOutcomeExcluded";
    public const string RoutingOutcomeDeferred = "RoutingOutcomeDeferred";

    // Plan 12.6 status-row closure: authentication, model availability, and Host connectivity
    // (SidebarViewModel.BuildStatusRow). Mirrors QuotaStatus*'s own worst-case-across-many shape.
    public const string AuthenticationStatusUnknown = "AuthenticationStatusUnknown";
    public const string AuthenticationStatusUnknownAccessible = "AuthenticationStatusUnknownAccessible";
    public const string AuthenticationStatusReady = "AuthenticationStatusReady";
    public const string AuthenticationStatusReadyAccessible = "AuthenticationStatusReadyAccessible";
    public const string AuthenticationStatusRequired = "AuthenticationStatusRequired";
    public const string AuthenticationStatusRequiredAccessible = "AuthenticationStatusRequiredAccessible";
    public const string AuthenticationStatusCheckFailed = "AuthenticationStatusCheckFailed";
    public const string AuthenticationStatusCheckFailedAccessible = "AuthenticationStatusCheckFailedAccessible";

    /// <summary>A <see cref="string.Format(IFormatProvider?, string, object?, object?)"/> template
    /// taking the model-available provider count then the enabled provider count, e.g. "{0}/{1}
    /// models available" -- mirrors <see cref="SidebarProvidersReadyStatus"/>'s own shape but counts
    /// toolchain-ready AND authenticated providers (see <c>SidebarViewModel.ModelAvailabilitySummary</c>).</summary>
    public const string SidebarModelsAvailableStatus = "SidebarModelsAvailableStatus";
    public const string SidebarModelsAvailableAccessible = "SidebarModelsAvailableAccessible";

    public const string HostConnectivityUnknown = "HostConnectivityUnknown";
    public const string HostConnectivityUnknownAccessible = "HostConnectivityUnknownAccessible";
    public const string HostConnectivityConnected = "HostConnectivityConnected";
    public const string HostConnectivityConnectedAccessible = "HostConnectivityConnectedAccessible";
    public const string HostConnectivityDisconnected = "HostConnectivityDisconnected";
    public const string HostConnectivityDisconnectedAccessible = "HostConnectivityDisconnectedAccessible";
    public const string HostConnectivityStale = "HostConnectivityStale";
    public const string HostConnectivityStaleAccessible = "HostConnectivityStaleAccessible";
}
