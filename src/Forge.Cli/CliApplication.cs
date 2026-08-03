using System.CommandLine;
using Forge.Localization;
using Forge.Updater.Windows;

namespace Forge.Cli;

public static class CliApplication
{
    public static RootCommand CreateRootCommand(
        ILocalizationCatalog catalog,
        TextWriter output,
        Func<CancellationToken, ValueTask<WindowsInstallationResult>>? install = null)
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
                    : result.Diagnostic.Detail);
                return result.Succeeded ? 0 : 1;
            });
            root.Subcommands.Add(installCommand);
        }
        return root;
    }
}
