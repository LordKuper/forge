using Forge.Cli;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Runtime.Windows;
using Forge.Updater.Windows;

ForgeRuntimeWindowsAdapter.Install();
return await CliHost.RunAsync(
    args,
    services => services.AddForgeWindowsUpdater().AddCodexProvider().AddClaudeProvider(),
    CancellationToken.None);
