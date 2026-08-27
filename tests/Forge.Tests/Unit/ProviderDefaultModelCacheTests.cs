using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>ADR 0063. The default-model cache is a durable file with the same two failure modes the
/// release cache has — absent, and present but unreadable — and both must degrade to "no cache", so a
/// corrupt file costs one extra vendor probe rather than breaking every provider check.</summary>
public sealed class ProviderDefaultModelCacheTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task WrittenEntryRoundTripsThroughRead()
    {
        using TestEnvironment environment = new();
        FileProviderDefaultModelCache cache = new(environment);
        ProviderDefaultModelCacheEntry written = new(DateTimeOffset.UtcNow, true, "gpt-5.6-sol");

        await cache.WriteAsync(new ProviderId("codex"), written, TestContext.Current.CancellationToken);
        ProviderDefaultModelCacheEntry? read =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(written.CheckedAt, read!.CheckedAt);
        Assert.Equal(written.Succeeded, read.Succeeded);
        Assert.Equal(written.Model, read.Model);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadDegradesToNullInsteadOfThrowingWhenTheCacheFileIsCorrupt()
    {
        using TestEnvironment environment = new();
        FileProviderDefaultModelCache cache = new(environment);
        string path = Path.Combine(
            FileProviderReleaseCache.ProviderStateDirectory(environment),
            "default-model-codex.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not json", TestContext.Current.CancellationToken);

        ProviderDefaultModelCacheEntry? entry =
            await cache.ReadAsync(new ProviderId("codex"), TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }
}
