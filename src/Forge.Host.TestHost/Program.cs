using Forge.Host;

// A cross-platform stand-in for Forge.Host.Windows: same runtime, no real ILlmProvider
// registered, so the Host/protocol/lease process tests run on Windows, Linux, and macOS
// without depending on any Windows-only provider adapter.
return await ForgeHostApplication.RunAsync(args, _ => { }, CancellationToken.None);
