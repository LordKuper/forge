using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Forge.Updater;

public sealed class GitHubReleaseApi : IReleaseApi
{
    private static readonly Uri ReleasesUri = new(
        "https://api.github.com/repos/LordKuper/forge/releases?per_page=100",
        UriKind.Absolute);
    private readonly HttpClient client;

    public GitHubReleaseApi(HttpClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async ValueTask<ReleaseApiResponse> GetReleasesAsync(
        ReleaseApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using HttpRequestMessage message = new(HttpMethod.Get, ReleasesUri);
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Forge", "1"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(request.EntityTag))
        {
            message.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(request.EntityTag));
        }

        using HttpResponseMessage response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new(true, response.Headers.ETag?.Tag, Array.Empty<ReleaseMetadata>());
        }

        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub releases response must be a JSON array.");
        }

        List<ReleaseMetadata> releases = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (TryReadRelease(item, out ReleaseMetadata? release))
            {
                releases.Add(release!);
            }
        }

        return new(false, response.Headers.ETag?.Tag, releases);
    }

    private static bool TryReadRelease(JsonElement item, out ReleaseMetadata? release)
    {
        release = null;
        if (!item.TryGetProperty("tag_name", out JsonElement tag) ||
            !SemanticVersion.TryParse(tag.GetString(), out SemanticVersion? version) ||
            !item.TryGetProperty("html_url", out JsonElement url) ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? releaseUri) ||
            !item.TryGetProperty("published_at", out JsonElement published) ||
            !published.TryGetDateTimeOffset(out DateTimeOffset publishedAt) ||
            !item.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<ReleaseAsset> parsedAssets = [];
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out JsonElement name) &&
                asset.TryGetProperty("size", out JsonElement size) &&
                size.TryGetInt64(out long length) &&
                asset.TryGetProperty("browser_download_url", out JsonElement download) &&
                Uri.TryCreate(download.GetString(), UriKind.Absolute, out Uri? downloadUri))
            {
                parsedAssets.Add(new ReleaseAsset(name.GetString()!, length, downloadUri));
            }
        }

        release = new ReleaseMetadata(
            version!,
            releaseUri,
            item.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean(),
            item.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean(),
            publishedAt,
            parsedAssets);
        return true;
    }
}
