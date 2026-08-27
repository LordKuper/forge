using System.Text.Json;
using Forge.Application;
using Forge.Configuration;

namespace Forge.Providers;

/// <summary>
/// The default-model probe's counterpart to <see cref="FileProviderReleaseCache"/> (ADR 0063), in
/// the same per-instance <c>Forge/{InstanceId}/providers</c> directory, with the same snake_case
/// JSON naming, the same atomic write, and the same degrade-to-"no cache" behaviour on a missing or
/// corrupt file. A separate small type rather than a generalization of the release cache: the two
/// payloads carry different values with different meanings.
/// </summary>
public sealed class FileProviderDefaultModelCache(IEnvironmentPaths paths) : IProviderDefaultModelCache
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<ProviderDefaultModelCacheEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken)
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
            return await JsonSerializer.DeserializeAsync<ProviderDefaultModelCacheEntry>(
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

    public async Task WriteAsync(
        ProviderId id, ProviderDefaultModelCacheEntry entry, CancellationToken cancellationToken)
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
        Path.Combine(FileProviderReleaseCache.ProviderStateDirectory(paths), $"default-model-{id.Value}.json");
}
