using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Providers;

/// <summary>
/// `Sha256` is GitHub's own per-asset digest (the API's `digest` field), computed by GitHub at
/// upload time. It is authoritative independent of whatever checksum manifest, if any, the
/// vendor also publishes as a release asset.
/// </summary>
public sealed record ProviderReleaseAsset(string Name, long Size, Uri DownloadUri, string? Sha256);

public sealed record ProviderRelease(string Version, IReadOnlyList<ProviderReleaseAsset> Assets);

/// <summary>Looks up the latest published release of a vendor's provider CLI repository.</summary>
public interface IProviderReleaseClient
{
    Task<ProviderRelease?> GetLatestAsync(string owner, string repo, CancellationToken cancellationToken);
}

/// <summary>
/// Queries GitHub's `releases/latest` endpoint, which already excludes drafts and prereleases.
/// The version is extracted from `tag_name` by pattern rather than a fixed prefix, since vendors
/// tag releases differently (for example `v2.1.221` versus `rust-v0.146.0`).
/// </summary>
public sealed partial class GitHubProviderReleaseClient(HttpClient client) : IProviderReleaseClient
{
    private readonly HttpClient client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<ProviderRelease?> GetLatestAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        Uri uri = new($"https://api.github.com/repos/{owner}/{repo}/releases/latest", UriKind.Absolute);
        using HttpRequestMessage message = new(HttpMethod.Get, uri);
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Forge", "1"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return TryReadRelease(document.RootElement);
    }

    private static ProviderRelease? TryReadRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out JsonElement tag) ||
            !root.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Match match = VersionPattern().Match(tag.GetString() ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        List<ProviderReleaseAsset> parsedAssets = [];
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out JsonElement name) &&
                asset.TryGetProperty("size", out JsonElement size) &&
                size.TryGetInt64(out long length) &&
                asset.TryGetProperty("browser_download_url", out JsonElement download) &&
                Uri.TryCreate(download.GetString(), UriKind.Absolute, out Uri? downloadUri))
            {
                parsedAssets.Add(new(name.GetString()!, length, downloadUri, ReadSha256Digest(asset)));
            }
        }

        return new(match.Value, parsedAssets);
    }

    private static string? ReadSha256Digest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out JsonElement digest) || digest.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string value = digest.GetString() ?? string.Empty;
        return value.StartsWith("sha256:", StringComparison.Ordinal) ? value["sha256:".Length..] : null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
