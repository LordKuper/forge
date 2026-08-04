using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Localization;

namespace Forge.Desktop;

public partial class MainPage : ContentPage
{
    private readonly ILocalizationCatalog catalog;
    private readonly ForgeApplication application;

    public MainPage(ILocalizationCatalog catalog, ForgeApplication application)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(application);
        InitializeComponent();
        this.catalog = catalog;
        this.application = application;
        TitleLabel.Text = catalog.Resolve(MessageKeys.AppTitle);
        RefreshButton.Text = catalog.Resolve(MessageKeys.RefreshAction);
        InitializeButton.Text = catalog.Resolve(MessageKeys.InitializeAction);
        ConfigurationTitleLabel.Text = catalog.Resolve(MessageKeys.ConfigurationTitle);
        ConfigurationSetButton.Text = catalog.Resolve(MessageKeys.ConfigurationSetAction);
    }

    /// <summary>Restores the view from durable application state, never from serialized UI objects.</summary>
    public async Task RefreshAsync()
    {
        StartupStatus startup =
            await application.GetStartupStatusAsync(null, CancellationToken.None).ConfigureAwait(true);
        ProjectStatusSnapshot snapshot =
            await application.GetProjectStatusAsync(null, CancellationToken.None).ConfigureAwait(true);
        StatusLabel.Text = catalog.Resolve(StartupMessage(snapshot.Startup));
        ProjectRootLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{catalog.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}");
        ProjectStateLabel.Text = catalog.Resolve(snapshot.Project.Initialized
            ? MessageKeys.ProjectInitialized
            : MessageKeys.ProjectNotInitialized);
        StartupChecksLabel.Text = Render(
            catalog.Resolve(MessageKeys.StartupChecksTitle),
            startup.Checks.Select(check => string.Create(
                CultureInfo.InvariantCulture,
                $"{Machine(check.Id)} {Machine(check.State)} {check.DiagnosticCode}")));
        SuggestedActionsLabel.Text = snapshot.SuggestedActions.Count == 0
            ? catalog.Resolve(MessageKeys.NoSuggestedActions)
            : Render(
                catalog.Resolve(MessageKeys.SuggestedActionsTitle),
                snapshot.SuggestedActions.Select(action => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action.Rank}. {action.ActionId} - {catalog.Resolve(action.RationaleKey)}")));
        InitializeButton.IsEnabled = !snapshot.Project.Initialized;
        ConfigurationLabel.Text = Render(
            null,
            (await application.GetUserConfigurationAsync(CancellationToken.None).ConfigureAwait(true))
                .Concat(await application
                    .GetProjectConfigurationAsync(null, CancellationToken.None)
                    .ConfigureAwait(true))
                .Select(value => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{value.Key} = {value.Value.GetRawText()} ({Machine(value.Provenance)})")));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) =>
        await RefreshAsync().ConfigureAwait(true);

    private async void OnInitializeClicked(object? sender, EventArgs e)
    {
        StartupStatus startup =
            await application.GetStartupStatusAsync(null, CancellationToken.None).ConfigureAwait(true);
        bool confirmed = await DisplayAlertAsync(
                catalog.Resolve(MessageKeys.InitializeAction),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{catalog.Resolve(MessageKeys.ProjectRootLabel)} {startup.Project.Root}"),
                catalog.Resolve(MessageKeys.InitializeAction),
                catalog.Resolve(MessageKeys.CancelAction))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            ConfigurationResultLabel.Text = catalog.Resolve(MessageKeys.InitConfirmationRequired);
            return;
        }

        InitializeProjectResult result = await application
            .InitializeProjectAsync(
                new(startup.Project.Root, true, StatusAdvisor.StateVersion(startup.Project)),
                CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationResultLabel.Text = catalog.Resolve(result.DiagnosticCode switch
        {
            DiagnosticCodes.ProjectAlreadyInitialized => MessageKeys.InitAlreadyInitialized,
            DiagnosticCodes.None => MessageKeys.InitCompleted,
            _ => MessageKeys.InitFailed,
        });
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void OnConfigurationSetClicked(object? sender, EventArgs e)
    {
        ConfigurationWriteResult result = await application
            .SetConfigurationAsync(
                ConfigurationScope.User,
                null,
                ConfigurationKeyEntry.Text ?? string.Empty,
                JsonSerializer.SerializeToElement(ConfigurationValueEntry.Text ?? string.Empty),
                CancellationToken.None)
            .ConfigureAwait(true);
        ConfigurationResultLabel.Text = catalog.Resolve(result.Succeeded
            ? MessageKeys.ConfigurationUpdated
            : MessageKeys.ConfigurationRejected);
        await RefreshAsync().ConfigureAwait(true);
    }

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
