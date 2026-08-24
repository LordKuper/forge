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
}
