using System.Text.Json;
using Forge.Application;
using Forge.Configuration;

namespace Forge.Providers;

/// <summary>
/// The model-enumeration probe's counterpart to <see cref="FileProviderDefaultModelCache"/> (ADR
/// 0066), in the same per-instance <c>Forge/{InstanceId}/providers</c> directory, with the same
/// snake_case JSON naming, the same atomic write, and the same degrade-to-"no cache" behaviour on a
/// missing or corrupt file.
/// </summary>
public sealed class FileProviderModelCatalogCache(IEnvironmentPaths paths) : IProviderModelCatalogCache
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<ProviderModelCatalogCacheEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken)
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
            return await JsonSerializer.DeserializeAsync<ProviderModelCatalogCacheEntry>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // A missing or corrupt cache degrades to "no cache" — the next enumeration just runs
            // fresh, same as the first one ever performed.
            return null;
        }
    }

    public async Task WriteAsync(
        ProviderId id, ProviderModelCatalogCacheEntry entry, CancellationToken cancellationToken)
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
            // Best-effort: a failed cache write just means the next enumeration also runs fresh
            // instead of hitting the throttle window early — never a reason to fail the caller.
        }
    }

    private static string CachePath(IEnvironmentPaths paths, ProviderId id) =>
        Path.Combine(FileProviderReleaseCache.ProviderStateDirectory(paths), $"model-catalog-{id.Value}.json");
}
