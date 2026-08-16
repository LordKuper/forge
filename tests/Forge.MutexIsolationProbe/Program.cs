using Forge.Host.Client;

// Exercises the exact production IProjectLease (MutexProjectLease, NamedWaitHandleOptions with
// CurrentUserOnly = true) that ControlPlaneHostedService/ProviderInstallLock use, so a same-user
// isolation result is about the primitive Forge actually ships, not a hand-rolled stand-in.
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
    // Any other failure is reported by the full type/message chain, not just the outer wrapper
    // type — MutexProjectLease.TryAcquire wraps every construction failure in the same
    // InvalidOperationException, so printing only GetType().Name erases the actual cause (e.g. an
    // access-denial reason) a caller script needs to diagnose or distinguish from "timeout".
    Console.WriteLine(string.Join(" | ", ExceptionChain(exception).Select(item => $"{item.GetType().Name}: {item.Message}")));
    return 1;
}

static IEnumerable<Exception> ExceptionChain(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        yield return current;
    }
}
