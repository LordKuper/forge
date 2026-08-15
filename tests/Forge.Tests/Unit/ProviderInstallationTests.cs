using Forge.Application;
using Forge.Providers;

namespace Forge.UnitTests;

public sealed class ProviderInstallationTests
{
    private static readonly ProviderId Id = new("codex");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecentSuccessfulCacheEntryIsReusedInsteadOfCheckingAgain()
    {
        FakeClock clock = new() { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        InMemoryReleaseCache cache = new();
        await cache.WriteAsync(
            Id,
            new(clock.UtcNow - TimeSpan.FromHours(1), true, "0.147.0"),
            TestContext.Current.CancellationToken);
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 148, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(),
            LocalVersionRunner("0.146.0"),
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: false,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, releaseSource.CallCount);
        Assert.True(status.UpdateAvailable); // computed from the cached 0.147.0 vs local 0.146.0
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACacheEntryOlderThanTheSuccessWindowTriggersAFreshCheck()
    {
        FakeClock clock = new() { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        InMemoryReleaseCache cache = new();
        await cache.WriteAsync(
            Id,
            new(clock.UtcNow - ProviderInstallation.ReleaseCheckSuccessWindow, true, "0.147.0"),
            TestContext.Current.CancellationToken);
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(),
            LocalVersionRunner("0.146.0"),
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: false,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, releaseSource.CallCount);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecentFailedCacheEntryIsNotRetriedWithinTheFailureWindow()
    {
        FakeClock clock = new() { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        InMemoryReleaseCache cache = new();
        await cache.WriteAsync(
            Id,
            new(clock.UtcNow - TimeSpan.FromMinutes(30), false, null),
            TestContext.Current.CancellationToken);
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(),
            LocalVersionRunner("0.146.0"),
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: false,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, releaseSource.CallCount);
        Assert.Null(status.UpdateAvailable); // a cached failure reports "unknown", never a guess
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFailedCacheEntryOlderThanTheFailureWindowTriggersAFreshCheck()
    {
        FakeClock clock = new() { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        InMemoryReleaseCache cache = new();
        await cache.WriteAsync(
            Id,
            new(clock.UtcNow - ProviderInstallation.ReleaseCheckFailureWindow, false, null),
            TestContext.Current.CancellationToken);
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(),
            LocalVersionRunner("0.146.0"),
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: false,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, releaseSource.CallCount);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BypassingTheCacheAlwaysChecksEvenWithAFreshEntry()
    {
        FakeClock clock = new() { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        InMemoryReleaseCache cache = new();
        await cache.WriteAsync(
            Id,
            new(clock.UtcNow, true, "0.146.0"),
            TestContext.Current.CancellationToken);
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(),
            LocalVersionRunner("0.146.0"),
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: true,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, releaseSource.CallCount);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANeverCheckedProviderChecksImmediately()
    {
        FakeClock clock = new();
        InMemoryReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(),
            LocalVersionRunner("0.146.0"),
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: false,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, releaseSource.CallCount);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADiscoveryFailureNeverConsultsTheReleaseSourceAtAll()
    {
        FakeClock clock = new();
        InMemoryReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));

        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            Spec(installed: false),
            LocalVersionRunner("0.146.0"), // never invoked: the executable path does not exist
            releaseSource,
            cache,
            clock,
            bypassReleaseCache: false,
            ProviderInstallation.DefaultVersionProbeTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Missing, status.State);
        Assert.Null(status.UpdateAvailable);
        Assert.Equal(0, releaseSource.CallCount);
    }

    private static ProviderInstallSpec Spec(bool installed = true)
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            $"forge-installation-tests-{Guid.NewGuid():N}",
            "codex.exe");
        if (installed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            File.WriteAllText(executablePath, "stub");
        }

        return new(executablePath, InstallExecutable: "install.exe", InstallArguments: [], null, null);
    }

    /// <summary>Reports a fixed local `--version` result.</summary>
    private static StubProcessRunner LocalVersionRunner(string version) => new(version);

    private sealed class StubProcessRunner(string? version) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, version ?? string.Empty, string.Empty));
    }

    private sealed class CountingReleaseSource(ProviderReleaseLookupResult result) : IProviderReleaseSource
    {
        public int CallCount { get; private set; }

        public Task<ProviderReleaseLookupResult> FetchLatestVersionAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class InMemoryReleaseCache : IProviderReleaseCache
    {
        private readonly Dictionary<string, ProviderReleaseCacheEntry> entries = new(StringComparer.Ordinal);

        public Task<ProviderReleaseCacheEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken) =>
            Task.FromResult(entries.TryGetValue(id.Value, out ProviderReleaseCacheEntry? entry) ? entry : null);

        public Task WriteAsync(ProviderId id, ProviderReleaseCacheEntry entry, CancellationToken cancellationToken)
        {
            entries[id.Value] = entry;
            return Task.CompletedTask;
        }
    }
}
