using System.Runtime.CompilerServices;
using Forge.Configuration;

namespace Forge.Tests.Support;

/// <summary>
/// The BCL cannot open a directory handle on any current platform (confirmed on both Windows and Linux; see ADR
/// 0007 and <see cref="BclDirectoryDurability"/>), and the portable net10.0 TFM must not reference an OS adapter
/// that can. <see cref="BclDirectoryDurability"/> deliberately fails closed for production composition roots, so
/// every test that durably flushes a directory would fail with <see cref="UnauthorizedAccessException"/> on this
/// TFM without this test-only override. Real durability is verified by the net10.0-windows TFM instead (see
/// Installer/WindowsDurabilityAssemblyInitializer.cs), which this file is excluded from.
/// </summary>
internal static class PortableDurabilityFallback
{
    [ModuleInitializer]
    public static void Install() => DirectoryFlusher.UseDurability(new BestEffortDirectoryDurability());

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
