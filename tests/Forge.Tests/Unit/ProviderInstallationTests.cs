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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsTheRealDiagnosticWhenTheLockCannotBeAcquiredForAMissingProvider()
    {
        FakeClock clock = new();
        InMemoryReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));
        NeverAcquiresLock installLock = new();

        ProviderStatus status = await ProviderInstallation.InstallOrUpdateAsync(
            Id,
            Spec(installed: false),
            LocalVersionRunner("0.146.0"), // never invoked: the executable path does not exist
            releaseSource,
            cache,
            installLock,
            clock,
            ProviderInstallation.DefaultVersionProbeTimeout,
            ProviderInstallation.DefaultInstallTimeout,
            ProviderInstallation.DefaultInstallLockTimeout,
            TestContext.Current.CancellationToken);

        // Regression: the lock-not-acquired branch used to synthesize a generic UpdateFailed
        // status here, discarding the real Missing diagnostic.
        Assert.Equal(ProviderState.Missing, status.State);
        Assert.Equal(ProviderDiagnosticCodes.Missing, status.DiagnosticCode);
        Assert.Equal(0, releaseSource.CallCount); // missing/broken skips the release comparison entirely
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateStaysReadyWhenAMutationAttemptFailsButThePriorInstallIsStillUsable()
    {
        FakeClock clock = new();
        InMemoryReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 148, 0)));
        AlwaysAcquiresLock installLock = new();
        SequencedProcessRunner runner = new(
            new ProcessResult(0, "0.146.0", string.Empty), // 1: local probe, current version
            new ProcessResult(1, string.Empty, "install failed"), // 2: install attempt fails
            new ProcessResult(0, "0.146.0", string.Empty)); // 3: post-failure recheck still works

        ProviderStatus status = await ProviderInstallation.InstallOrUpdateAsync(
            Id,
            Spec(),
            runner,
            releaseSource,
            cache,
            installLock,
            clock,
            ProviderInstallation.DefaultVersionProbeTimeout,
            ProviderInstallation.DefaultInstallTimeout,
            ProviderInstallation.DefaultInstallLockTimeout,
            TestContext.Current.CancellationToken);

        // ADR 0008: "An update failure blocks only when the installed provider is no longer usable."
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
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

    /// <summary>Returns each response in order, then repeats the last one for any further call.</summary>
    private sealed class SequencedProcessRunner(params ProcessResult[] responses) : IProcessRunner
    {
        private int index;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            int position = Math.Min(index, responses.Length - 1);
            index++;
            return Task.FromResult(responses[position]);
        }
    }

    private sealed class NeverAcquiresLock : IProviderInstallLock
    {
        public Task<IProviderInstallLease?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<IProviderInstallLease?>(null);
    }

    private sealed class AlwaysAcquiresLock : IProviderInstallLock
    {
        public Task<IProviderInstallLease?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<IProviderInstallLease?>(new Lease());

        private sealed class Lease : IProviderInstallLease
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
