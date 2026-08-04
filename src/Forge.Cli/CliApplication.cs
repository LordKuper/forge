using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Localization;
using Forge.Updater;
using Forge.Updater.Windows;

namespace Forge.Cli;

public static class CliApplication
{
    public static RootCommand CreateRootCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application,
        Func<CancellationToken, ValueTask<WindowsInstallationResult>>? install = null,
        Func<CancellationToken, ValueTask<UpdateResult>>? update = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(application);

        RootCommand root = new(catalog.Resolve(MessageKeys.AppDescription));
        root.Subcommands.Add(CreateDoctorCommand(catalog, output, application));
        root.Subcommands.Add(CreateInitCommand(catalog, output, application));
        root.Subcommands.Add(CreateStatusCommand(catalog, output, application));
        root.Subcommands.Add(CreateNextCommand(catalog, output, application));
        root.Subcommands.Add(CreateConfigCommand(catalog, output, application));
        if (install is not null)
        {
            root.Subcommands.Add(CreateInstallCommand(catalog, output, install));
        }

        if (update is not null)
        {
            root.Subcommands.Add(CreateUpdateCommand(catalog, output, update));
        }

        return root;
    }

    private static Option<string?> CreateProjectRootOption() =>
        new("--project-root") { Description = "Absolute project directory." };

    private static Option<bool> CreateJsonOption() =>
        new("--json") { Description = "Emit the culture-invariant machine contract." };

    private static Command CreateDoctorCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> startup = new("--startup") { Description = "Show the startup checks." };
        Command command = new("doctor", catalog.Resolve(MessageKeys.DoctorDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(startup);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            StartupStatus status = await application
                .GetStartupStatusAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(catalog.Resolve(StartupMessage(status.State)));
            output.WriteLine(catalog.Resolve(MessageKeys.StartupChecksTitle));
            foreach (StartupCheck check in status.Checks)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {Machine(check.Id)} {Machine(check.State)} {check.DiagnosticCode}"));
            }

            WriteProject(catalog, output, status.Project);
            return status.State == StartupState.Failed ? 1 : 0;
        });
        return command;
    }

    private static Command CreateInitCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> confirm = new("--yes") { Description = "Confirm the displayed directory." };
        Command command = new("init", catalog.Resolve(MessageKeys.InitDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            InitializeProjectResult result = await application
                .InitializeProjectAsync(
                    new(parseResult.GetValue(projectRoot), parseResult.GetValue(confirm)),
                    cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{catalog.Resolve(MessageKeys.ProjectRootLabel)} {result.Root}"));
            output.WriteLine(catalog.Resolve(InitMessage(result)));
            if (!result.Succeeded)
            {
                output.WriteLine(result.DiagnosticCode);
            }

            return result.Succeeded ? 0 : 1;
        });
        return command;
    }

    private static Command CreateStatusCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> json = CreateJsonOption();
        Command command = new("status", catalog.Resolve(MessageKeys.StatusDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ProjectStatusSnapshot snapshot = await application
                .GetProjectStatusAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(snapshot));
                return 0;
            }

            output.WriteLine(catalog.Resolve(StartupMessage(snapshot.Startup)));
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{catalog.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}"));
            output.WriteLine(catalog.Resolve(snapshot.Project.Initialized
                ? MessageKeys.ProjectInitialized
                : MessageKeys.ProjectNotInitialized));
            WriteActions(catalog, output, snapshot.SuggestedActions);
            return 0;
        });
        return command;
    }

    private static Command CreateNextCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> json = CreateJsonOption();
        Command command = new("next", catalog.Resolve(MessageKeys.NextDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            IReadOnlyList<SuggestedAction> actions = await application
                .GetSuggestedActionsAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(actions));
                return 0;
            }

            WriteActions(catalog, output, actions);
            return 0;
        });
        return command;
    }

    private static Command CreateConfigCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Command command = new("config", catalog.Resolve(MessageKeys.ConfigDescription));
        Command show = new("show", catalog.Resolve(MessageKeys.ConfigurationTitle));
        show.Options.Add(projectRoot);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            output.WriteLine(catalog.Resolve(MessageKeys.ConfigurationTitle));
            WriteValues(
                output,
                await application.GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false));
            WriteValues(
                output,
                await application
                    .GetProjectConfigurationAsync(parseResult.GetValue(projectRoot), cancellationToken)
                    .ConfigureAwait(false));
            return 0;
        });
        command.Subcommands.Add(show);
        command.Subcommands.Add(CreateConfigSetCommand(
            catalog,
            output,
            application,
            ConfigurationScope.User));
        command.Subcommands.Add(CreateConfigSetCommand(
            catalog,
            output,
            application,
            ConfigurationScope.Project));
        return command;
    }

    private static Command CreateConfigSetCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        ForgeApplication application,
        ConfigurationScope scope)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Argument<string> key = new("key");
        Argument<string> value = new("value");
        Command command = new(
            scope == ConfigurationScope.User ? "user" : "project",
            catalog.Resolve(MessageKeys.ConfigDescription));
        command.Arguments.Add(key);
        command.Arguments.Add(value);
        command.Options.Add(projectRoot);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ConfigurationWriteResult result = await application
                .SetConfigurationAsync(
                    scope,
                    parseResult.GetValue(projectRoot),
                    parseResult.GetValue(key)!,
                    JsonSerializer.SerializeToElement(parseResult.GetValue(value)),
                    cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(catalog.Resolve(result.Succeeded
                ? MessageKeys.ConfigurationUpdated
                : MessageKeys.ConfigurationRejected));
            if (!result.Succeeded)
            {
                output.WriteLine(result.DiagnosticCode);
            }

            return result.Succeeded ? 0 : 1;
        });
        return command;
    }

    private static Command CreateInstallCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        Func<CancellationToken, ValueTask<WindowsInstallationResult>> install)
    {
        Command command = new("install", catalog.Resolve(MessageKeys.InstallDescription));
        command.SetAction(async (_, cancellationToken) =>
        {
            WindowsInstallationResult result = await install(cancellationToken).ConfigureAwait(false);
            output.WriteLine(result.Succeeded
                ? catalog.Resolve(MessageKeys.InstallCompleted)
                : catalog.Resolve(MessageKeys.InstallFailed));
            return result.Succeeded ? 0 : 1;
        });
        return command;
    }

    private static Command CreateUpdateCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        Func<CancellationToken, ValueTask<UpdateResult>> update)
    {
        Command command = new("update", catalog.Resolve(MessageKeys.UpdateDescription));
        command.SetAction(async (_, cancellationToken) =>
        {
            UpdateResult result = await update(cancellationToken).ConfigureAwait(false);
            output.WriteLine(result.Diagnostic.Code == UpdateDiagnosticCode.None
                ? catalog.Resolve(MessageKeys.UpdateCompleted)
                : catalog.Resolve(MessageKeys.UpdateFailed));
            return result.Diagnostic.Code == UpdateDiagnosticCode.None ? 0 : 1;
        });
        return command;
    }

    private static void WriteProject(
        ILocalizationCatalog catalog,
        TextWriter output,
        ProjectRootStatus project)
    {
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{catalog.Resolve(MessageKeys.ProjectRootLabel)} {project.Root}"));
        output.WriteLine(catalog.Resolve(project.Initialized
            ? MessageKeys.ProjectInitialized
            : MessageKeys.ProjectNotInitialized));
    }

    private static void WriteActions(
        ILocalizationCatalog catalog,
        TextWriter output,
        IReadOnlyList<SuggestedAction> actions)
    {
        if (actions.Count == 0)
        {
            output.WriteLine(catalog.Resolve(MessageKeys.NoSuggestedActions));
            return;
        }

        output.WriteLine(catalog.Resolve(MessageKeys.SuggestedActionsTitle));
        foreach (SuggestedAction action in actions)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {action.Rank}. {action.ActionId} - {catalog.Resolve(action.RationaleKey)}"));
        }
    }

    private static void WriteValues(
        TextWriter output,
        IReadOnlyList<EffectiveConfigurationValue> values)
    {
        foreach (EffectiveConfigurationValue value in values)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {value.Key} = {value.Value.GetRawText()} ({Machine(value.Provenance)})"));
        }
    }

    private static string StartupMessage(StartupState state) => state switch
    {
        StartupState.Ready => MessageKeys.StartupReady,
        StartupState.Blocked => MessageKeys.StartupBlocked,
        _ => MessageKeys.StartupFailed,
    };

    private static string InitMessage(InitializeProjectResult result) =>
        result.DiagnosticCode switch
        {
            DiagnosticCodes.ProjectAlreadyInitialized => MessageKeys.InitAlreadyInitialized,
            DiagnosticCodes.ConfirmationRequired => MessageKeys.InitConfirmationRequired,
            DiagnosticCodes.None => MessageKeys.InitCompleted,
            _ => MessageKeys.InitFailed,
        };

    private static string Machine<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!);
}
