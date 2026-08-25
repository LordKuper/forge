using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Forge.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Runtime.Posix;

/// <summary>
/// Round 2 review's most important finding: <see cref="Attach"/>'s `setpgid(childPid, 0)` call is
/// currently a NO-OP in practice, not merely "best-effort" -- read this fully before relying on it
/// for anything.
///
/// <list type="bullet">
/// <item><description><b>Why it does not take effect.</b> POSIX specifies `setpgid` fails with
/// `EACCES` once the target child has already called one of the `exec` functions. `Attach` only runs
/// after <c>Process.Start</c> has already returned control to the caller. On Unix, .NET's own
/// process-launch implementation (`SystemNative_ForkAndExecProcess` in `pal_process.c`) creates a
/// `close-on-exec` pipe across the fork specifically so the PARENT can block, inside that native
/// call, until the child has already reached `execve` (successfully or not) -- this is how .NET turns
/// a failed exec into a proper managed exception instead of returning a "successfully started" handle
/// for a process that never ran. That synchronization is unconditional: it is not a rare, sub-
/// millisecond race the child usually loses (contrast
/// <c>Forge.Runtime.Windows.WindowsJobObjectProcessContainment</c>'s own, genuinely narrow race
/// window) -- by the time `Process.Start` returns at all, the child has, by construction, already
/// exec'd. `setpgid(childPid, 0)` called afterward is therefore expected to return `EACCES` every
/// time, on every currently supported .NET version, not merely "almost certainly" as first
/// suspected.</description></item>
/// <item><description><b>What would actually work.</b> The group change must happen INSIDE the child,
/// between `fork` and `exec` (a self `setpgid(0, 0)`, or Linux's `PR_SET_PDEATHSIG` for a closer
/// approximation of the Windows guarantee -- see the historical discussion this doc comment used to
/// carry). `System.Diagnostics.Process.Start` exposes no hook to run code in that window; reaching it
/// would mean bypassing `Process.Start` entirely with a bespoke fork/exec/`posix_spawn` wrapper, which
/// duplicates its stdio-redirection plumbing and is a materially larger change than this port
/// justifies. Not attempted here.</description></item>
/// <item><description><b>Why this type still exists.</b> The call is harmless (fails closed, never
/// throws out of <see cref="Attach"/>) and now observable (logged once -- see below) rather than
/// silently assumed to work, so it is left in place as inert scaffolding for a future fork/exec-hook
/// implementation rather than removed outright. It must NOT be read as delivering any containment
/// today: on Linux and macOS, a spawned process is NOT currently placed in an isolated process group
/// by this adapter, and an abrupt Host crash leaves it (and its descendants) running as an orphan
/// exactly as if <c>NullProcessContainment</c> were installed instead.</description></item>
/// </list>
/// </summary>
public sealed partial class PosixProcessGroupContainment : IProcessContainment
{
    // Round 2 review: a fail-open path that never says anything is indistinguishable from success.
    // Both logged once per instance (not per spawn) -- on a machine where either condition holds it
    // holds for every subsequent spawn too, so repeating the warning would only be noise.
    private static readonly Action<ILogger, int, string, Exception?> LogSetpgidFailed =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(2080, "ProcessContainmentSetpgidFailed"),
            "setpgid(pid={ProcessId}, 0) failed ({Reason}); this process (and, until libc/.NET's Unix process-launch " +
            "behavior changes, every further process this Forge Host instance spawns) is NOT placed in its own " +
            "process group, despite CHANGELOG.md's Security note -- see PosixProcessGroupContainment's own doc " +
            "comment for why this is expected on every currently supported .NET version.");

    private static readonly Action<ILogger, Exception> LogLibcUnavailable = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2081, "ProcessContainmentLibcUnavailable"),
        "Could not resolve libc's `setpgid` symbol on this platform; process-group containment is unavailable for " +
        "this Forge Host instance.");

    private readonly ILogger<PosixProcessGroupContainment> logger;
    private int failureLogged;

    public PosixProcessGroupContainment(ILogger<PosixProcessGroupContainment>? logger = null) =>
        this.logger = logger ?? NullLogger<PosixProcessGroupContainment>.Instance;

    // A static constructor (not [ModuleInitializer], which CA2255 reserves for application code, not
    // a library like this one) runs once, automatically, before this type's first use -- which is
    // always before Attach's own `setpgid` P/Invoke call, since that call only happens from an
    // instance method and constructing an instance already runs this first.
    static PosixProcessGroupContainment() =>
        NativeLibrary.SetDllImportResolver(typeof(PosixProcessGroupContainment).Assembly, ResolveLibc);

    public IDisposable Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Best-effort, never fails the spawn: a failure here (the child already exited in the gap
        // between Start() and this call, this platform's libc does not expose `setpgid` under this
        // exact symbol, or -- expected, see the type doc comment -- the child has already exec'd)
        // must never break the process spawn that already worked before this adapter existed, the
        // same fail-open discipline WindowsJobObjectProcessContainment.Attach uses. Round 2 review:
        // the outcome must still be observable rather than silently assumed, so both the return value
        // and the two exception paths are now logged (once) instead of discarded.
        try
        {
            if (setpgid(process.Id, 0) != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                LogFailureOnce(() => LogSetpgidFailed(logger, process.Id, Marshal.GetPInvokeErrorMessage(errno), null));
            }
        }
        catch (DllNotFoundException exception)
        {
            LogFailureOnce(() => LogLibcUnavailable(logger, exception));
        }
        catch (EntryPointNotFoundException exception)
        {
            LogFailureOnce(() => LogLibcUnavailable(logger, exception));
        }
        catch (InvalidOperationException)
        {
            // process.Id itself throws this when the process has already exited and been disposed
            // between Start() and this call (matches the same defensive fail-open widening applied to
            // WindowsJobObjectProcessContainment.Attach) -- nothing to group in that case.
        }

        return NoopHandle.Instance;
    }

    private void LogFailureOnce(Action log)
    {
        if (Interlocked.CompareExchange(ref failureLogged, 1, 0) == 0)
        {
            log();
        }
    }

    // glibc's actual loadable SONAME is `libc.so.6`; plain `libc.so` (what the default DllImport
    // probing for the bare name "libc" tries) is a linker SCRIPT shipped only in the -dev/-devel
    // package and is not dlopen-able -- absent entirely from runtime-only images (the
    // mcr.microsoft.com/dotnet/runtime family, most slim containers). musl (Alpine) ships neither
    // name; its SONAME is `libc.musl-<arch>.so.1`. macOS is deliberately left alone here: it already
    // resolves the bare "libc" name via its libSystem shared-cache mapping without this hook.
    private static nint ResolveLibc(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "libc", StringComparison.Ordinal) || !OperatingSystem.IsLinux())
        {
            return nint.Zero;
        }

        foreach (string candidate in LinuxLibcCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out nint handle))
            {
                return handle;
            }
        }

        // Zero tells the runtime to fall through to its own default probing (which is what already
        // ran, and failed, before this resolver existed) rather than asserting failure itself --
        // preserves the exact DllNotFoundException path Attach's catch clause already handles.
        return nint.Zero;
    }

    private static IEnumerable<string> LinuxLibcCandidates()
    {
        yield return "libc.so.6";
        string? muslArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.Arm => "armhf",
            Architecture.X86 => "i386",
            _ => null,
        };
        if (muslArch is not null)
        {
            yield return $"libc.musl-{muslArch}.so.1";
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setpgid(int pid, int pgid);

    private sealed class NoopHandle : IDisposable
    {
        // No OS resource to release -- unlike CreateJobObject's job handle, `setpgid` leaves nothing
        // behind to close. This exists only so Attach's return type matches the port.
        public static readonly NoopHandle Instance = new();

        public void Dispose()
        {
        }
    }
}
