using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderReleaseCacheTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadReturnsNullWhenNoCacheFileExistsYet()
    {
        using TestEnvironment environment = new();
        FileProviderReleaseCache cache = new(environment);

        ProviderReleaseCacheEntry? entry =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WrittenEntryRoundTripsThroughRead()
    {
        using TestEnvironment environment = new();
        FileProviderReleaseCache cache = new(environment);
        ProviderReleaseCacheEntry written = new(DateTimeOffset.UtcNow, true, "0.147.0");

        await cache.WriteAsync(new ProviderId("codex"), written, TestContext.Current.CancellationToken);
        ProviderReleaseCacheEntry? read =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(written.CheckedAt, read!.CheckedAt);
        Assert.Equal(written.Succeeded, read.Succeeded);
        Assert.Equal(written.LatestVersion, read.LatestVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EachProviderHasAnIndependentCacheEntry()
    {
        using TestEnvironment environment = new();
        FileProviderReleaseCache cache = new(environment);
        await cache.WriteAsync(
            new ProviderId("codex"),
            new(DateTimeOffset.UtcNow, true, "0.147.0"),
            TestContext.Current.CancellationToken);

        ProviderReleaseCacheEntry? claudeEntry =
            await cache.ReadAsync(new ProviderId("claude_code"), TestContext.Current.CancellationToken);

        Assert.Null(claudeEntry);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadDegradesToNullInsteadOfThrowingWhenTheCacheFileIsCorrupt()
    {
        using TestEnvironment environment = new();
        FileProviderReleaseCache cache = new(environment);
        string path = Path.Combine(
            environment.LocalApplicationData,
            "Forge",
            environment.InstanceId,
            "providers",
            "release-cache-codex.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not json", TestContext.Current.CancellationToken);

        ProviderReleaseCacheEntry? entry =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASecondWriteOverwritesTheFirstEntryForTheSameProvider()
    {
        using TestEnvironment environment = new();
        FileProviderReleaseCache cache = new(environment);
        ProviderId id = new("codex");
        await cache.WriteAsync(
            id,
            new(DateTimeOffset.UtcNow.AddHours(-1), false, null),
            TestContext.Current.CancellationToken);

        DateTimeOffset latest = DateTimeOffset.UtcNow;
        await cache.WriteAsync(id, new(latest, true, "0.147.0"), TestContext.Current.CancellationToken);
        ProviderReleaseCacheEntry? entry = await cache.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.True(entry!.Succeeded);
        Assert.Equal("0.147.0", entry.LatestVersion);
        Assert.Equal(latest, entry.CheckedAt);
    }
}
