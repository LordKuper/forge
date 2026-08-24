using System.Diagnostics;
using System.Runtime.InteropServices;
using Forge.Application;

namespace Forge.Runtime.Posix;

/// <summary>
/// Best-effort containment for Linux and macOS via POSIX process groups. This is NOT the same
/// guarantee <c>Forge.Runtime.Windows.WindowsJobObjectProcessContainment</c> gives on Windows --
/// read this fully before assuming parity:
///
/// <list type="bullet">
/// <item><description><see cref="Attach"/> calls `setpgid(childPid, 0)` immediately after the child
/// starts, making it the leader of its own new process group (group id == its own pid). Any further
/// descendant it spawns inherits that same group id via `fork`, unless that descendant changes its
/// own group.</description></item>
/// <item><description>This does NOT kill the group when the current (Forge Host) process dies.
/// Unlike a Windows Job Object, a POSIX process group has no OS-level "kill on last owner exit"
/// mechanism -- group membership alone only lets a THIRD party that already knows the group id (an
/// init system, a process supervisor, systemd, a shell's own job control) issue
/// `kill(-pgid, SIGKILL)` to reap it. This change installs no such reaper. So on Linux/macOS, an
/// abrupt Host crash still leaves any live provider child (and its descendants) running as an
/// orphan, exactly as before this change -- this adapter only lays the groundwork (an isolated,
/// addressable group) for a future reaper to close that gap; it does not close the gap
/// itself.</description></item>
/// <item><description>Linux's `PR_SET_PDEATHSIG` could get closer to the Windows guarantee, by
/// asking the kernel to signal the CHILD itself when its parent dies -- but that call must run
/// inside the child, after `fork` and before `exec`. `System.Diagnostics.Process.Start` gives no
/// hook to run code there (it does not expose a fork/exec split); using it would mean bypassing
/// `Process.Start` entirely with a bespoke fork/exec/`posix_spawn` wrapper, a materially larger
/// change than this port justifies, and not attempted here. It also has no macOS equivalent, so it
/// could never be more than a Linux-only half of parity anyway.</description></item>
/// </list>
///
/// In short: Windows gets a real kill-on-host-death guarantee from this change; Linux/macOS get a
/// process-group primitive that is honest infrastructure for a future reaper, not a guarantee
/// against orphaned processes on its own.
/// </summary>
public sealed partial class PosixProcessGroupContainment : IProcessContainment
{
    public IDisposable Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Best-effort, never fails the spawn: a failure here (the child already exited in the gap
        // between Start() and this call, or this platform's libc does not expose `setpgid` under
        // this exact symbol) must never break the process spawn that already worked before this
        // adapter existed -- the same fail-open discipline
        // WindowsJobObjectProcessContainment.Attach uses, for the same reason. `setpgid` itself
        // reports failure only via `errno` (not an exception), so no return-value handling is
        // needed beyond letting the process continue ungrouped.
        try
        {
            _ = setpgid(process.Id, 0);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return NoopHandle.Instance;
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
