using Forge.Providers;

namespace Forge.UnitTests;

public sealed class ProviderInstallLockTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASecondAcquireBlocksUntilTheFirstIsReleased()
    {
        ProviderInstallLock @lock = new(UniqueLockName());
        IProviderInstallLease? first =
            await @lock.TryAcquireAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        // The same process-wide named lock — a second acquire attempt with a short timeout must
        // time out while the first lease is still held.
        IProviderInstallLease? second =
            await @lock.TryAcquireAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.Null(second);

        await first!.DisposeAsync();
        await using IProviderInstallLease? third =
            await @lock.TryAcquireAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(third);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisposingTwiceIsSafe()
    {
        ProviderInstallLock @lock = new(UniqueLockName());
        IProviderInstallLease? lease =
            await @lock.TryAcquireAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        await lease!.DisposeAsync();
        await lease.DisposeAsync();
    }

    // A unique name per test keeps these tests from contending with the real production lock
    // (or each other) — see ProviderInstallLock.DefaultLockName's remarks.
    private static string UniqueLockName() => $"forge-provider-install-lock-test-{Guid.NewGuid():N}";
}
