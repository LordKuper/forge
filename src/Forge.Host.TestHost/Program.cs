using Forge.Host;
using Forge.Runtime.Posix;

// A cross-platform stand-in for Forge.Host.Windows: same runtime, no real ILlmProvider
// registered, so the Host/protocol/lease process tests run on Windows, Linux, and macOS
// without depending on any Windows-only provider adapter. No Windows-only adapter is installed
// here either (this process must keep working identically on Linux/macOS), so on Windows it runs
// with the cross-platform NullProcessContainment default -- the real Windows guarantee (plan
// section 12.4) is exercised only against Forge.Host.Windows/Forge.Cli.Windows/Forge.Desktop,
// which do install it. On Linux/macOS this installs the best-effort process-group adapter; see
// PosixProcessGroupContainment's own doc comment for exactly what that does and does not
// guarantee.
return await ForgeHostApplication.RunAsync(
    args,
    services =>
    {
        if (!OperatingSystem.IsWindows())
        {
            services.AddForgeRuntimePosixProcessContainment();
        }
    },
    CancellationToken.None);
