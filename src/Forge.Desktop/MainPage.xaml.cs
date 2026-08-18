using Forge.Application;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Localization;

namespace Forge.Desktop;

public partial class MainPage : ContentPage
{
    private readonly SurfaceText text;
    private readonly MainPageViewModel viewModel;
    private bool busy;

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
        Describe(ConfigurationKeyEntry, text.Resolve(MessageKeys.ConfigurationKeyLabel));
        Describe(ConfigurationValueEntry, text.Resolve(MessageKeys.ConfigurationValueLabel));
        GateApproveButton.Text = text.Resolve(MessageKeys.GateApproveAction);
        GateRejectButton.Text = text.Resolve(MessageKeys.GateRejectAction);
        ConfigurationSetButton.Text = text.Resolve(MessageKeys.ConfigurationSetAction);
        // Actions stay disabled until the first refresh reports the durable state.
        InitializeButton.IsEnabled = false;
        RecoverButton.IsEnabled = false;
        // Scope names are machine identifiers and stay culture invariant.
        ConfigurationScopePicker.ItemsSource = new List<string> { "user", "project" };
        ConfigurationScopePicker.SelectedIndex = 0;
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

    public async Task RefreshAsync()
    {
        MainPageSnapshot snapshot = await viewModel.RefreshAsync(ProjectRoot, SprintId, CancellationToken.None)
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
}
