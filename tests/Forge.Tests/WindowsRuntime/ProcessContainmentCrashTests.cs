using System.Diagnostics;

namespace Forge.WindowsRuntimeTests;

/// <summary>
/// Plan section 12.4's crash-simulation gap: proves an abrupt kill of the SPAWNING process does not
/// orphan the child it contained, not merely that a cancellation-driven, still-alive-parent kill
/// does (already covered by <c>Forge.IntegrationTests.ProcessRunnerTests</c>). No in-process xunit
/// test can observe this about itself -- the test host is the very process that would need to die
/// -- so this spawns a genuine, separate harness process
/// (<see href="../../../Forge.ProcessContainmentProbe">Forge.ProcessContainmentProbe</see>) that
/// itself spawns a child (through the real production <c>ProcessRunner</c> +
/// <c>WindowsJobObjectProcessContainment</c> path), which in turn spawns its own grandchild through
/// a plain, uncontained <c>ProcessRunner</c> (no <c>Attach</c> call of its own) -- so the grandchild's
/// only route into any job is the OS's automatic job-membership inheritance from its parent, not a
/// second, independent containment layer. This then kills the harness ungracefully
/// (<see cref="Process.Kill()"/>, no process tree) and confirms neither the child NOR the grandchild
/// survives as an orphan: proof that inheritance itself works, not merely that a directly assigned
/// process dies -- the property this containment's real-world usefulness depends on, since a
/// contained provider process routinely spawns its own helpers with no containment of their own.
/// </summary>
[Collection("External process tests")]
public sealed class ProcessContainmentCrashTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnAbruptHostProcessKillLeavesNoOrphanedProviderProcess()
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
        string grandchildPidPath = Path.Combine(directory, "grandchild.pid");
        Process? harness = null;
        Process? child = null;
        Process? grandchild = null;
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
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited, "The contained child should be running before the crash.");

            // The child waits for the harness's explicit post-Attach marker before spawning this
            // process, so this is the ordinary inheritance case outside the accepted race window.
            int grandchildPid = await ReadPidOnceWrittenAsync(grandchildPidPath);
            grandchild = Process.GetProcessById(grandchildPid);
            Assert.False(grandchild.HasExited, "The contained grandchild should be running before the crash.");

            // Not Kill(true): an ungraceful kill of ONLY the harness itself, exactly simulating an
            // abrupt Forge Host crash that never runs any of its own graceful-shutdown/tree-kill
            // code. Whatever keeps the child and grandchild from surviving this must come from the
            // OS-level Job Object containment alone, not from any cooperation by the process being
            // killed.
            harness.Kill();
            Assert.True(harness.WaitForExit((int)TimeSpan.FromSeconds(15).TotalMilliseconds));

            await AssertProcessDiesAsync(child);
            await AssertProcessDiesAsync(grandchild);
        }
        finally
        {
            try
            {
                // The harness blocks forever (Timeout.InfiniteTimeSpan) until killed: if an earlier
                // assertion above threw before the deliberate harness.Kill() ran, Dispose() alone
                // would release only this test's own handle to it and leave the OS process itself
                // running indefinitely as a leak -- exactly the immortal leaked harness processes a
                // prior review found on this machine. Kill it here too (idempotent: a no-op once
                // already exited) before disposing.
                if (harness is { HasExited: false })
                {
                    harness.Kill();
                }

                harness?.Dispose();
                TryKill(child);
                TryKill(grandchild);
                child?.Dispose();
                grandchild?.Dispose();
                await DeleteDirectoryAsync(directory);
            }
            catch (Exception error)
            {
                // Cleanup is best-effort only: a failure here (e.g. a still-locked temp directory
                // because an earlier kill above did not fully take effect) must never replace
                // whatever exception -- typically the real assertion failure -- the try block above
                // was already propagating out of this finally. Log it so it stays visible, but let
                // the original exception win.
                Console.Error.WriteLine($"[ProcessContainmentCrashTests] Cleanup failed for '{directory}': {error}");
            }
        }
    }

    private static async Task<int> ReadPidOnceWrittenAsync(string path)
    {
        string? observed = null;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    observed = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                    if (int.TryParse(
                        observed.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int processId))
                    {
                        return processId;
                    }
                }
            }
            catch (IOException)
            {
                // The writer or a scanner may still hold the newly published file.
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"'{path}' did not contain a complete process id within 10 seconds (last value: '{observed}').");
        return 0;
    }

    private static async Task AssertProcessDiesAsync(Process process)
    {
        for (int attempt = 0; attempt < 200 && !process.HasExited; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(process.HasExited, $"Process {process.Id} should no longer be running.");
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception error) when (
            error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup must not replace the real assertion failure.
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
