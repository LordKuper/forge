using Forge.Host;

// A cross-platform stand-in for Forge.Host.Windows: same runtime, no real ILlmProvider
// registered, so the Host/protocol/lease process tests run on Windows, Linux, and macOS
// without depending on any Windows-only provider adapter. No adapter is installed here (this
// process must keep working identically on Windows, Linux, and macOS), so it always runs with
// the cross-platform NullProcessContainment default -- the real Windows guarantee (plan section
// 12.4) is exercised only against Forge.Host.Windows/Forge.Cli.Windows/Forge.Desktop, which do
// install it. Linux/macOS process containment does not exist yet (see IProcessContainment's own
// doc comment for that known limitation).
return await ForgeHostApplication.RunAsync(args, static _ => { }, CancellationToken.None);
