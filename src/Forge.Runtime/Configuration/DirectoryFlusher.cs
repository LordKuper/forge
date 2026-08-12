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
/// request; Linux/macOS reject it too), so this throws on every platform until a composed
/// <see cref="IDirectoryDurability"/> adapter overrides it; see ADR 0007. Failing closed here is deliberate: a
/// composition root that forgets to install a real adapter must find out immediately, not lose the durability
/// guarantee silently. Test hosts that cannot compose a platform adapter install their own best-effort override
/// instead of weakening this default; see tests/Forge.Tests/Support/PortableDurabilityFallback.cs.
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
