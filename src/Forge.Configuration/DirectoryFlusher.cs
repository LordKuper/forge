using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Forge.Configuration;

internal static partial class DirectoryFlusher
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FlushWindows(directory);
            return;
        }

        using SafeFileHandle handle = File.OpenHandle(
            directory,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.FlushToDisk(handle);
    }

    private static void FlushWindows(string directory)
    {
        using SafeFileHandle handle = CreateFile(
            directory,
            GenericWrite,
            ShareReadWriteDelete,
            0,
            OpenExisting,
            BackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to open the configuration directory for flushing.");
        }

        if (!FlushFileBuffers(handle))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to flush the configuration directory.");
        }
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle file);
}
