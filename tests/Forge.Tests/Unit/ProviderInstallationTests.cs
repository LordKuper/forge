using Forge.Application;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderInstallationTests
{
    private static readonly ProviderId Id = new("codex");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecentSuccessfulCacheEntryIsReusedInsteadOfCheckingAgain()
    {
        FakeClock clock = new() { UtcNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
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
        FakeReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));
        FakeInstallLock installLock = new(acquires: false);

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
        FakeReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 148, 0)));
        FakeInstallLock installLock = new();
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateSkipsTheMutationWhenAnotherProcessAlreadyAppliedItWhileWaitingOnTheLock()
    {
        FakeClock clock = new();
        FakeReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 147, 0)));
        FakeInstallLock installLock = new();
        SequencedProcessRunner runner = new(
            new ProcessResult(0, "0.146.0", string.Empty), // 1: initial local probe, stale
            new ProcessResult(0, "0.147.0", string.Empty)); // 2: re-probe under lock, already current

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

        // Regression: nothing re-read local state after the lease was granted, so a queued
        // process reran the vendor updater against an executable another process already updated.
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.147.0", status.Version);
        Assert.False(status.UpdateAvailable);
        Assert.Equal(2, runner.CallCount); // no install/update process ever ran
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateSkipsRepairWhenAnotherProcessAlreadyRepairedItWhileWaitingOnTheLock()
    {
        FakeClock clock = new();
        FakeReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));
        FakeInstallLock installLock = new();
        SequencedProcessRunner runner = new(
            new ProcessResult(1, string.Empty, "broken"), // 1: initial local probe — broken install
            new ProcessResult(0, "0.146.0", string.Empty)); // 2: re-probe under lock — already repaired

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

        // Regression: the post-lock re-probe used to be gated on "this is an update", so a
        // concurrent install/repair of a missing-or-broken provider was never detected and the
        // installer reran redundantly.
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
        Assert.Equal(0, releaseSource.CallCount); // repair skips the release comparison entirely
        Assert.Equal(2, runner.CallCount); // no install process ever ran
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRepairsACorruptInstallWithTheFullInstallerNotTheUpdateCommand()
    {
        FakeClock clock = new();
        FakeReleaseCache cache = new();
        CountingReleaseSource releaseSource = new(new(true, new Version(0, 149, 0)));
        FakeInstallLock installLock = new();
        List<ProcessRequest> requests = [];
        int calls = 0;
        RecordingProcessRunner runner = new(request =>
        {
            requests.Add(request);
            calls++;
            // 1: initial probe, 2: re-probe under lock — both see the same broken install.
            // 3: the installer runs. 4: the post-install recheck reports the repaired version.
            return calls <= 2
                ? new ProcessResult(1, string.Empty, "broken")
                : calls == 3
                    ? new ProcessResult(0, string.Empty, string.Empty)
                    : new ProcessResult(0, "1.0.0", string.Empty);
        });

        ProviderStatus status = await ProviderInstallation.InstallOrUpdateAsync(
            Id,
            Spec(updateArguments: ["update"]), // the executable exists but --version fails
            runner,
            releaseSource,
            cache,
            installLock,
            clock,
            ProviderInstallation.DefaultVersionProbeTimeout,
            ProviderInstallation.DefaultInstallTimeout,
            ProviderInstallation.DefaultInstallLockTimeout,
            TestContext.Current.CancellationToken);

        // Regression: deciding the update-vs-install branch from File.Exists alone "repaired" a
        // corrupt install by rerunning the same failing `update` command instead of the installer.
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("install.exe", requests[2].FileName);
        Assert.DoesNotContain(requests, request => request.Arguments is ["update"]);
    }

    private static ProviderInstallSpec Spec(bool installed = true, IReadOnlyList<string>? updateArguments = null)
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

        return new(executablePath, InstallExecutable: "install.exe", InstallArguments: [], updateArguments, null);
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

        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            int position = Math.Min(index, responses.Length - 1);
            index++;
            return Task.FromResult(responses[position]);
        }
    }

    private sealed class RecordingProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
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
}
