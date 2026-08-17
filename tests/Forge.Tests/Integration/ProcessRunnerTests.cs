using Forge.Application;
using Forge.Infrastructure;

namespace Forge.IntegrationTests;

[Collection("External process tests")]
public sealed class ProcessRunnerTests
{
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StandardInputReachesTheChildProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(
            new(
                "powershell.exe",
                ["-NoProfile", "-Command", "[Console]::In.ReadToEnd()"],
                Path.GetTempPath(),
                StandardInput: "hello from stdin"),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello from stdin", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Regression test: an earlier version of the streaming read rewrite reconstructed
    /// `StandardOutput` by joining lines with `\n`, silently normalizing CRLF/bare-CR endings and
    /// dropping a trailing newline. `GitContextReader` hashes this exact text into a content
    /// digest (ADR 0012), so any such normalization is a silent compatibility break, not just a
    /// cosmetic difference.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task StandardOutputPreservesExactLineEndingsForContentDigestFidelity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();
        RecordingSink sink = new();
        ProcessResult result = await runner.RunAsync(
            new(
                "powershell.exe",
                ["-NoProfile", "-Command", "[Console]::Out.Write(\"line1`r`nline2`r`n\")"],
                Path.GetTempPath()),
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
    /// it, since that check only runs on a line this read loop hands off.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnUnterminatedLineIsFlushedToTheSinkInBoundedChunksInsteadOfBufferingForever()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();
        RecordingSink sink = new();
        string command = "$s = New-Object string('x', 5000000); [Console]::Out.Write($s)";
        ProcessResult result = await runner.RunAsync(
            new("powershell.exe", ["-NoProfile", "-Command", command], Path.GetTempPath()),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(5_000_000, result.StandardOutput.Length);
        Assert.True(
            sink.StandardOutputLines.Count > 1,
            "A 5,000,000-character unterminated line should be flushed to the sink in more than one bounded chunk.");
        Assert.Equal(5_000_000, sink.StandardOutputLines.Sum(line => line.Length));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReplaceEnvironmentGivesTheChildOnlyTheSuppliedVariables()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Environment.SetEnvironmentVariable("FORGE_TEST_HOST_ONLY", "leaked");
        try
        {
            Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
            {
                ["FORGE_TEST_CHILD_ONLY"] = "present",
            };
            foreach (string name in new[] { "SystemRoot", "ComSpec", "PATH", "TEMP", "TMP", "USERPROFILE" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (value is not null)
                {
                    overrides[name] = value;
                }
            }

            ProcessRunner runner = new();
            ProcessResult result = await runner.RunAsync(
                new(
                    "powershell.exe",
                    ["-NoProfile", "-Command", "\"HOST=[$env:FORGE_TEST_HOST_ONLY] CHILD=[$env:FORGE_TEST_CHILD_ONLY]\""],
                    Path.GetTempPath(),
                    overrides,
                    ReplaceEnvironment: true),
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
