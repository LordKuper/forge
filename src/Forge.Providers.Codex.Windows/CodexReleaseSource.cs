using System.Text.Json;
using Forge.Application;

namespace Forge.Providers.Codex;

/// <summary>
/// Reads Codex's own release channel metadata (ADR 0008: "Codex reads the vendor release
/// metadata used by its own updater") — the same `releases.openai.com` endpoint its own installer
/// script (`install.ps1`) queries for the `latest` channel, returning `{"tag_name": "rust-vX.Y.Z",
/// ...}`.
/// </summary>
public sealed class CodexReleaseSource(INetworkClient network) : IProviderReleaseSource
{
    private const string TagPrefix = "rust-v";

    /// <summary>Only <c>tag_name</c> is read from this response; a bound caps the memory an
    /// unexpectedly large or malicious response can force on this unattended startup path.</summary>
    private const int MaxResponseBytes = 64 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri LatestChannelUri = new("https://releases.openai.com/codex/channels/latest");

    public async Task<ProviderReleaseLookupResult> FetchLatestVersionAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(Timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await using Stream stream = await network
                .GetStreamAsync(LatestChannelUri, linked.Token)
                .ConfigureAwait(false);
            using MemoryStream bounded = new();
            byte[] chunk = new byte[4096];
            int chunkRead;
            while ((chunkRead = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + chunkRead > MaxResponseBytes)
                {
                    return ProviderReleaseLookupResult.Failed;
                }

                bounded.Write(chunk, 0, chunkRead);
            }

            bounded.Position = 0;
            using JsonDocument document = await JsonDocument
                .ParseAsync(bounded, cancellationToken: linked.Token)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("tag_name", out JsonElement tagElement) ||
                tagElement.ValueKind != JsonValueKind.String)
            {
                return ProviderReleaseLookupResult.Failed;
            }

            string tag = tagElement.GetString() ?? string.Empty;
            string versionText = tag.StartsWith(TagPrefix, StringComparison.Ordinal)
                ? tag[TagPrefix.Length..]
                : tag;
            return Version.TryParse(versionText, out Version? version)
                ? new(true, version)
                : ProviderReleaseLookupResult.Failed;
        }
        catch (Exception exception) when (
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested) ||
            exception is HttpRequestException or JsonException or IOException)
        {
            return ProviderReleaseLookupResult.Failed;
        }
    }
}
