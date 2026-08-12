using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Localization;

namespace Forge.Desktop;

public partial class MainPage : ContentPage
{
    private readonly SurfaceText text;
    private readonly ForgeApplication application;
    private bool busy;

    public MainPage(SurfaceText text, ForgeApplication application)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(application);
        InitializeComponent();
        this.text = text;
        this.application = application;
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

    /// <summary>Restores the view from durable application state, never from serialized UI objects.</summary>
    public async Task RefreshAsync()
    {
        ProjectOverview overview = await application
            .GetOverviewAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
        StartupStatus startup = overview.Startup;
        ProjectSnapshot snapshot = overview.Snapshot;
        StatusLabel.Text = text.Resolve(StartupMessage(snapshot.Startup));
        ProjectRootLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}");
        ProjectStateLabel.Text = text.Resolve(snapshot.Project.Initialized
            ? MessageKeys.ProjectInitialized
            : MessageKeys.ProjectNotInitialized);
        StartupChecksLabel.Text = Render(
            text.Resolve(MessageKeys.StartupChecksTitle),
            startup.Checks.Select(check => string.Create(
                CultureInfo.InvariantCulture,
                $"{Machine(check.Id)} {Machine(check.State)} {check.DiagnosticCode}")));
        SuggestedActionsLabel.Text = snapshot.SuggestedActions.Count == 0
            ? text.Resolve(MessageKeys.NoSuggestedActions)
            : Render(
                text.Resolve(MessageKeys.SuggestedActionsTitle),
                snapshot.SuggestedActions.Select(action => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action.Rank}. {action.ActionId} - {text.Resolve(action.RationaleKey)}")));
        InitializeButton.IsEnabled = !snapshot.Project.Initialized && startup.AllowsProjectMutation;
        RecoverButton.IsEnabled = startup.FirstFailure is not null;
        ConfigurationView user = await application
            .GetUserConfigurationAsync(CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationView project = await application
            .GetProjectConfigurationAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationLabel.Text = Render(
            null,
            user.Values
                .Concat(project.Values)
                .Select(value => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{value.Key} = {value.Value.GetRawText()} ({Machine(value.Provenance)})")));
        DiagnosticsLabel.Text = Render(
            text.Resolve(MessageKeys.DiagnosticsTitle),
            new[]
            {
                startup.Project.DiagnosticCode,
                user.DiagnosticCode,
                project.DiagnosticCode,
            }.Where(code => code != DiagnosticCodes.None).Distinct(StringComparer.Ordinal));
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
        ProjectSnapshot snapshot = await application
            .GetProjectSnapshotAsync(ProjectRoot, CancellationToken.None)
            .ConfigureAwait(true);
        bool confirmed = await DisplayAlertAsync(
                text.Resolve(MessageKeys.InitializeAction),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}"),
                text.Resolve(MessageKeys.InitializeAction),
                text.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            ConfigurationResultLabel.Text = text.Resolve(MessageKeys.InitConfirmationRequired);
            return;
        }

        SuggestedAction? suggestion = snapshot.SuggestedActions.FirstOrDefault(
            action => action.ActionId == ForgeApplication.InitializeProjectAction);
        InitializeProjectResult result = await application
            .InitializeProjectAsync(
                new(
                    snapshot.Project.Root,
                    true,
                    snapshot.StateVersion,
                    suggestion?.Command.IdempotencyKey ??
                        ForgeApplication.InitializationKey(snapshot)),
                CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationResultLabel.Text = Message(
            text.Resolve(result.DiagnosticCode switch
            {
                DiagnosticCodes.ProjectAlreadyInitialized => MessageKeys.InitAlreadyInitialized,
                DiagnosticCodes.None => MessageKeys.InitCompleted,
                _ => MessageKeys.InitFailed,
            }),
            result.DiagnosticCode);
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
        RecoverStartupResult result = await application
            .RecoverStartupAsync(ProjectRoot, confirmed, CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationResultLabel.Text = Message(
            text.Resolve(result switch
            {
                { Succeeded: true, Check: null } => MessageKeys.RecoveryNotNeeded,
                { Succeeded: true } => MessageKeys.RecoveryCompleted,
                _ => MessageKeys.RecoveryFailed,
            }),
            result.DiagnosticCode);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SetConfigurationAsync()
    {
        ConfigurationScope scope = ConfigurationScopePicker.SelectedIndex == 1
            ? ConfigurationScope.Project
            : ConfigurationScope.User;
        ConfigurationWriteResult result = await application
            .SetConfigurationAsync(
                scope,
                ProjectRoot,
                ConfigurationKeyEntry.Text ?? string.Empty,
                ConfigurationValueEntry.Text,
                CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationResultLabel.Text = Message(
            text.Resolve(result.Succeeded
                ? MessageKeys.ConfigurationUpdated
                : MessageKeys.ConfigurationRejected),
            result.DiagnosticCode);
        await RefreshAsync().ConfigureAwait(true);
    }

    private static string Message(string message, string diagnosticCode) =>
        diagnosticCode == DiagnosticCodes.None
            ? message
            : string.Create(CultureInfo.InvariantCulture, $"{message} ({diagnosticCode})");

    private static string Render(string? title, IEnumerable<string> lines)
    {
        StringBuilder builder = new();
        if (title is not null)
        {
            builder.AppendLine(title);
        }

        foreach (string line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static string StartupMessage(StartupState state) => state switch
    {
        StartupState.Ready => MessageKeys.StartupReady,
        StartupState.Blocked => MessageKeys.StartupBlocked,
        _ => MessageKeys.StartupFailed,
    };

    private static string Machine<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!);
}
