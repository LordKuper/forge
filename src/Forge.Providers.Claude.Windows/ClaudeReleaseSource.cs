using Forge.Application;

namespace Forge.Providers.Claude;

/// <summary>
/// Reads Claude Code's own selected release channel metadata (ADR 0008: "Claude Code reads the
/// selected vendor channel metadata") — the `stable` channel endpoint the vendor's installer and
/// updater use, which responds with the plain-text latest version (e.g. <c>2.1.224</c>), nothing
/// else to parse.
/// </summary>
public sealed class ClaudeReleaseSource(INetworkClient network) : IProviderReleaseSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri StableChannelUri = new(
        "https://storage.googleapis.com/claude-code-dist-86c565f3-f756-42ad-8dfa-d59b1c096819/claude-code-releases/stable");

    public async Task<ProviderReleaseLookupResult> FetchLatestVersionAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(Timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await using Stream stream = await network
                .GetStreamAsync(StableChannelUri, linked.Token)
                .ConfigureAwait(false);
            using StreamReader reader = new(stream);
            string text = (await reader.ReadToEndAsync(linked.Token).ConfigureAwait(false)).Trim();
            return Version.TryParse(text, out Version? version)
                ? new(true, version)
                : ProviderReleaseLookupResult.Failed;
        }
        catch (Exception exception) when (
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested) ||
            exception is HttpRequestException or IOException)
        {
            return ProviderReleaseLookupResult.Failed;
        }
    }
}
