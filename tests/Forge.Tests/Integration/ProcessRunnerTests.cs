using System.Diagnostics;
using Forge.Application;
using Forge.Infrastructure;

namespace Forge.IntegrationTests;

[Collection("External process tests")]
public sealed class ProcessRunnerTests
{
    /// <summary>Proves the plan section 12.4 wiring itself (attach immediately after start, release
    /// only once the process is known to have exited on every code path), independent of any real
    /// OS guarantee -- the actual guarantee is proven separately by a real process-kill against
    /// Forge.ProcessContainmentProbe (Windows only: see
    /// tests/Forge.Tests/WindowsRuntime/ProcessContainmentCrashTests.cs; no equivalent containment
    /// exists on Linux/macOS, so there is nothing to exercise there either). A fake here keeps this
    /// test deterministic and cross-platform, matching every other test in this file.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ContainmentIsAttachedImmediatelyAfterStartAndReleasedAfterNormalExit()
    {
        RecordingContainment containment = new();
        ProcessRunner runner = new(containment);
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "exit 0" })
            : ("/bin/sh", new[] { "-c", "exit 0" });

        ProcessResult result = await runner.RunAsync(
            new(fileName, arguments, Path.GetTempPath()), null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, containment.AttachCount);
        Assert.True(containment.Disposed);
    }

    /// <summary>The cancellation-path counterpart: containment must still be released even when the
    /// process is torn down via <c>Process.Kill(true)</c> rather than exiting on its own -- the
    /// cancellation branch in <c>ProcessRunner.RunAsync</c> is a second, independent code path back
    /// to the method's single exit, and only manual inspection (not the normal-exit test above)
    /// would have caught a release that only ran on the happy path.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ContainmentIsReleasedAfterCancellationKillsTheProcess()
    {
        RecordingContainment containment = new();
        ProcessRunner runner = new(containment);
        using CancellationTokenSource cancellation = new();
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });

        Task<ProcessResult> run = runner.RunAsync(
            new(fileName, arguments, Path.GetTempPath()), null, cancellation.Token);
        for (int attempt = 0; attempt < 100 && containment.AttachCount == 0; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, containment.AttachCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(containment.Disposed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationTerminatesProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"forge-process-tests-{Guid.NewGuid():N}");
        string lockPath = Path.Combine(directory, "process.lock");
        string readyPath = Path.Combine(directory, "ready");
        Directory.CreateDirectory(directory);
        try
        {
            ProcessRunner runner = new();
            using CancellationTokenSource cancellation = new();
            string command =
                $"$stream=[IO.File]::Open('{lockPath}','OpenOrCreate','ReadWrite','None');" +
                $"[IO.File]::WriteAllText('{readyPath}','ready');" +
                "Start-Sleep -Seconds 30";
            Task<ProcessResult> run = runner.RunAsync(
                new(
                    "powershell.exe",
                    ["-NoProfile", "-Command", command],
                    directory),
                null,
                cancellation.Token);

            for (int attempt = 0; attempt < 100 && !File.Exists(readyPath); attempt++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(readyPath), "The child process did not signal readiness.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            await using (FileStream stream = new(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.True(stream.CanWrite);
            }
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreCancelledRequestDoesNotStartProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string marker = Path.Combine(
            Path.GetTempPath(),
            $"forge-process-marker-{Guid.NewGuid():N}");
        try
        {
            ProcessRunner runner = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runner.RunAsync(
                    new(
                        "powershell.exe",
                        [
                            "-NoProfile",
                            "-Command",
                            $"[IO.File]::WriteAllText('{marker}','started')",
                        ],
                        Path.GetTempPath()),
                    null,
                    cancellation.Token));

            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
    }

    /// <summary>Runs on every OS this repo targets (ADR 0007: neutral core, built and tested on
    /// Windows, Linux, and macOS) — the behavior under test (`ProcessRunner` stdin/environment/
    /// streaming) is entirely OS-neutral, so it must not be Windows-only coverage even though the
    /// concrete child command differs per platform.
    /// <para>Regression test: `StandardInputEncoding = Encoding.UTF8` makes `StreamWriter` emit a
    /// UTF-8 byte-order-mark preamble on its first write, silently prepending a `U+FEFF` character
    /// (`EF BB BF`) to every prompt — exactly the byte-fidelity boundary this stdin path exists to
    /// protect. Asserting `StartsWith` (not just `Contains`) is what actually catches a leading
    /// BOM; a POSIX `cat` echoes bytes exactly, so no OS-added prefix could mask one.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task StandardInputReachesTheChildProcess()
    {
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "[Console]::In.ReadToEnd()" })
            : ("/bin/sh", new[] { "-c", "cat" });

        ProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(
            new(fileName, arguments, Path.GetTempPath(), StandardInput: "hello from stdin"),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("hello from stdin", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Regression test: an earlier version of the streaming read rewrite reconstructed
    /// `StandardOutput` by joining lines with `\n`, silently normalizing CRLF/bare-CR endings and
    /// dropping a trailing newline. `GitContextReader` hashes this exact text into a content
    /// digest (ADR 0012), so any such normalization is a silent compatibility break, not just a
    /// cosmetic difference. Cross-platform per ADR 0007 (see
    /// <see cref="StandardInputReachesTheChildProcess"/>).</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task StandardOutputPreservesExactLineEndingsForContentDigestFidelity()
    {
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "[Console]::Out.Write(\"line1`r`nline2`r`n\")" })
            : ("/bin/sh", new[] { "-c", @"printf 'line1\r\nline2\r\n'" });

        ProcessRunner runner = new();
        RecordingSink sink = new();
        ProcessResult result = await runner.RunAsync(
            new(fileName, arguments, Path.GetTempPath()),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("line1\r\nline2\r\n", result.StandardOutput);
        Assert.Equal(["line1", "line2"], sink.StandardOutputLines);
    }

    /// <summary>Regression test for the generic per-logical-line safety ceiling
    /// (`MaxBufferedLineChars`): without it, a child that never emits a newline could grow the
    /// line buffer handed to the output sink without limit, and a caller-owned per-line bound
    /// (like `ProviderExecution.MaxLineLengthBytes`) would never get a chance to see and reject
    /// it, since that check only runs on a line this read loop hands off. Cross-platform per ADR
    /// 0007 (see <see cref="StandardInputReachesTheChildProcess"/>).</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnUnterminatedLineIsFlushedToTheSinkInBoundedChunksInsteadOfBufferingForever()
    {
        (string fileName, string[] arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe",
                new[] { "-NoProfile", "-Command", "$s = New-Object string('x', 5000000); [Console]::Out.Write($s)" })
            : ("/bin/sh", new[] { "-c", "head -c 5000000 /dev/zero | tr '\\0' 'x'" });

        ProcessRunner runner = new();
        RecordingSink sink = new();
        ProcessResult result = await runner.RunAsync(
            new(fileName, arguments, Path.GetTempPath()),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5_000_000, result.StandardOutput.Length);
        Assert.True(
            sink.StandardOutputLines.Count > 1,
            "A 5,000,000-character unterminated line should be flushed to the sink in more than one bounded chunk.");
        Assert.Equal(5_000_000, sink.StandardOutputLines.Sum(line => line.Length));
    }

    /// <summary>Cross-platform per ADR 0007 (see <see cref="StandardInputReachesTheChildProcess"/>);
    /// this is the security-relevant half of the item, so it must not be Windows-only coverage.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReplaceEnvironmentGivesTheChildOnlyTheSuppliedVariables()
    {
        Environment.SetEnvironmentVariable("FORGE_TEST_HOST_ONLY", "leaked");
        try
        {
            Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
            {
                ["FORGE_TEST_CHILD_ONLY"] = "present",
            };
            (string fileName, string[] arguments) = OperatingSystem.IsWindows()
                ? ("powershell.exe",
                    new[]
                    {
                        "-NoProfile", "-Command",
                        "\"HOST=[$env:FORGE_TEST_HOST_ONLY] CHILD=[$env:FORGE_TEST_CHILD_ONLY]\"",
                    })
                : ("/bin/sh", new[] { "-c", "echo \"HOST=[$FORGE_TEST_HOST_ONLY] CHILD=[$FORGE_TEST_CHILD_ONLY]\"" });
            foreach (string name in new[] { "SystemRoot", "ComSpec", "PATH", "TEMP", "TMP", "USERPROFILE", "HOME" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (value is not null)
                {
                    overrides[name] = value;
                }
            }

            ProcessRunner runner = new();
            ProcessResult result = await runner.RunAsync(
                new(fileName, arguments, Path.GetTempPath(), overrides, ReplaceEnvironment: true),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("HOST=[] CHILD=[present]", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_TEST_HOST_ONLY", null);
        }
    }

    /// <summary>ADR 0006: "Windows, Linux, and macOS tests launch a child and grandchild and prove
    /// none survives cancellation, timeout, or normal parent exit." Cross-platform per ADR 0007.
    /// Each process writes its own process id to a marker file (rather than relying on file-lock
    /// semantics, which differ enough across platforms to be a weaker cross-platform proof) so the
    /// test can positively confirm both are gone afterward, not merely infer it.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellationTerminatesTheEntireProcessTreeIncludingAGrandchild()
    {
        string directory = CreateTestDirectory();
        try
        {
            string childPidPath = Path.Combine(directory, "child.pid");
            string grandchildPidPath = Path.Combine(directory, "grandchild.pid");
            string readyPath = Path.Combine(directory, "ready");
            (string fileName, string[] arguments) = TreeSpawningCommand(
                directory, childPidPath, grandchildPidPath, readyPath, sleepSeconds: 30);

            ProcessRunner runner = new();
            using CancellationTokenSource cancellation = new();
            Task<ProcessResult> run = runner.RunAsync(new(fileName, arguments, directory), null, cancellation.Token);

            await WaitForFileAsync(readyPath);
            int childPid = await ReadPidAsync(childPidPath);
            int grandchildPid = await ReadPidAsync(grandchildPidPath);
            // The one assertion that would have caught either shape of the "same process twice"
            // bug this test regresses against (a POSIX `$$`-in-subshell quirk, then a POSIX
            // exec-in-tail-position quirk): a genuine grandchild never shares the child's pid.
            Assert.NotEqual(childPid, grandchildPid);
            Assert.True(IsProcessAlive(childPid), "The child process should still be running before cancellation.");
            Assert.True(
                IsProcessAlive(grandchildPid), "The grandchild process should still be running before cancellation.");
            // Captured while confirmed alive, so death can be verified against this exact process
            // instance rather than merely an OS-reassignable pid.
            DateTime childStartedAt = GetProcessStartTimeUtc(childPid);
            DateTime grandchildStartedAt = GetProcessStartTimeUtc(grandchildPid);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            await AssertProcessDiesAsync(childPid, childStartedAt);
            await AssertProcessDiesAsync(grandchildPid, grandchildStartedAt);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>The companion baseline to the cancellation test above: a well-behaved parent that
    /// waits for its own grandchild before exiting leaves nothing running once `RunAsync` returns
    /// normally -- no cancellation or forced kill involved at all.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task NormalParentExitLeavesNoOrphanedGrandchildRunning()
    {
        string directory = CreateTestDirectory();
        try
        {
            string childPidPath = Path.Combine(directory, "child.pid");
            string grandchildPidPath = Path.Combine(directory, "grandchild.pid");
            string readyPath = Path.Combine(directory, "ready");
            // No sleep: both processes exit on their own almost immediately, and the parent script
            // waits for the grandchild before it exits.
            (string fileName, string[] arguments) = TreeSpawningCommand(
                directory, childPidPath, grandchildPidPath, readyPath, sleepSeconds: 0);

            ProcessRunner runner = new();
            ProcessResult result = await runner.RunAsync(
                new(fileName, arguments, directory), null, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            int childPid = await ReadPidAsync(childPidPath);
            int grandchildPid = await ReadPidAsync(grandchildPidPath);
            Assert.NotEqual(childPid, grandchildPid);
            await AssertProcessDiesAsync(childPid);
            await AssertProcessDiesAsync(grandchildPid);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-process-tree-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Builds a per-OS command that: writes the running (child) process's own id to
    /// <paramref name="childPidPath"/>, spawns a grandchild that writes its own id to
    /// <paramref name="grandchildPidPath"/> and then signals <paramref name="readyPath"/>, and has
    /// both processes sleep for <paramref name="sleepSeconds"/> (0 for an immediate, well-behaved
    /// exit -- the parent script still waits for the grandchild before exiting itself). The
    /// grandchild's own PowerShell script is written to a `.ps1` file (rather than nested inline in
    /// the parent's `-Command` string) so its own single-quoted path literals never have to be
    /// re-escaped to survive being embedded inside the parent script's own quoting.</summary>
    private static (string FileName, string[] Arguments) TreeSpawningCommand(
        string directory, string childPidPath, string grandchildPidPath, string readyPath, int sleepSeconds)
    {
        if (OperatingSystem.IsWindows())
        {
            string grandchildScriptPath = Path.Combine(directory, "grandchild.ps1");
            string grandchildScript = string.Join(
                Environment.NewLine,
                $"[IO.File]::WriteAllText('{grandchildPidPath}', $PID)",
                $"[IO.File]::WriteAllText('{readyPath}','ready')",
                sleepSeconds > 0 ? $"Start-Sleep -Seconds {sleepSeconds}" : string.Empty);
            File.WriteAllText(grandchildScriptPath, grandchildScript);

            string parentScript =
                $"[IO.File]::WriteAllText('{childPidPath}', $PID); " +
                "$grandchild = Start-Process powershell.exe " +
                $"-ArgumentList '-NoProfile','-File','{grandchildScriptPath}' " +
                "-WindowStyle Hidden -PassThru; " +
                (sleepSeconds > 0
                    ? $"Start-Sleep -Seconds {sleepSeconds}"
                    : "$grandchild.WaitForExit()");
            return ("powershell.exe", ["-NoProfile", "-Command", parentScript]);
        }

        // `$$` inside a `(...)` subshell still reports the *invoking* shell's own pid in POSIX
        // sh/dash/bash, not the subshell's -- it is fixed at shell startup, not re-evaluated per
        // fork. Spawning the grandchild as a genuinely separate `sh` invocation (its own process,
        // exec'd fresh) is what gives it its own, correctly-reported `$$`; a script file sidesteps
        // nested-quoting the same way the Windows `.ps1` file does above.
        string posixGrandchildScriptPath = Path.Combine(directory, "grandchild.sh");
        string posixGrandchildScript = string.Join(
            "\n",
            $"echo $$ > '{grandchildPidPath}'",
            $"touch '{readyPath}'",
            sleepSeconds > 0 ? $"sleep {sleepSeconds}" : "true");
        File.WriteAllText(posixGrandchildScriptPath, posixGrandchildScript);

        // Never a bare trailing `sh '<script>'` with nothing after it: several POSIX shells
        // (including dash and bash's script-final-command path) replace the current process image
        // in place (`exec`) for a simple command in tail position, so the "grandchild" would
        // become the *same* process as the parent instead of a genuine child of it -- silently
        // testing one process twice. Backgrounding with `&` always forks, never exec-replaces, so
        // both branches use it; the no-sleep branch adds `wait` to still block for it.
        string posixScript = sleepSeconds > 0
            ? $"echo $$ > '{childPidPath}'; sh '{posixGrandchildScriptPath}' & sleep {sleepSeconds}"
            : $"echo $$ > '{childPidPath}'; sh '{posixGrandchildScriptPath}' & wait";
        return ("/bin/sh", ["-c", posixScript]);
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (int attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"'{path}' was never created.");
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                return int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static DateTime GetProcessStartTimeUtc(int processId)
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime();
    }

    /// <summary>Matches on start time too, not just the pid alone: an OS can reassign a just-freed
    /// pid to an unrelated process before this check runs, which a pid-only liveness check would
    /// misreport as "still alive."</summary>
    private static bool IsSameProcessStillAlive(int processId, DateTime expectedStartTimeUtc)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == expectedStartTimeUtc;
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>When <paramref name="expectedStartTimeUtc"/> is supplied (the process was
    /// previously confirmed alive at that instant), death is checked against that exact process
    /// instance rather than the pid alone -- see <see cref="IsSameProcessStillAlive"/>.</summary>
    private static async Task AssertProcessDiesAsync(int processId, DateTime? expectedStartTimeUtc = null)
    {
        bool StillAlive() => expectedStartTimeUtc is { } startTime
            ? IsSameProcessStillAlive(processId, startTime)
            : IsProcessAlive(processId);

        for (int attempt = 0; attempt < 100 && StillAlive(); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(StillAlive(), $"Process {processId} should no longer be running.");
    }

    private sealed class RecordingContainment : IProcessContainment
    {
        public int AttachCount { get; private set; }

        public bool Disposed { get; private set; }

        public IDisposable Attach(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);
            AttachCount++;
            return new Handle(this);
        }

        private sealed class Handle(RecordingContainment owner) : IDisposable
        {
            public void Dispose() => owner.Disposed = true;
        }
    }

    private sealed class RecordingSink : IProcessOutputSink
    {
        public List<string> StandardOutputLines { get; } = [];

        public Task OnStandardOutputLineAsync(string line, CancellationToken cancellationToken)
        {
            StandardOutputLines.Add(line);
            return Task.CompletedTask;
        }

        public Task OnStandardErrorLineAsync(string line, CancellationToken cancellationToken) => Task.CompletedTask;
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
