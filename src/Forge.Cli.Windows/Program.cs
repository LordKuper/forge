using Forge.Cli;
using Forge.Runtime.Windows;
using Forge.Updater.Windows;

ForgeRuntimeWindowsAdapter.Install();
return await CliHost.RunAsync(
    args,
    services => services.AddForgeWindowsUpdater(),
    CancellationToken.None);
