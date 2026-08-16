namespace Forge.Host.Client;

/// <summary>
/// The cross-process, current-user mutual-exclusion lease a Host acquires before mutating a project's
/// <c>.forge/</c> tree. Held for the Host process's lifetime; releasing it (including on process death) lets a
/// successor acquire it.
/// </summary>
public interface IProjectLease : IDisposable
{
    /// <summary>
    /// True when this lease was acquired via <see cref="AbandonedMutexException"/> recovery: the previous owner
    /// died without releasing it. ADR 0005 treats this as ownership plus a mandatory durable-state recovery
    /// signal, never as clean state.
    /// </summary>
    bool WasAbandoned { get; }
}

/// <summary>
/// The one platform-neutral <see cref="IProjectLease"/> implementation: a <see cref="Mutex"/> whose short, hashed
/// name uses <see cref="NamedWaitHandleOptions.CurrentUserOnly"/>. Release, Debug, and test instances of the same
/// project share this lease namespace, so a second host reading the same project never becomes a concurrent
/// writer; it must call <see cref="TryAcquire"/> and treat a null result as <c>project_in_use</c>.
/// </summary>
/// <remarks>
/// Uses <see cref="NamedWaitHandleOptions.CurrentSessionOnly"/> = <see langword="true"/> (the BCL default),
/// scoping the mutex to the user's own logon session rather than the OS-wide <c>Global\</c> namespace: creating a
/// <c>Global\</c> named object on Windows requires <c>SeCreateGlobalPrivilege</c>, which a standard (non-admin)
/// user does not hold by default — a same-user CI check caught this concretely (a fresh standard local user's
/// <see cref="Mutex"/> construction threw <see cref="UnauthorizedAccessException"/>). Session-scoping still
/// protects every process within one logon session (the case Forge actually runs in); it does not extend across
/// two concurrent sessions of the same user (e.g. console + a simultaneous RDP session), an edge case judged
/// acceptable against making the lease unusable for every non-admin user.
/// </remarks>
/// <remarks>
/// A named <see cref="Mutex"/>'s ownership is tracked per OS thread, not per <see cref="Mutex"/> object or async
/// flow — an <c>await</c> between acquiring and releasing it can resume on a different thread-pool thread and
/// make <see cref="Mutex.ReleaseMutex"/> throw. This type owns a dedicated background thread for the mutex's
/// entire held lifetime so the acquire and release calls always run on the same thread, and exposes only the
/// thread-agnostic <see cref="TryAcquire"/>/<see cref="Dispose"/> surface to callers.
/// </remarks>
public sealed class MutexProjectLease : IProjectLease
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim releaseSignal;
    private int disposed;

    private MutexProjectLease(Thread thread, ManualResetEventSlim releaseSignal, bool wasAbandoned)
    {
        this.thread = thread;
        this.releaseSignal = releaseSignal;
        WasAbandoned = wasAbandoned;
    }

    public bool WasAbandoned { get; }

    /// <summary>Attempts to acquire the lease within <paramref name="timeout"/>. Returns null if another owner holds it.</summary>
    public static MutexProjectLease? TryAcquire(string leaseName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);
        using ManualResetEventSlim acquireSignal = new(false);
        ManualResetEventSlim releaseSignal = new(false);
        bool acquired = false;
        bool wasAbandoned = false;
        Exception? failure = null;

        Thread thread = new(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(
                    initiallyOwned: false,
                    leaseName,
                    new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true },
                    out _);
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    wasAbandoned = true;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // Must fire the instant the acquire outcome (or a construction failure) is known — never
                // deferred until the lease is later released, or the calling thread's acquireSignal.Wait()
                // below deadlocks forever waiting for a Dispose() call it cannot make until TryAcquire returns.
                acquireSignal.Set();
            }

            if (failure is not null || !acquired || mutex is null)
            {
                mutex?.Dispose();
                return;
            }

            try
            {
                releaseSignal.Wait();
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "forge-project-lease",
        };
        thread.Start();
        acquireSignal.Wait();

        if (failure is not null)
        {
            thread.Join();
            releaseSignal.Dispose();
            throw new InvalidOperationException($"Could not acquire the project lease '{leaseName}'.", failure);
        }

        if (!acquired)
        {
            thread.Join();
            releaseSignal.Dispose();
            return null;
        }

        return new MutexProjectLease(thread, releaseSignal, wasAbandoned);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        releaseSignal.Set();
        thread.Join();
        releaseSignal.Dispose();
    }
}
