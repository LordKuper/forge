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
        ProjectRootResolver rootResolver,
        IConfigurationRegistry registry,
        IEnvironmentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(paths);
        InitializeComponent();
        this.text = text;
        string clientVersion = typeof(MainPage).Assembly.GetName().Version!.ToString(3);
        viewModel = new MainPageViewModel(
            text,
            application,
            (root, cancellationToken) => HostMutationsFactory.CreateAsync(
                rootResolver,
                registry,
                paths,
                application,
                clientVersion,
                root,
                cancellationToken));
        TitleLabel.Text = text.Resolve(MessageKeys.AppTitle);
        RefreshButton.Text = text.Resolve(MessageKeys.RefreshAction);
        InitializeButton.Text = text.Resolve(MessageKeys.InitializeAction);
        RecoverButton.Text = text.Resolve(MessageKeys.RecoverAction);
        ConfigurationTitleLabel.Text = text.Resolve(MessageKeys.ConfigurationTitle);
        ConfigurationSetButton.Text = text.Resolve(MessageKeys.ConfigurationSetAction);
        // Actions stay disabled until the first refresh reports the durable state.
        InitializeButton.IsEnabled = false;
        RecoverButton.IsEnabled = false;
        // Scope names are machine identifiers and stay culture invariant.
        ConfigurationScopePicker.ItemsSource = new List<string> { "user", "project" };
        ConfigurationScopePicker.SelectedIndex = 0;
    }

    private string? ProjectRoot =>
        string.IsNullOrWhiteSpace(ProjectRootEntry.Text) ? null : ProjectRootEntry.Text;

    public async Task RefreshAsync()
    {
        MainPageSnapshot snapshot = await viewModel.RefreshAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
        StatusLabel.Text = snapshot.StatusText;
        ProjectRootLabel.Text = snapshot.ProjectRootText;
        ProjectStateLabel.Text = snapshot.ProjectStateText;
        StartupChecksLabel.Text = snapshot.StartupChecksText;
        ProvidersLabel.Text = snapshot.ProvidersText;
        SuggestedActionsLabel.Text = snapshot.SuggestedActionsText;
        InitializeButton.IsEnabled = snapshot.InitializeEnabled;
        RecoverButton.IsEnabled = snapshot.RecoverEnabled;
        ConfigurationLabel.Text = snapshot.ConfigurationText;
        DiagnosticsLabel.Text = snapshot.DiagnosticsText;
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
}
