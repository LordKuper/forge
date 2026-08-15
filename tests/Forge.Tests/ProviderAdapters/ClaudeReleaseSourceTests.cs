using Forge.Providers;
using Forge.Providers.Claude;
using Forge.Tests.Support;

namespace Forge.ProviderAdapterTests;

public sealed class ClaudeReleaseSourceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ParsesTheVersionFromTheRealStableChannelResponseShape()
    {
        // The exact shape the stable channel endpoint returns today: a bare version string.
        ClaudeReleaseSource source = new(new FakeNetworkClient("2.1.224"));

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new Version(2, 1, 224), result.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TrimsTrailingWhitespaceFromThePlainTextResponse()
    {
        ClaudeReleaseSource source = new(new FakeNetworkClient("2.1.224\n"));

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new Version(2, 1, 224), result.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailsWithoutThrowingWhenTheResponseIsNotAVersion()
    {
        ClaudeReleaseSource source = new(new FakeNetworkClient("<html>not a version</html>"));

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailsWithoutThrowingWhenTheNetworkRequestItselfFails()
    {
        ClaudeReleaseSource source = new(new ThrowingNetworkClient());

        ProviderReleaseLookupResult result =
            await source.FetchLatestVersionAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

}
