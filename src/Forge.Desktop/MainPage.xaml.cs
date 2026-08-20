using Forge.Application;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop;

public partial class MainPage : ContentPage
{
    private readonly SurfaceText text;
    private readonly MainPageViewModel viewModel;
    private bool busy;
    // Unlike GateResultLabel/AttemptSupersedeResultLabel (a one-shot mutation's own outcome, with
    // no companion state), EventsLabel is a live view of the view model's own stored polling
    // cursor -- clearing it on every routine refresh would discard a still-valid poll's rendered
    // page for no reason. Tracked independently here (the view model's cursor state is not
    // exposed) so RefreshAsync can clear it only when the condition that actually invalidates it --
    // a project-root switch, matching MainPageViewModel.PollEventsAsync's own reset condition --
    // is true, not on every refresh regardless.
    private string? lastPolledEventsProjectRoot;

    public MainPage(
        SurfaceText text,
        ForgeApplication application,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(resolveMutations);
        InitializeComponent();
        this.text = text;
        viewModel = new MainPageViewModel(text, application, resolveMutations);
        TitleLabel.Text = text.Resolve(MessageKeys.AppTitle);
        RefreshButton.Text = text.Resolve(MessageKeys.RefreshAction);
        InitializeButton.Text = text.Resolve(MessageKeys.InitializeAction);
        RecoverButton.Text = text.Resolve(MessageKeys.RecoverAction);
        ConfigurationTitleLabel.Text = text.Resolve(MessageKeys.ConfigurationTitle);
        // No free-text box on this page has an adjacent visible label, so each carries its own
        // screen-reader name and visible placeholder (ADR 0005: every action is screen-reader
        // named). SurfaceParityTests derives the list from the XAML, so a new Entry fails until
        // it is described here too.
        Describe(ProjectRootEntry, text.Resolve(MessageKeys.ProjectRootLabel));
        Describe(SprintIdEntry, text.Resolve(MessageKeys.SprintIdLabel));
        Describe(GateNodeIdEntry, text.Resolve(MessageKeys.GateNodeIdLabel));
        Describe(AttemptIdEntry, text.Resolve(MessageKeys.AttemptIdLabel));
        Describe(AttemptInstructionEntry, text.Resolve(MessageKeys.AttemptInstructionLabel));
        Describe(ConfirmNodeIdEntry, text.Resolve(MessageKeys.ConfirmNodeIdLabel));
        Describe(ConfirmDefinitionOfDoneEntry, text.Resolve(MessageKeys.ConfirmDefinitionOfDoneLabel));
        Describe(ConfirmEvidenceEntry, text.Resolve(MessageKeys.ConfirmEvidenceLabel));
        Describe(TestWorkNodeIdEntry, text.Resolve(MessageKeys.TestWorkNodeIdLabel));
        Describe(TestWorkJustificationEntry, text.Resolve(MessageKeys.TestWorkJustificationLabel));
        Describe(FinalizeNodeIdEntry, text.Resolve(MessageKeys.FinalizeNodeIdLabel));
        Describe(ConfigurationKeyEntry, text.Resolve(MessageKeys.ConfigurationKeyLabel));
        Describe(ConfigurationValueEntry, text.Resolve(MessageKeys.ConfigurationValueLabel));
        GateApproveButton.Text = text.Resolve(MessageKeys.GateApproveAction);
        GateRejectButton.Text = text.Resolve(MessageKeys.GateRejectAction);
        AttemptSupersedeButton.Text = text.Resolve(MessageKeys.AttemptSupersedeAction);
        ConfirmConfirmedButton.Text = text.Resolve(MessageKeys.ConfirmConfirmedAction);
        ConfirmNotConfirmedButton.Text = text.Resolve(MessageKeys.ConfirmNotConfirmedAction);
        TestWorkAddedButton.Text = text.Resolve(MessageKeys.TestWorkAddedAction);
        TestWorkNoNewTestsButton.Text = text.Resolve(MessageKeys.TestWorkNoNewTestsAction);
        FinalizeButton.Text = text.Resolve(MessageKeys.FinalizeAction);
        EventsPollButton.Text = text.Resolve(MessageKeys.EventsPollAction);
        IntegrationGenerateButton.Text = text.Resolve(MessageKeys.IntegrationGenerateAction);
        IntegrationInstallButton.Text = text.Resolve(MessageKeys.IntegrationInstallAction);
        IntegrationRemoveButton.Text = text.Resolve(MessageKeys.IntegrationRemoveAction);
        SprintCreateButton.Text = text.Resolve(MessageKeys.SprintCreateAction);
        SprintRunButton.Text = text.Resolve(MessageKeys.SprintRunAction);
        SprintResumeButton.Text = text.Resolve(MessageKeys.SprintResumeAction);
        SprintCancelButton.Text = text.Resolve(MessageKeys.SprintCancelAction);
        ConfigurationSetButton.Text = text.Resolve(MessageKeys.ConfigurationSetAction);
        // Actions stay disabled until the first refresh reports the durable state.
        InitializeButton.IsEnabled = false;
        RecoverButton.IsEnabled = false;
        // Scope names are machine identifiers and stay culture invariant.
        ConfigurationScopePicker.ItemsSource = new List<string> { "user", "project" };
        ConfigurationScopePicker.SelectedIndex = 0;
        // Same reasoning: `forge confirm --evidence-kind`'s own machine vocabulary.
        ConfirmEvidenceKindPicker.ItemsSource = new List<string> { "inspection", "execution", "existing-check" };
        ConfirmEvidenceKindPicker.SelectedIndex = 0;
    }

    private static void Describe(Entry entry, string label)
    {
        entry.Placeholder = label;
        SemanticProperties.SetDescription(entry, label);
    }

    private string? ProjectRoot =>
        string.IsNullOrWhiteSpace(ProjectRootEntry.Text) ? null : ProjectRootEntry.Text;

    /// <summary>Empty means "expand the active sprint", matching `forge tree` with no `--sprint`.</summary>
    private string? SprintId =>
        string.IsNullOrWhiteSpace(SprintIdEntry.Text) ? null : SprintIdEntry.Text;

    /// <summary>Empty means the canonical human-approval node, matching `forge gate approve|reject`
    /// with no `--node`.</summary>
    private string? GateNodeId =>
        string.IsNullOrWhiteSpace(GateNodeIdEntry.Text) ? null : GateNodeIdEntry.Text;

    private string? AttemptId =>
        string.IsNullOrWhiteSpace(AttemptIdEntry.Text) ? null : AttemptIdEntry.Text;

    /// <summary>Empty means the canonical confirmation node, matching `forge confirm` with no
    /// `--node`.</summary>
    private string? ConfirmNodeId =>
        string.IsNullOrWhiteSpace(ConfirmNodeIdEntry.Text) ? null : ConfirmNodeIdEntry.Text;

    /// <summary>Empty means the canonical test_work node, matching `forge test-work` with no
    /// `--node`.</summary>
    private string? TestWorkNodeId =>
        string.IsNullOrWhiteSpace(TestWorkNodeIdEntry.Text) ? null : TestWorkNodeIdEntry.Text;

    /// <summary>Empty means the canonical finalization node, matching `forge finalize` with no
    /// `--node`.</summary>
    private string? FinalizeNodeId =>
        string.IsNullOrWhiteSpace(FinalizeNodeIdEntry.Text) ? null : FinalizeNodeIdEntry.Text;

    public async Task RefreshAsync()
    {
        // Captured once, before the request: ProjectRootEntry can be edited while this await is in
        // flight, and reading the live property again afterward (as this PR originally did for the
        // EventsLabel guard below) would let the rendered snapshot describe one root while the
        // events-reset decision judges a different one -- the same TOCTOU shape round 2 review
        // fixed in MainPageViewModel.PollEventsAsync, reintroduced here by this PR's own diff.
        string? requestedRoot = ProjectRoot;
        MainPageSnapshot snapshot = await viewModel.RefreshAsync(requestedRoot, SprintId, CancellationToken.None)
            .ConfigureAwait(true);
        StatusLabel.Text = snapshot.StatusText;
        ProjectRootLabel.Text = snapshot.ProjectRootText;
        ProjectStateLabel.Text = snapshot.ProjectStateText;
        StartupChecksLabel.Text = snapshot.StartupChecksText;
        ProvidersLabel.Text = snapshot.ProvidersText;
        SuggestedActionsLabel.Text = snapshot.SuggestedActionsText;
        SprintsLabel.Text = snapshot.SprintsText;
        SprintDetailsLabel.Text = snapshot.SprintDetailsText;
        InitializeButton.IsEnabled = snapshot.InitializeEnabled;
        RecoverButton.IsEnabled = snapshot.RecoverEnabled;
        ConfigurationLabel.Text = snapshot.ConfigurationText;
        DiagnosticsLabel.Text = snapshot.DiagnosticsText;
        // Not part of the snapshot: a prior gate decision's outcome must never survive into an
        // unrelated later refresh (a different sprint typed in, a different project root, or just a
        // routine Refresh click) and read as if it still describes what is now on screen.
        // ResolveGateAsync re-assigns this immediately after calling RefreshAsync, so a decision's
        // own outcome still shows correctly right after making it.
        GateResultLabel.Text = string.Empty;
        // Same reasoning as GateResultLabel above, for the attempt-supersession outcome.
        AttemptSupersedeResultLabel.Text = string.Empty;
        // Same reasoning as GateResultLabel above, for the confirm/test-work/finalize outcomes.
        ConfirmResultLabel.Text = string.Empty;
        TestWorkResultLabel.Text = string.Empty;
        FinalizeResultLabel.Text = string.Empty;
        // Same reasoning as GateResultLabel above: a preview or a write outcome carries no
        // companion state (unlike EventsLabel's cursor below), so it is safe -- and correct -- to
        // always clear both unconditionally.
        IntegrationLabel.Text = string.Empty;
        IntegrationWriteResultLabel.Text = string.Empty;
        // Same reasoning as GateResultLabel above, for a sprint-lifecycle action's outcome.
        SprintManageResultLabel.Text = string.Empty;
        // Same reasoning again, for the last poll's rendered page -- but only reset it on the
        // condition that actually invalidates it (a project-root switch), not on every refresh:
        // see the lastPolledEventsProjectRoot field's own comment.
        if (!string.Equals(requestedRoot, lastPolledEventsProjectRoot, StringComparison.Ordinal))
        {
            EventsLabel.Text = string.Empty;
            lastPolledEventsProjectRoot = requestedRoot;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(RefreshAsync).ConfigureAwait(true);
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) =>
        await RunAsync(RefreshAsync).ConfigureAwait(true);

    private async void OnInitializeClicked(object? sender, EventArgs e) =>
        await RunAsync(InitializeAsync).ConfigureAwait(true);

    private async void OnRecoverClicked(object? sender, EventArgs e) =>
        await RunAsync(RecoverAsync).ConfigureAwait(true);

    private async void OnConfigurationSetClicked(object? sender, EventArgs e) =>
        await RunAsync(SetConfigurationAsync).ConfigureAwait(true);

    private async void OnGateApproveClicked(object? sender, EventArgs e) =>
        await RunAsync(() => ResolveGateAsync(approved: true)).ConfigureAwait(true);

    private async void OnGateRejectClicked(object? sender, EventArgs e) =>
        await RunAsync(() => ResolveGateAsync(approved: false)).ConfigureAwait(true);

    private async void OnAttemptSupersedeClicked(object? sender, EventArgs e) =>
        await RunAsync(SupersedeAttemptAsync).ConfigureAwait(true);

    private async void OnConfirmConfirmedClicked(object? sender, EventArgs e) =>
        await RunAsync(() => ConfirmNodeAsync(ConfirmationOutcome.Confirmed)).ConfigureAwait(true);

    private async void OnConfirmNotConfirmedClicked(object? sender, EventArgs e) =>
        await RunAsync(() => ConfirmNodeAsync(ConfirmationOutcome.NotConfirmed)).ConfigureAwait(true);

    private async void OnTestWorkAddedClicked(object? sender, EventArgs e) =>
        await RunAsync(() => RecordTestWorkAsync(TestWorkOutcome.TestsAdded)).ConfigureAwait(true);

    private async void OnTestWorkNoNewTestsClicked(object? sender, EventArgs e) =>
        await RunAsync(() => RecordTestWorkAsync(TestWorkOutcome.NoNewTestsJustified)).ConfigureAwait(true);

    private async void OnFinalizeClicked(object? sender, EventArgs e) =>
        await RunAsync(FinalizeAsync).ConfigureAwait(true);

    private async void OnEventsPollClicked(object? sender, EventArgs e) =>
        await RunAsync(PollEventsAsync).ConfigureAwait(true);

    private async void OnIntegrationGenerateClicked(object? sender, EventArgs e) =>
        await RunAsync(GenerateIntegrationPreviewAsync).ConfigureAwait(true);

    private async void OnIntegrationInstallClicked(object? sender, EventArgs e) =>
        await RunAsync(InstallIntegrationAsync).ConfigureAwait(true);

    private async void OnIntegrationRemoveClicked(object? sender, EventArgs e) =>
        await RunAsync(RemoveIntegrationAsync).ConfigureAwait(true);

    private async void OnSprintCreateClicked(object? sender, EventArgs e) =>
        await RunAsync(CreateSprintAsync).ConfigureAwait(true);

    private async void OnSprintRunClicked(object? sender, EventArgs e) =>
        await RunAsync(RunSprintAsync).ConfigureAwait(true);

    private async void OnSprintResumeClicked(object? sender, EventArgs e) =>
        await RunAsync(ResumeSprintAsync).ConfigureAwait(true);

    private async void OnSprintCancelClicked(object? sender, EventArgs e) =>
        await RunAsync(CancelSprintAsync).ConfigureAwait(true);

    /// <summary>Serializes surface actions so a second click cannot re-enter a mutation.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task InitializeAsync()
    {
        ProjectSnapshot snapshot = await viewModel.GetProjectSnapshotAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
        bool confirmed = await DisplayAlertAsync(
                text.Resolve(MessageKeys.InitializeAction),
                viewModel.InitializePrompt(snapshot),
                text.Resolve(MessageKeys.InitializeAction),
                text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            ConfigurationResultLabel.Text = text.Resolve(MessageKeys.InitConfirmationRequired);
            return;
        }

        ConfigurationResultLabel.Text = await viewModel.InitializeAsync(snapshot, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RecoverAsync()
    {
        bool confirmed = await DisplayAlertAsync(
                text.Resolve(MessageKeys.RecoverAction),
                text.Resolve(MessageKeys.RecoverAction),
                text.Resolve(MessageKeys.RecoverAction),
                text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        ConfigurationResultLabel.Text = await viewModel
            .RecoverAsync(ProjectRoot, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SetConfigurationAsync()
    {
        ConfigurationScope scope = ConfigurationScopePicker.SelectedIndex == 1
            ? ConfigurationScope.Project
            : ConfigurationScope.User;
        ConfigurationResultLabel.Text = await viewModel
            .SetConfigurationAsync(
                scope,
                ProjectRoot,
                ConfigurationKeyEntry.Text ?? string.Empty,
                ConfigurationValueEntry.Text,
                CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>ADR 0005/0018's human-only `workflow.review` capability: unlike
    /// <see cref="RecoverAsync"/>, this confirmation is never bypassable — the dialog's own
    /// yes/no answer *is* the `confirmed` value passed through, with no config-driven shortcut.
    /// Declining is not itself a failed mutation: matching <see cref="InitializeAsync"/>, it short-
    /// circuits before <see cref="viewModel"/> ever resolves a Host connection or sends a request.</summary>
    private async Task ResolveGateAsync(bool approved)
    {
        string action = text.Resolve(approved ? MessageKeys.GateApproveAction : MessageKeys.GateRejectAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.GatePrompt(SprintId, GateNodeId), action, text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            GateResultLabel.Text = text.Resolve(MessageKeys.GateConfirmationRequired);
            return;
        }

        string message = await viewModel
            .ResolveGateAsync(ProjectRoot, SprintId, GateNodeId, approved, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        // After RefreshAsync, which clears GateResultLabel as part of its own reset, so this
        // decision's own outcome is what the user sees, not a stale value RefreshAsync just wiped.
        await RefreshAsync().ConfigureAwait(true);
        GateResultLabel.Text = message;
    }

    /// <summary>ADR 0005/0018's human-only `attempt.supersede` capability: same shape as
    /// <see cref="ResolveGateAsync"/> — the dialog's own answer is the only source of `confirmed`,
    /// and declining short-circuits before <see cref="viewModel"/> ever resolves a Host connection.
    /// A blank attempt id or blank instruction is refused *before* the dialog shows: unlike the
    /// gate's node id, neither has a default to fall back to, so showing a confirmation dialog for
    /// either would ask the user to confirm superseding an unnamed target with no replacement text.
    /// The instruction's *length* bound stays server-validated in <see cref="viewModel"/> — only
    /// emptiness is checked here, since only emptiness makes the target itself meaningless the way a
    /// blank attempt id does.</summary>
    private async Task SupersedeAttemptAsync()
    {
        if (AttemptId is null)
        {
            AttemptSupersedeResultLabel.Text = text.Resolve(MessageKeys.AttemptIdRequired);
            return;
        }

        if (string.IsNullOrWhiteSpace(AttemptInstructionEntry.Text))
        {
            AttemptSupersedeResultLabel.Text = text.Resolve(MessageKeys.AttemptInstructionRequired);
            return;
        }

        string action = text.Resolve(MessageKeys.AttemptSupersedeAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.AttemptSupersedePrompt(SprintId, AttemptId), action,
                text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            AttemptSupersedeResultLabel.Text = text.Resolve(MessageKeys.AttemptSupersedeConfirmationRequired);
            return;
        }

        string message = await viewModel
            .SupersedeAttemptAsync(
                ProjectRoot, SprintId, AttemptId, AttemptInstructionEntry.Text, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        AttemptSupersedeResultLabel.Text = message;
    }

    /// <summary>ADR 0037's human-only `workflow.confirm` capability: same shape as
    /// <see cref="ResolveGateAsync"/>/<see cref="SupersedeAttemptAsync"/> -- the dialog's own answer
    /// is the only source of `confirmed`, and declining short-circuits before <see cref="viewModel"/>
    /// ever resolves a Host connection. A blank definition-of-done or evidence is refused *before*
    /// the dialog shows, matching <see cref="SupersedeAttemptAsync"/>'s own blank-instruction guard:
    /// neither field has a default to fall back to, so showing a confirmation dialog for either would
    /// ask the user to confirm a decision with no actual content.</summary>
    private async Task ConfirmNodeAsync(ConfirmationOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(ConfirmDefinitionOfDoneEntry.Text))
        {
            ConfirmResultLabel.Text = text.Resolve(MessageKeys.ConfirmDefinitionOfDoneRequired);
            return;
        }

        if (string.IsNullOrWhiteSpace(ConfirmEvidenceEntry.Text))
        {
            ConfirmResultLabel.Text = text.Resolve(MessageKeys.ConfirmEvidenceRequired);
            return;
        }

        string action = text.Resolve(outcome == ConfirmationOutcome.Confirmed
            ? MessageKeys.ConfirmConfirmedAction
            : MessageKeys.ConfirmNotConfirmedAction);
        bool confirmed = await DisplayAlertAsync(
                action,
                viewModel.ConfirmPrompt(
                    SprintId, ConfirmNodeId, ConfirmDefinitionOfDoneEntry.Text, ConfirmEvidenceEntry.Text),
                action, text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            ConfirmResultLabel.Text = text.Resolve(MessageKeys.ConfirmConfirmationRequired);
            return;
        }

        string message = await viewModel
            .ConfirmNodeAsync(
                ProjectRoot, SprintId, ConfirmNodeId, outcome, ConfirmDefinitionOfDoneEntry.Text,
                ConfirmEvidenceKindPicker.SelectedItem as string, ConfirmEvidenceEntry.Text, confirmed,
                CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        ConfirmResultLabel.Text = message;
    }

    /// <summary>ADR 0037's human-only `workflow.test_work` capability -- same shape as
    /// <see cref="ConfirmNodeAsync"/>, minus the evidence-kind/definition-of-done fields.</summary>
    private async Task RecordTestWorkAsync(TestWorkOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(TestWorkJustificationEntry.Text))
        {
            TestWorkResultLabel.Text = text.Resolve(MessageKeys.TestWorkJustificationRequired);
            return;
        }

        string action = text.Resolve(outcome == TestWorkOutcome.TestsAdded
            ? MessageKeys.TestWorkAddedAction
            : MessageKeys.TestWorkNoNewTestsAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.TestWorkPrompt(SprintId, TestWorkNodeId, TestWorkJustificationEntry.Text),
                action, text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            TestWorkResultLabel.Text = text.Resolve(MessageKeys.TestWorkConfirmationRequired);
            return;
        }

        string message = await viewModel
            .RecordTestWorkAsync(
                ProjectRoot, SprintId, TestWorkNodeId, outcome, TestWorkJustificationEntry.Text, confirmed,
                CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        TestWorkResultLabel.Text = message;
    }

    /// <summary>ADR 0037's human-only `workflow.finalize` capability (ADR 0036's own CLI command) --
    /// same shape as <see cref="ResolveGateAsync"/>, minus the outcome choice: finalization only ever
    /// attempts the same merge.</summary>
    private async Task FinalizeAsync()
    {
        string action = text.Resolve(MessageKeys.FinalizeAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.FinalizePrompt(SprintId, FinalizeNodeId), action,
                text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            FinalizeResultLabel.Text = text.Resolve(MessageKeys.FinalizeConfirmationRequired);
            return;
        }

        string message = await viewModel
            .FinalizeSprintAsync(ProjectRoot, SprintId, FinalizeNodeId, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        FinalizeResultLabel.Text = message;
    }

    /// <summary>ADR 0005's read-only `control.events` capability. Unlike every mutating action on
    /// this page, a poll needs no confirmation dialog (nothing irreversible happens) and does not
    /// call <see cref="RefreshAsync"/> afterward -- the events page is not part of
    /// <see cref="MainPageSnapshot"/>, so nothing else on screen needs to change for it. Round 2
    /// review found <c>ProjectRoot</c> read twice across the `await` below -- editing the entry
    /// mid-poll (the `busy` guard blocks re-clicks, not entry edits) could store the request's own
    /// root in <see cref="lastPolledEventsProjectRoot"/> while the label and the view model's own
    /// cursor still reflect whatever root the request actually read against. Captured into a local
    /// once, before the request, so both assignments always agree with what was actually polled.</summary>
    private async Task PollEventsAsync()
    {
        string? requestedRoot = ProjectRoot;
        EventsLabel.Text = await viewModel.PollEventsAsync(requestedRoot, CancellationToken.None)
            .ConfigureAwait(true);
        lastPolledEventsProjectRoot = requestedRoot;
    }

    /// <summary>ADR 0026's `integration.skill` capability -- the read-only preview verb (`forge
    /// integration skill generate`). Like <see cref="PollEventsAsync"/>, a query needs no
    /// confirmation dialog and does not call <see cref="RefreshAsync"/> afterward, since the
    /// preview is not part of <see cref="MainPageSnapshot"/>.</summary>
    private async Task GenerateIntegrationPreviewAsync()
    {
        IntegrationLabel.Text = await viewModel
            .GenerateIntegrationPreviewAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
    }

    /// <summary>ADR 0026's `integration.skill` install verb. Unlike
    /// <see cref="ResolveGateAsync"/>/<see cref="SupersedeAttemptAsync"/>'s human-only mandatory
    /// confirmation, `integration_write_confirm` is an ordinary permission -- the dialog's own
    /// answer is still passed through as <c>confirmed</c>, but a decline does not itself short-
    /// circuit the call the way it does for those two, matching <see cref="RecoverAsync"/>'s own
    /// shape exactly (the mutation may still succeed via a configured bypass). Round 1 review found
    /// the dialog originally repeated the action name as its own message instead of naming a
    /// target -- unlike <see cref="RecoverAsync"/> (nothing to name beyond "startup"), this writes
    /// to the project's own working tree, so it reuses <see cref="MainPageViewModel.InitializePrompt"/>'s
    /// shape, the same way <see cref="InitializeAsync"/> already names its own target.</summary>
    private async Task InstallIntegrationAsync()
    {
        string? requestedRoot = ProjectRoot;
        ProjectSnapshot snapshot = await viewModel.GetProjectSnapshotAsync(requestedRoot, CancellationToken.None)
            .ConfigureAwait(true);
        string action = text.Resolve(MessageKeys.IntegrationInstallAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.InitializePrompt(snapshot), action, text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        string message = await viewModel
            .InstallIntegrationAsync(requestedRoot, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        IntegrationWriteResultLabel.Text = message;
    }

    /// <summary>ADR 0026's `integration.skill` remove verb -- same shape as
    /// <see cref="InstallIntegrationAsync"/>, including naming its own target: `remove` deletes a
    /// file from the project's own working tree, the same destructiveness reasoning that made
    /// round 1 review flag the original undescriptive dialog.</summary>
    private async Task RemoveIntegrationAsync()
    {
        string? requestedRoot = ProjectRoot;
        ProjectSnapshot snapshot = await viewModel.GetProjectSnapshotAsync(requestedRoot, CancellationToken.None)
            .ConfigureAwait(true);
        string action = text.Resolve(MessageKeys.IntegrationRemoveAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.InitializePrompt(snapshot), action, text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        string message = await viewModel
            .RemoveIntegrationAsync(requestedRoot, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        IntegrationWriteResultLabel.Text = message;
    }

    /// <summary>ADR 0027's `sprint.manage` capability -- the `create` verb. Not confirmable
    /// (additive, not destructive): no dialog, matching `forge sprint create`.</summary>
    private async Task CreateSprintAsync()
    {
        string message = await viewModel.CreateSprintAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SprintManageResultLabel.Text = message;
    }

    /// <summary>ADR 0027's `sprint.manage` capability -- the `run` verb. Reuses
    /// <see cref="SprintId"/> the same way <see cref="ResolveGateAsync"/>/<see cref="SupersedeAttemptAsync"/>
    /// already do: blank means the active sprint, resolved entirely inside
    /// <see cref="MainPageViewModel.RunSprintAsync"/>. Not confirmable.</summary>
    private async Task RunSprintAsync()
    {
        string message = await viewModel.RunSprintAsync(ProjectRoot, SprintId, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SprintManageResultLabel.Text = message;
    }

    /// <summary>ADR 0027's `sprint.manage` capability -- the `resume` verb, same shape as
    /// <see cref="RunSprintAsync"/>.</summary>
    private async Task ResumeSprintAsync()
    {
        string message = await viewModel.ResumeSprintAsync(ProjectRoot, SprintId, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SprintManageResultLabel.Text = message;
    }

    /// <summary>ADR 0027's `sprint.manage` capability -- the `cancel` verb. Ordinarily bypassable
    /// (`workflow_mutate`, not one of <see cref="ResolveGateAsync"/>/<see cref="SupersedeAttemptAsync"/>'s
    /// human-only capabilities), matching <see cref="RecoverAsync"/>/<see cref="InstallIntegrationAsync"/>'s
    /// shape exactly -- the dialog's own answer is still passed through as `confirmed`, but a
    /// decline does not itself short-circuit the call. Reading <see cref="SprintId"/> again after
    /// the dialog is safe, not a TOCTOU risk: `DisplayAlertAsync` is a platform modal that blocks
    /// input to the page beneath, the same reasoning that already applies to
    /// <see cref="ResolveGateAsync"/>/<see cref="SupersedeAttemptAsync"/>'s own post-dialog reads.</summary>
    private async Task CancelSprintAsync()
    {
        string action = text.Resolve(MessageKeys.SprintCancelAction);
        bool confirmed = await DisplayAlertAsync(
                action, viewModel.SprintCancelPrompt(SprintId), action, text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        string message = await viewModel
            .CancelSprintAsync(ProjectRoot, SprintId, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SprintManageResultLabel.Text = message;
    }
}
