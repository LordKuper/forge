using Forge.Host;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Runtime.Windows;

ForgeRuntimeWindowsAdapter.Install();
return await ForgeHostApplication.RunAsync(
    args,
    services => services.AddCodexProvider().AddClaudeProvider(),
    CancellationToken.None);
