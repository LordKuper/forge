using System.Runtime.CompilerServices;
using Forge.Runtime.Windows;

namespace Forge.InstallerTests;

/// <summary>
/// The BCL cannot open a directory handle on Windows (see ADR 0007), so any test that durably flushes a directory
/// needs the real Windows adapter installed, exactly like the composed product. Only the Windows test TFM compiles
/// this file.
/// </summary>
internal static class WindowsDurabilityAssemblyInitializer
{
    [ModuleInitializer]
    public static void Install() => ForgeRuntimeWindowsAdapter.Install();
}
