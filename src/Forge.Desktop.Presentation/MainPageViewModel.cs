using System.Globalization;
using System.Text;
using Forge.Application;
using Forge.Configuration;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>Everything the Desktop main page renders, resolved from durable application state.</summary>
public sealed record MainPageSnapshot(
    string StatusText,
    string ProjectRootText,
    string ProjectStateText,
    string StartupChecksText,
    string ProvidersText,
    string SuggestedActionsText,
    string ConfigurationText,
    string DiagnosticsText,
    bool InitializeEnabled,
    bool RecoverEnabled);

/// <summary>
/// The Desktop main page's reusable orchestration, independent of any MAUI/WinUI control. The Windows host
/// (<c>Forge.Desktop</c>) assigns each resolved snapshot field to its controls; it owns no application logic itself.
/// </summary>
public sealed class MainPageViewModel(
    SurfaceText text,
    ForgeApplication application,
    Func<string?, CancellationToken, Task<IForgeMutations>>? resolveMutations = null)
{
    private readonly SurfaceText text = text ?? throw new ArgumentNullException(nameof(text));
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    // ADR 0005: every `.forge/` mutation routes through the project's Host once one is reachable.
    // A caller that supplied none (every existing test, and any bootstrap path where no project is
    // initialized yet) falls back to the local ForgeApplication, matching CliApplication's own
    // default.
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations =
        resolveMutations ?? ((_, _) => Task.FromResult<IForgeMutations>(application));

    /// <summary>Restores the view from durable application state, never from serialized UI objects.</summary>
    public async Task<MainPageSnapshot> RefreshAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        ProjectOverview overview = await application
            .GetOverviewAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        StartupStatus startup = overview.Startup;
        ProjectSnapshot snapshot = overview.Snapshot;
        ConfigurationView user = await application
            .GetUserConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        ConfigurationView project = await application
            .GetProjectConfigurationAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        return new(
            text.Resolve(SurfaceFormatting.StartupMessageKey(snapshot.Startup)),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}"),
            text.Resolve(snapshot.Project.Initialized
                ? MessageKeys.ProjectInitialized
                : MessageKeys.ProjectNotInitialized),
            Render(
                text.Resolve(MessageKeys.StartupChecksTitle),
                startup.Checks.Select(check => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{SurfaceFormatting.Machine(check.Id)} {SurfaceFormatting.Machine(check.State)} {check.DiagnosticCode}"))),
            Render(
                text.Resolve(MessageKeys.ProviderToolchainTitle),
                snapshot.Providers.Select(SurfaceFormatting.ProviderRow)),
            snapshot.SuggestedActions.Count == 0
                ? text.Resolve(MessageKeys.NoSuggestedActions)
                : Render(
                    text.Resolve(MessageKeys.SuggestedActionsTitle),
                    snapshot.SuggestedActions.Select(action => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{action.Rank}. {action.ActionId} - {text.Resolve(action.RationaleKey)}"))),
            Render(
                null,
                user.Values
                    .Concat(project.Values)
                    .Select(value => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{value.Key} = {value.Value.GetRawText()} ({SurfaceFormatting.Machine(value.Provenance)})"))),
            Render(
                text.Resolve(MessageKeys.DiagnosticsTitle),
                new[]
                {
                    startup.Project.DiagnosticCode,
                    user.DiagnosticCode,
                    project.DiagnosticCode,
                }.Where(code => code != DiagnosticCodes.None).Distinct(StringComparer.Ordinal)),
            !snapshot.Project.Initialized && startup.AllowsProjectMutation,
            startup.FirstFailure is not null);
    }

    public Task<ProjectSnapshot> GetProjectSnapshotAsync(string? projectRoot, CancellationToken cancellationToken) =>
        application.GetProjectSnapshotAsync(projectRoot, cancellationToken);

    public string InitializePrompt(ProjectSnapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}");

    public async Task<string> InitializeAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SuggestedAction? suggestion = snapshot.SuggestedActions.FirstOrDefault(
            action => action.ActionId == ForgeApplication.InitializeProjectAction);
        InitializeProjectResult result = await application
            .InitializeProjectAsync(
                new(
                    snapshot.Project.Root,
                    true,
                    snapshot.StateVersion,
                    suggestion?.Command.IdempotencyKey ?? ForgeApplication.InitializationKey(snapshot)),
                cancellationToken)
            .ConfigureAwait(false);
        return Message(
            text.Resolve(result.DiagnosticCode switch
            {
                DiagnosticCodes.ProjectAlreadyInitialized => MessageKeys.InitAlreadyInitialized,
                DiagnosticCodes.None => MessageKeys.InitCompleted,
                _ => MessageKeys.InitFailed,
            }),
            result.DiagnosticCode);
    }

    public async Task<string> RecoverAsync(string? projectRoot, bool confirmed, CancellationToken cancellationToken)
    {
        IForgeMutations mutations = await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false);
        try
        {
            RecoverStartupResult result = await mutations
                .RecoverStartupAsync(projectRoot, confirmed, cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result switch
                {
                    { Succeeded: true, Check: null } => MessageKeys.RecoveryNotNeeded,
                    { Succeeded: true } => MessageKeys.RecoveryCompleted,
                    _ => MessageKeys.RecoveryFailed,
                }),
                result.DiagnosticCode);
        }
        finally
        {
            if (mutations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<string> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        // User-scope configuration is not `.forge/` project state (ADR 0005 protects the latter),
        // so it stays local even when a Host connection is available for project mutations.
        IForgeMutations mutations = scope == ConfigurationScope.Project
            ? await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false)
            : application;
        try
        {
            ConfigurationWriteResult result = await mutations
                .SetConfigurationAsync(scope, projectRoot, key, value, cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result.Succeeded ? MessageKeys.ConfigurationUpdated : MessageKeys.ConfigurationRejected),
                result.DiagnosticCode);
        }
        finally
        {
            if (mutations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
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

}
