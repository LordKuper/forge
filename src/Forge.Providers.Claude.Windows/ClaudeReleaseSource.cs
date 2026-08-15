using Forge.Application;

namespace Forge.Providers.Claude;

/// <summary>
/// Reads Claude Code's release channel metadata (ADR 0008: "Claude Code reads the selected vendor
/// channel metadata") — the plain-text latest version (e.g. <c>2.1.224</c>), nothing else to parse.
/// </summary>
/// <remarks>
/// Deferred: this always reads the <c>stable</c> channel rather than the channel the local
/// install actually selected. Claude Code's CLI supports a <c>latest</c> channel whose installed
/// version can run ahead of <c>stable</c>; on that channel <see cref="FetchLatestVersionAsync"/>
/// under-reports (compares against a version already superseded locally), so
/// <c>DiscoverAsync</c> never reports <see cref="ProviderStatus.UpdateAvailable"/> and
/// <c>InstallOrUpdateAsync</c> never runs the vendor updater for that install — combined with
/// <c>DISABLE_AUTOUPDATER=1</c> during normal execution, a <c>latest</c>-channel install's update
/// cadence goes fully unmanaged until this is resolved by reading the vendor's own recorded
/// channel selection.
/// </remarks>
public sealed class ClaudeReleaseSource(INetworkClient network) : IProviderReleaseSource
{
    private const int MaxResponseChars = 256;
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
            // A version string is a handful of bytes; a bounded read caps the memory an
            // unexpectedly large or malicious response can force on this unattended startup path.
            char[] buffer = new char[MaxResponseChars];
            int read = await reader.ReadBlockAsync(buffer, linked.Token).ConfigureAwait(false);
            string text = new string(buffer, 0, read).Trim();
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
