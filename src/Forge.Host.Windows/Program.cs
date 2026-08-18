using Forge.Host;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Runtime.Windows;
using Forge.Updater.Windows;

ForgeRuntimeWindowsAdapter.Install();
return await ForgeHostApplication.RunAsync(
    args,
    // AddForgeWindowsUpdater registers WindowsPlatformPreflight (needed by StartupPipeline's
    // platform check, e.g. RecoverStartupAsync) alongside the CLI's own self-update machinery;
    // the Host never calls the latter, but there is no smaller registration to reuse — see
    // WindowsPlatformPreflight's own dependency on the updater's target detector/strategy resolver.
    services => services.AddForgeWindowsUpdater().AddCodexProvider().AddClaudeProvider()
        .AddForgeRuntimeWindowsNotifications(),
    CancellationToken.None);
