using Microsoft.Win32.SafeHandles;

namespace Forge.Configuration;

/// <summary>Durably flushes a directory entry to disk after a rename/create inside it.</summary>
public interface IDirectoryDurability
{
    void Flush(string directory);
}

/// <summary>
/// Portable default: works everywhere <see cref="File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)"/>
/// can open the directory as a handle. On Windows a directory handle needs backup-semantics, which this API cannot
/// request, so a composed <see cref="IDirectoryDurability"/> adapter is required for a real Windows deployment; see
/// ADR 0007.
/// </summary>
public sealed class BclDirectoryDurability : IDirectoryDurability
{
    public void Flush(string directory)
    {
        using SafeFileHandle handle = File.OpenHandle(
            directory,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.FlushToDisk(handle);
    }
}

public static class DirectoryFlusher
{
    private static IDirectoryDurability durability = new BclDirectoryDurability();

    public static void Flush(string directory) => durability.Flush(directory);

    /// <summary>
    /// Installs a platform-specific durability strategy. Composition roots call this once at startup, before any
    /// flush; it is not safe to swap concurrently with in-flight flushes.
    /// </summary>
    public static void UseDurability(IDirectoryDurability strategy) =>
        durability = strategy ?? throw new ArgumentNullException(nameof(strategy));
}
