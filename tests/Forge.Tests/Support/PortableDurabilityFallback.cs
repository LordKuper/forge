using System.Runtime.CompilerServices;
using Forge.Configuration;

namespace Forge.Tests.Support;

/// <summary>
/// The BCL cannot open a directory handle on Windows (ADR 0007), and the portable net10.0 TFM must not reference
/// the Windows adapter that can. Without this, every test that durably flushes a directory fails with
/// <see cref="UnauthorizedAccessException"/> whenever this TFM happens to run on Windows (e.g. a plain
/// <c>dotnet test</c> with no <c>--framework</c> filter). Falling back to a best-effort flush keeps that
/// combination green; real Windows durability is verified by the net10.0-windows TFM instead (see
/// Installer/WindowsDurabilityAssemblyInitializer.cs), which this file is excluded from.
/// </summary>
internal static class PortableDurabilityFallback
{
    [ModuleInitializer]
    public static void Install()
    {
        if (OperatingSystem.IsWindows())
        {
            DirectoryFlusher.UseDurability(new BestEffortDirectoryDurability());
        }
    }

    private sealed class BestEffortDirectoryDurability : IDirectoryDurability
    {
        private readonly BclDirectoryDurability inner = new();

        public void Flush(string directory)
        {
            try
            {
                inner.Flush(directory);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
