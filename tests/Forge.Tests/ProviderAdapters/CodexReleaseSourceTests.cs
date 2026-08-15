using System.Text;
using Forge.Application;
using Forge.Providers;
using Forge.Providers.Codex;

namespace Forge.ProviderAdapterTests;

public sealed class CodexReleaseSourceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ParsesTheVersionFromARealChannelResponseShape()
    {
        // The exact shape releases.openai.com/codex/channels/latest returns today.
        const string body = """{"tag_name": "rust-v0.147.0", "assets": []}""";
        CodexReleaseSource source = new(new FakeNetworkClient(body));

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new Version(0, 147, 0), result.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailsWithoutThrowingWhenTheTagNameFieldIsMissing()
    {
        CodexReleaseSource source = new(new FakeNetworkClient("""{"assets": []}"""));

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailsWithoutThrowingWhenTheResponseIsNotJson()
    {
        CodexReleaseSource source = new(new FakeNetworkClient("not json"));

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailsWithoutThrowingWhenTheNetworkRequestItselfFails()
    {
        CodexReleaseSource source = new(new ThrowingNetworkClient());

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    private sealed class FakeNetworkClient(string body) : INetworkClient
    {
        public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(body)));
    }

    private sealed class ThrowingNetworkClient : INetworkClient
    {
        public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The endpoint is unreachable.");
    }
}
