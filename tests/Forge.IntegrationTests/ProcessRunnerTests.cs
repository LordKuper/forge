using Forge.Application;
using Forge.Infrastructure;

namespace Forge.IntegrationTests;

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

    private static async Task DeleteDirectoryAsync(string directory)
    {
        for (int attempt = 0; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}
