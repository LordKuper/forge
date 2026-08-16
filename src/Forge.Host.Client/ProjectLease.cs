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
/// Uses <see cref="NamedWaitHandleOptions.CurrentSessionOnly"/> = <see langword="true"/> (session-scoped) on
/// Windows and <see langword="false"/> (the OS-wide <c>Global\</c> namespace — the strongest guarantee, covering
/// every session of the user) everywhere else. This is a fixed platform choice, reported once via
/// <see cref="OperatingSystem.IsWindows"/> to select between two BCL-portable construction options — not an
/// OS-specific behavior branch (ADR 0007). Two designs were tried and rejected before this one:
/// <list type="bullet">
/// <item>Always <see langword="false"/> everywhere: creating a <c>Global\</c> named object on Windows requires
/// <c>SeCreateGlobalPrivilege</c>, which a standard (non-admin) user does not hold by default — a same-user CI
/// check caught this concretely, as a hard startup failure for that account. Unix has no equivalent privilege gate
/// for its own namespace, so this was never a problem there.</item>
/// <item>A per-process capability probe (try <c>Global\</c>, fall back to session-scoped only if that account
/// cannot create one): this does not test the <em>account</em>, it tests the <em>process token</em> —
/// <c>SeCreateGlobalPrivilege</c> is present only on an elevated UAC token, not the same admin user's ordinary
/// filtered token. Two processes of the identical user (one elevated, one not) would then resolve to two different,
/// non-communicating mutex objects and never exclude each other — a silent, fail-open loss of the lease's entire
/// purpose, for an everyday scenario (a Windows admin sometimes running a terminal elevated, sometimes not), not a
/// rare edge case.</item>
/// <item>Uniform session-scoping everywhere (dropping <c>Global\</c> entirely): fixes both problems above, but
/// unnecessarily narrows Unix too, where <c>CurrentSessionOnly = false</c> never had a privilege problem to begin
/// with — a session there is only a single shell, so two terminal windows of the same user would stop excluding
/// each other for no reason tied to any actual OS constraint.</item>
/// </list>
/// The chosen, Windows-only session-scoping still protects every process within one Windows logon session — it
/// does not extend across two concurrent Windows sessions of the same user (e.g. console + a simultaneous RDP
/// session), an edge case judged acceptable against every non-admin Windows account failing outright or silently
/// losing exclusion across elevation levels.
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
                mutex = CreateMutex(leaseName);
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

    /// <summary>See the type-level remarks for why this is a fixed platform choice rather than a
    /// per-process <c>Global\</c> capability check or uniform session-scoping.</summary>
    private static Mutex CreateMutex(string leaseName) =>
        new(
            initiallyOwned: false,
            leaseName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = OperatingSystem.IsWindows() },
            out _);

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
