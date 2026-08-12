using Microsoft.Win32.SafeHandles;

namespace Forge.Configuration;

/// <summary>Durably flushes a directory entry to disk after a rename/create inside it.</summary>
public interface IDirectoryDurability
{
    void Flush(string directory);
}

/// <summary>
/// Portable default. <see cref="File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)"/>
/// refuses to open a directory on every current .NET platform (Windows needs backup-semantics this API cannot
/// request; Linux/macOS reject it too), so this is a best-effort no-op rather than the durability guarantee its
/// name implies. A composed <see cref="IDirectoryDurability"/> adapter is required for a real guarantee on any
/// platform; see ADR 0007. Ceiling: no directory-entry durability without one. Upgrade path: an OS adapter (the
/// Windows one already exists) or, if Linux/macOS ship, an adapter using a raw <c>open</c>/<c>fsync</c> P/Invoke.
/// </summary>
public sealed class BclDirectoryDurability : IDirectoryDurability
{
    public void Flush(string directory)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                directory,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.FlushToDisk(handle);
        }
        catch (UnauthorizedAccessException)
        {
        }
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
