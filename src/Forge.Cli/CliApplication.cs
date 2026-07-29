using System.CommandLine;
using Forge.Localization;

namespace Forge.Cli;

public static class CliApplication
{
    public static RootCommand CreateRootCommand(
        ILocalizationCatalog catalog,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(output);

        RootCommand root = new(catalog.Resolve(MessageKeys.AppDescription));
        Command status = new("status", catalog.Resolve(MessageKeys.StatusDescription));
        status.SetAction(_ => output.WriteLine(catalog.Resolve(MessageKeys.StatusReady)));
        root.Subcommands.Add(status);
        return root;
    }
}
