using System.CommandLine;
using Forge.Localization;
using Forge.Updater;
using Forge.Updater.Windows;

namespace Forge.Cli;

public static class CliApplication
{
    public static RootCommand CreateRootCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        Func<CancellationToken, ValueTask<WindowsInstallationResult>>? install = null,
        Func<CancellationToken, ValueTask<UpdateResult>>? update = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(output);

        RootCommand root = new(catalog.Resolve(MessageKeys.AppDescription));
        Command status = new("status", catalog.Resolve(MessageKeys.StatusDescription));
        status.SetAction(_ => output.WriteLine(catalog.Resolve(MessageKeys.StatusReady)));
        root.Subcommands.Add(status);
        if (install is not null)
        {
            Command installCommand = new("install", catalog.Resolve(MessageKeys.InstallDescription));
            installCommand.SetAction(async _ =>
            {
                WindowsInstallationResult result = await install(CancellationToken.None).ConfigureAwait(false);
                output.WriteLine(result.Succeeded
                    ? catalog.Resolve(MessageKeys.InstallCompleted)
                    : catalog.Resolve(MessageKeys.InstallFailed));
                return result.Succeeded ? 0 : 1;
            });
            root.Subcommands.Add(installCommand);
        }
        if (update is not null)
        {
            Command updateCommand = new("update", catalog.Resolve(MessageKeys.UpdateDescription));
            updateCommand.SetAction(async _ =>
            {
                UpdateResult result = await update(CancellationToken.None).ConfigureAwait(false);
                output.WriteLine(result.Diagnostic.Code == UpdateDiagnosticCode.None
                    ? catalog.Resolve(MessageKeys.UpdateCompleted)
                    : catalog.Resolve(MessageKeys.UpdateFailed));
                return result.Diagnostic.Code == UpdateDiagnosticCode.None ? 0 : 1;
            });
            root.Subcommands.Add(updateCommand);
        }
        return root;
    }
}
