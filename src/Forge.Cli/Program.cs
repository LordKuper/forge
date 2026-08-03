using System.CommandLine;
using Forge.Bootstrap;
using Forge.Cli;
using Forge.Localization;
using Forge.Updater;
using Forge.Updater.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using IHost host = ForgeHost.CreateBuilder()
    .ConfigureServices(services => services.AddForgeWindowsUpdater())
    .Build();
ILocalizationCatalog catalog = host.Services.GetRequiredService<ILocalizationCatalog>();
if (args.Length >= 2 && string.Equals(args[0], "--restart-token", StringComparison.Ordinal))
{
    UpdateDiagnostic handshake = new StartupHandshake(host.Services.GetRequiredService<IRestartTokenService>()).Confirm(
        args[1],
        new(
            SemanticVersion.Parse(typeof(CliApplication).Assembly.GetName().Version!.ToString(3)),
            host.Services.GetRequiredService<IUpdateTargetDetector>().Detect(),
            UpdateSurface.Cli));
    if (handshake.Code != UpdateDiagnosticCode.None)
    {
        return 1;
    }

    args = args[2..];
}

if (args is ["--self-test"])
{
    return 0;
}

WindowsInstaller installer = host.Services.GetRequiredService<WindowsInstaller>();
IForgeSelfUpdater updater = host.Services.GetRequiredService<IForgeSelfUpdater>();
RootCommand root = CliApplication.CreateRootCommand(
    catalog,
    Console.Out,
    installer.InstallLatestAsync,
    cancellationToken => updater.UpdateAsync(
        new(
            SemanticVersion.Parse(typeof(CliApplication).Assembly.GetName().Version!.ToString(3)),
            Environment.ProcessPath ?? throw new InvalidOperationException("The Forge executable path is unavailable."),
            ["status"],
            Environment.CurrentDirectory,
            UpdateSurface.Cli),
        cancellationToken));
return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
