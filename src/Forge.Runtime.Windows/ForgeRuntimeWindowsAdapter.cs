using Forge.Configuration;

namespace Forge.Runtime.Windows;

/// <summary>Installs the Windows-specific durability overrides. Composition roots call this once at startup.</summary>
public static class ForgeRuntimeWindowsAdapter
{
    public static void Install() => DirectoryFlusher.UseDurability(new WindowsDirectoryDurability());
}
