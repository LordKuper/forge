namespace Forge.Providers;

/// <summary>
/// The one platform-neutral <see cref="IProviderInstallLock"/> implementation: a named
/// <see cref="Mutex"/> using <see cref="NamedWaitHandleOptions.CurrentUserOnly"/> — the same
/// portable primitive <c>Forge.Host.Client.MutexProjectLease</c> uses for the project lease,
/// including its capability-probed Global\-namespace-when-possible construction (see
/// <c>MutexProjectLease</c>'s own remarks for why), so this works on every OS a future provider
/// adapter targets without an OS-specific adapter of its own (ADR 0007/0008: locking policy is
/// generic, only vendor specifics are adapter-owned).
/// </summary>
/// <remarks>
/// A named <see cref="Mutex"/>'s ownership is tracked per OS thread, not per <see cref="Mutex"/>
/// object or async flow — an <c>await</c> between acquiring and releasing it can resume on a
/// different thread-pool thread and make <see cref="Mutex.ReleaseMutex"/> throw. This type owns a
/// dedicated background thread for the mutex's entire held lifetime so the acquire and release
/// calls always run on the same thread (matching <c>MutexProjectLease</c>'s own remarks), and the
/// blocking wait itself runs on a pool thread via <see cref="Task.Run(Action)"/> so the caller's
/// own thread is never pinned for the wait.
/// </remarks>
public sealed class ProviderInstallLock(string lockName = ProviderInstallLock.DefaultLockName) : IProviderInstallLock
{
    /// <summary>The production per-user lock name — one lock shared by every Forge process for a
    /// given user (the vendor executables it protects are a shared per-user resource), except on
    /// the rare account that falls back to session-scoping (see the type-level remarks) — there it
    /// is shared only within one session, so two concurrent installs from different sessions of the
    /// same account are not mutually excluded. ADR 0002's install idempotency covers crash-and-retry,
    /// not that concurrent-writer case. Tests should pass a unique name instead so they never
    /// contend with a real install.</summary>
    public const string DefaultLockName = "forge-provider-install-lock";

    /// <remarks>
    /// <paramref name="cancellationToken"/> only guards the moment before the wait starts (it
    /// short-circuits <see cref="Task.Run(Action, CancellationToken)"/> if already canceled).
    /// Once the dedicated thread is inside <c>Mutex.WaitOne(TimeSpan)</c>, cancellation is
    /// not observed — that native wait is not cancelable — so a cancellation requested mid-wait
    /// only takes effect when <paramref name="timeout"/> itself elapses.
    /// </remarks>
    public async Task<IProviderInstallLease?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => TryAcquireCore(lockName, timeout), cancellationToken).ConfigureAwait(false);
    }

    private static Lease? TryAcquireCore(string lockName, TimeSpan timeout)
    {
        using ManualResetEventSlim acquireSignal = new(false);
        ManualResetEventSlim releaseSignal = new(false);
        bool acquired = false;
        Exception? failure = null;

        Thread thread = new(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = CreateMutex(lockName);
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    // A prior owner crashed mid-install. The vendor's own install/update mechanism
                    // is idempotent by design (ADR 0002), so proceeding here is safe.
                    acquired = true;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // Must fire the instant the acquire outcome is known — see MutexProjectLease's
                // identical remark on why this cannot be deferred to release.
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
            Name = "forge-provider-install-lock",
        };
        thread.Start();
        acquireSignal.Wait();

        if (failure is not null)
        {
            thread.Join();
            releaseSignal.Dispose();
            throw new InvalidOperationException("Could not acquire the provider install lock.", failure);
        }

        if (!acquired)
        {
            thread.Join();
            releaseSignal.Dispose();
            return null;
        }

        return new Lease(thread, releaseSignal);
    }

    /// <summary>Determines once, from a dedicated always-uncontended probe name (a fresh
    /// <see cref="Guid"/> every time this runs, so it can never collide with a real lock and never
    /// observes contention), whether this process's account can create <c>Global\</c> named objects
    /// at all. See <c>Forge.Host.Client.MutexProjectLease</c>'s type-level remarks for why the real
    /// lock name must never make this decision itself.</summary>
    private static readonly Lazy<bool> CanCreateGlobalMutexes = new(() =>
    {
        try
        {
            using Mutex probe = new(
                initiallyOwned: false,
                $"forge-global-mutex-capability-probe-{Guid.NewGuid():N}",
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false },
                out _);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    });

    /// <summary>See <c>Forge.Host.Client.MutexProjectLease</c>'s type-level remarks: uses the
    /// OS-wide <c>Global\</c> namespace when this process's account can create one (determined once
    /// via <see cref="CanCreateGlobalMutexes"/>), session-scoping otherwise.</summary>
    private static Mutex CreateMutex(string lockName) =>
        new(
            initiallyOwned: false,
            lockName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = !CanCreateGlobalMutexes.Value },
            out _);

    private sealed class Lease(Thread thread, ManualResetEventSlim releaseSignal) : IProviderInstallLease
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                releaseSignal.Set();
                thread.Join();
                releaseSignal.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
