using System.Text.Json;
using Forge.Application;
using Forge.Configuration;

namespace Forge.Providers;

/// <summary>The one canonical per-instance provider-state directory (every cache file and each
/// adapter's probe working directory live here), on a non-generic base so callers never have to name
/// a payload type to ask for a path.</summary>
public abstract class FileProviderJsonCache
{
    public static string ProviderStateDirectory(IEnvironmentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.LocalApplicationData, "Forge", paths.InstanceId, "providers");
    }
}

/// <summary>
/// The file mechanics every per-provider probe cache shares, stated once (round 1 review of PR #123).
/// Release, default-model, and model-catalog caches differ only in their payload record and their
/// file-name prefix; the atomic-write, corrupt-file, and degrade-to-"no cache" contract below is
/// identical for all three, so it lives here instead of being restated in each. The generalization is
/// over the FILE mechanics, not the payloads: <typeparamref name="TEntry"/> keeps each cache's
/// serialized shape, file name, and meaning completely distinct.
///
/// A per-provider, per-instance JSON file under the same <c>Forge/{InstanceId}</c> root every other
/// per-user Forge state uses, so release, Debug, and test instances never share one cache (matching
/// <see cref="ConfigurationStoreFactory.UserPath"/>'s exact pattern). Written atomically via
/// <see cref="AtomicConfigurationFile"/> — the same durable-write primitive the configuration store
/// uses — so a torn write can never corrupt the cache even without any lock protecting it; at worst
/// two concurrent writers race harmlessly for which one's fresher timestamp wins.
/// </summary>
/// <typeparam name="TEntry">The cached payload record, serialized with snake_case property names.</typeparam>
public abstract class FileProviderJsonCache<TEntry>(IEnvironmentPaths paths, string fileNamePrefix)
    : FileProviderJsonCache
    where TEntry : class
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A missing or corrupt cache degrades to "no cache" — the next check just runs fresh,
    /// same as the first check ever performed.</summary>
    public async Task<TEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken)
    {
        string path = CachePath(id);
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
            return await JsonSerializer.DeserializeAsync<TEntry>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Best-effort: a failed cache write just means the next check also runs fresh instead of
    /// hitting the throttle window early — never a reason to fail the caller's own probe.</summary>
    public async Task WriteAsync(ProviderId id, TEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            byte[] contents = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            await AtomicConfigurationFile.WriteAsync(CachePath(id), contents, cancellationToken, retainPrevious: false)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string CachePath(ProviderId id) =>
        Path.Combine(ProviderStateDirectory(paths), $"{fileNamePrefix}{id.Value}.json");
}
