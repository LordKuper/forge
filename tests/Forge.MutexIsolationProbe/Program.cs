using Forge.Host.Client;

// Exercises the exact production IProjectLease (MutexProjectLease, NamedWaitHandleOptions with
// CurrentUserOnly = true) that ControlPlaneHostedService/ProviderInstallLock use, so a same-user
// isolation test proves the real primitive is namespaced per user — not a hand-rolled stand-in.
//
// Usage: Forge.MutexIsolationProbe acquire <leaseName> <holdSeconds> <timeoutSeconds>
// Acquires the named lease within <timeoutSeconds>. On success, prints "acquired" (flushed
// immediately so a caller script can observe it before the hold ends), holds it for
// <holdSeconds>, then releases and exits 0. On failure to acquire within the timeout, prints
// "timeout" and exits 1.
if (args.Length != 4 ||
    args[0] != "acquire" ||
    !double.TryParse(args[2], out double holdSeconds) ||
    !double.TryParse(args[3], out double timeoutSeconds))
{
    Console.Error.WriteLine("Usage: Forge.MutexIsolationProbe acquire <leaseName> <holdSeconds> <timeoutSeconds>");
    return 64;
}

string leaseName = args[1];

try
{
    using MutexProjectLease? lease = MutexProjectLease.TryAcquire(leaseName, TimeSpan.FromSeconds(timeoutSeconds));
    if (lease is null)
    {
        Console.WriteLine("timeout");
        return 1;
    }

    Console.WriteLine("acquired");
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(holdSeconds));
    return 0;
}
catch (Exception exception)
{
    // Any other failure is reported by type, not swallowed, so a caller script can tell a real
    // primitive error apart from the expected "timeout" outcome.
    Console.WriteLine(exception.GetType().Name);
    return 1;
}
