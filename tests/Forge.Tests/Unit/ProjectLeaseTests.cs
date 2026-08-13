using Forge.Host.Client;

namespace Forge.UnitTests;

public sealed class ProjectLeaseTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void SecondAcquireFailsWhileTheFirstHoldsTheLease()
    {
        // A named Mutex is reentrant per OS thread, not per Mutex object: acquiring it again from the same
        // thread that already owns it would trivially "succeed" and prove nothing. A real second owner is
        // always a different process (a different thread here is the closest in-process equivalent).
        string name = $"forge-test-lease-{Guid.NewGuid():N}";
        using MutexProjectLease? first = MutexProjectLease.TryAcquire(name, TimeSpan.FromSeconds(1));
        Assert.NotNull(first);
        Assert.False(first.WasAbandoned);

        MutexProjectLease? second = null;
        Thread contender = new(() => second = MutexProjectLease.TryAcquire(name, TimeSpan.FromMilliseconds(200)));
        contender.Start();
        contender.Join();

        Assert.Null(second);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReleasingLetsAnotherAcquireSucceed()
    {
        string name = $"forge-test-lease-{Guid.NewGuid():N}";
        using (MutexProjectLease? first = MutexProjectLease.TryAcquire(name, TimeSpan.FromSeconds(1)))
        {
            Assert.NotNull(first);
        }

        using MutexProjectLease? second = MutexProjectLease.TryAcquire(name, TimeSpan.FromSeconds(1));

        Assert.NotNull(second);
        Assert.False(second.WasAbandoned);
    }
}
