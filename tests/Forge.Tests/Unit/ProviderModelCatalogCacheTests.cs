using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>ADR 0066. The catalog cache is a durable file with the same two failure modes the release
/// and default-model caches have — absent, and present but unreadable — and both must degrade to "no
/// cache", so a corrupt file costs one extra vendor probe rather than breaking every model query. Its
/// payload is the only non-scalar one of the three, so the round trip also covers a list surviving
/// real JSON.</summary>
public sealed class ProviderModelCatalogCacheTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task WrittenEntryRoundTripsThroughRead()
    {
        using TestEnvironment environment = new();
        FileProviderModelCatalogCache cache = new(environment);
        ProviderModelCatalogCacheEntry written =
            new(DateTimeOffset.UtcNow, true, ["gpt-5.6-sol", "gpt-5.6-codex"]);

        await cache.WriteAsync(new ProviderId("codex"), written, TestContext.Current.CancellationToken);
        ProviderModelCatalogCacheEntry? read =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(written.CheckedAt, read!.CheckedAt);
        Assert.Equal(written.Succeeded, read.Succeeded);
        Assert.Equal(written.Models, read.Models);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadDegradesToNullInsteadOfThrowingWhenTheCacheFileIsCorrupt()
    {
        using TestEnvironment environment = new();
        FileProviderModelCatalogCache cache = new(environment);
        string path = Path.Combine(
            FileProviderModelCatalogCache.ProviderStateDirectory(environment),
            "model-catalog-codex.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not json", TestContext.Current.CancellationToken);

        ProviderModelCatalogCacheEntry? entry =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    /// <summary>Round 2 review of PR #123. All three caches now share one <c>CachePath</c> over one
    /// directory, so their file-name prefixes are the only thing keeping them apart — and a collision
    /// would be silent, not loud: each would deserialize the other's payload into its own record,
    /// yielding a "recorded but empty" entry that reads as a legitimate cached failure while
    /// clobbering the other cache's throttle on every write. Writing all three for the SAME provider
    /// id and requiring three distinct files, each reading back its own value, is what makes such a
    /// collision fail here instead of downstream.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task EachProviderCacheKeepsItsOwnFileForTheSameProviderId()
    {
        using TestEnvironment environment = new();
        ProviderId id = new("codex");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FileProviderReleaseCache release = new(environment);
        FileProviderDefaultModelCache defaultModel = new(environment);
        FileProviderModelCatalogCache catalog = new(environment);

        await release.WriteAsync(id, new(DateTimeOffset.UtcNow, true, "1.2.3"), cancellationToken);
        await defaultModel.WriteAsync(id, new(DateTimeOffset.UtcNow, true, "gpt-5.6-sol"), cancellationToken);
        await catalog.WriteAsync(id, new(DateTimeOffset.UtcNow, true, ["gpt-5.6-sol"]), cancellationToken);

        string directory = FileProviderModelCatalogCache.ProviderStateDirectory(environment);
        Assert.Equal(
            ["default-model-codex.json", "model-catalog-codex.json", "release-cache-codex.json"],
            Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("1.2.3", (await release.ReadAsync(id, cancellationToken))!.LatestVersion);
        Assert.Equal("gpt-5.6-sol", (await defaultModel.ReadAsync(id, cancellationToken))!.Model);
        Assert.Equal(["gpt-5.6-sol"], (await catalog.ReadAsync(id, cancellationToken))!.Models);
    }
}
