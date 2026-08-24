using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forge.Application;
using Forge.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace Forge.Runtime.Windows;

/// <summary>
/// Real containment: assigns each spawned child to its own Windows Job Object configured with
/// `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so the OS itself terminates the child -- and every
/// descendant it spawns, since a process created by a job member is itself automatically a member
/// of the same job unless it explicitly opts out -- the instant the job's last handle closes. That
/// handle is exactly what <see cref="Attach"/> returns: when the current (Forge Host) process is
/// torn down for any reason, including an abrupt `taskkill /F` or a crash, the OS closes every
/// handle that process held, unconditionally, which fires the same kill. No cooperation from the
/// dying process is required at that moment -- this is what makes it a real guarantee rather than a
/// best-effort one.
///
/// Residual race window, named honestly rather than hidden: <see cref="Attach"/> can only run after
/// <c>Process.Start</c> has already returned control to the caller
/// (<see cref="Forge.Infrastructure.ProcessRunner.RunAsync"/> calls it immediately afterward, with
/// no `await` in between), so a child that spawns and exits its own grandchild inside that
/// (realistically sub-millisecond, but not zero) gap could leave that grandchild outside the job
/// before assignment lands. Fully closing this window needs `CREATE_SUSPENDED` plus an explicit
/// `ResumeThread` once assignment succeeds, which `System.Diagnostics.Process` does not expose --
/// using it would mean bypassing `Process.Start`'s shared stdio-redirection plumbing entirely, a
/// materially larger change than this port justifies. Left as a known, narrow gap rather than
/// attempted half-way.
/// </summary>
public sealed partial class WindowsJobObjectProcessContainment : IProcessContainment
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    public IDisposable Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Fail open, never fail the spawn: ProcessRunner.RunAsync calls this for every process it
        // starts, including ordinary `git` plumbing that worked before this adapter existed. Some
        // environments restrict job-object nesting or breakaway (a Forge Host itself already
        // confined to a restrictive job, as some CI/sandbox hosts do) -- a Job Object failure there
        // must degrade to "no containment for this one process," never regress the process spawn
        // itself, which owes nothing to this adapter's success.
        try
        {
            return AttachCore(process);
        }
        catch (Win32Exception)
        {
            return new NullProcessContainment().Attach(process);
        }
    }

    private static SafeJobObjectHandle AttachCore(Process process)
    {
        SafeJobObjectHandle job = CreateJobObject(nint.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(), "Failed to create a job object for process containment.");
        }

        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "Failed to configure kill-on-job-close for process containment.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (!AssignProcessToJobObject(job, process.SafeHandle))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(), "Failed to assign the process to its containment job object.");
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport(
        "kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobObjectHandle CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobObjectHandle job, int infoClass, nint jobObjectInfo, uint jobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobObjectHandle job, SafeProcessHandle process);
}

/// <summary>Closing this handle is the exact trigger <see cref="WindowsJobObjectProcessContainment"/>
/// relies on: `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` fires when the job's last handle closes, so
/// disposing (releasing) this handle both frees the OS resource and, if the OS process holding it
/// died without disposing it first, executes containment for whatever the job still owns at that
/// moment.</summary>
internal sealed partial class SafeJobObjectHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
