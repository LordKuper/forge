using System.Text.Json;
using Forge.Application;
using Forge.Configuration;

namespace Forge.Providers;

/// <summary>
/// A per-provider, per-instance JSON file under the same <c>Forge/{InstanceId}</c> root every
/// other per-user Forge state uses, so release, Debug, and test instances never share one cache
/// (matching <see cref="ConfigurationStoreFactory.UserPath"/>'s exact pattern). Written atomically
/// via <see cref="AtomicConfigurationFile"/> — the same durable-write primitive the configuration
/// store uses — so a torn write can never corrupt the cache even without any lock protecting it;
/// at worst two concurrent writers race harmlessly for which one's fresher timestamp wins.
/// </summary>
public sealed class FileProviderReleaseCache(IEnvironmentPaths paths) : IProviderReleaseCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The one canonical per-instance provider-state directory (release cache files and
    /// adapter authentication-probe working directories both live here) — every caller must go
    /// through this instead of re-spelling the path.</summary>
    public static string ProviderStateDirectory(IEnvironmentPaths paths) =>
        Path.Combine(paths.LocalApplicationData, "Forge", paths.InstanceId, "providers");

    public async Task<ProviderReleaseCacheEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken)
    {
        string path = CachePath(paths, id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<ProviderReleaseCacheEntry>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // A missing or corrupt cache degrades to "no cache" — the next check just runs fresh,
            // same as the first check ever performed.
            return null;
        }
    }

    public async Task WriteAsync(ProviderId id, ProviderReleaseCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            byte[] contents = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            await AtomicConfigurationFile.WriteAsync(
                CachePath(paths, id),
                contents,
                cancellationToken,
                retainPrevious: false).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed cache write just means the next check also runs fresh instead
            // of hitting the throttle window early — never a reason to fail the caller's own probe.
        }
    }

    private static string CachePath(IEnvironmentPaths paths, ProviderId id) =>
        Path.Combine(ProviderStateDirectory(paths), $"release-cache-{id.Value}.json");
}
