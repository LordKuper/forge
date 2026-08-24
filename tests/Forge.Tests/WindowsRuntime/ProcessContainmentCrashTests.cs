using System.Diagnostics;

namespace Forge.WindowsRuntimeTests;

/// <summary>
/// Plan section 12.4's crash-simulation gap: proves an abrupt kill of the SPAWNING process does not
/// orphan the child it contained, not merely that a cancellation-driven, still-alive-parent kill
/// does (already covered by <c>Forge.IntegrationTests.ProcessRunnerTests</c>). No in-process xunit
/// test can observe this about itself -- the test host is the very process that would need to die
/// -- so this spawns a genuine, separate harness process
/// (<see href="../../../Forge.ProcessContainmentProbe">Forge.ProcessContainmentProbe</see>) that
/// itself spawns a grandchild through the real production
/// <c>ProcessRunner</c> + <c>WindowsJobObjectProcessContainment</c> path, then kills that harness
/// ungracefully (<see cref="Process.Kill()"/>, no process tree) and confirms the grandchild does not
/// survive as an orphan.
/// </summary>
[Collection("External process tests")]
public sealed class ProcessContainmentCrashTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnAbruptKillOfTheSpawningProcessDoesNotOrphanItsContainedChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string probePath = Path.Combine(AppContext.BaseDirectory, "Forge.ProcessContainmentProbe.exe");
        Assert.True(File.Exists(probePath), $"'{probePath}' must ship next to the test binaries.");

        string directory = Path.Combine(Path.GetTempPath(), $"forge-containment-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string childPidPath = Path.Combine(directory, "child.pid");
        Process? harness = null;
        try
        {
            harness = Process.Start(new ProcessStartInfo(probePath)
            {
                ArgumentList = { "harness", directory, "60" },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(harness);

            int childPid = await ReadPidOnceWrittenAsync(childPidPath);
            Assert.True(IsProcessAlive(childPid), "The contained grandchild should be running before the crash.");

            // Not Kill(true): an ungraceful kill of ONLY the harness itself, exactly simulating an
            // abrupt Forge Host crash that never runs any of its own graceful-shutdown/tree-kill
            // code. Whatever keeps the grandchild from surviving this must come from the OS-level
            // Job Object containment alone, not from any cooperation by the process being killed.
            harness.Kill();
            Assert.True(harness.WaitForExit((int)TimeSpan.FromSeconds(15).TotalMilliseconds));

            await AssertProcessDiesAsync(childPid);
        }
        finally
        {
            harness?.Dispose();
            TryKillByPidFile(childPidPath);
            await DeleteDirectoryAsync(directory);
        }
    }

    private static async Task<int> ReadPidOnceWrittenAsync(string path)
    {
        // 200 * 50ms = 10s: generous enough for the harness/child process pair to actually get
        // scheduled and start under a loaded machine, not just an idle one.
        for (int attempt = 0; attempt < 200 && !File.Exists(path); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"'{path}' was never created -- the harness never reported a live child.");
        string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        return int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task AssertProcessDiesAsync(int processId)
    {
        // 200 * 50ms = 10s, matching ReadPidOnceWrittenAsync's own budget.
        for (int attempt = 0; attempt < 200 && IsProcessAlive(processId); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(IsProcessAlive(processId), $"Process {processId} should no longer be running.");
    }

    /// <summary>Best-effort cleanup only, for a failed assertion above that leaves the grandchild
    /// running: never itself part of what the test proves.</summary>
    private static void TryKillByPidFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            int pid = int.Parse(File.ReadAllText(path).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            // Best-effort only (see the doc comment above): a transient share violation reading the
            // pid file (e.g. a security scanner momentarily holding it) must never replace whatever
            // exception the try block above was already propagating -- a throw here would silently
            // swap the real test failure for this unrelated cleanup failure.
        }
    }

    private static async Task DeleteDirectoryAsync(string directory)
    {
        for (int attempt = 0; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}
