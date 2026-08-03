using Forge.Updater;
using Forge.Updater.Windows;

namespace Forge.InstallerTests;

[Collection("External process tests")]
public sealed class WindowsRestartCoordinatorTests
{
    [Fact]
    [Trait("Category", "Installer")]
    public async Task TerminatesAnUnconfirmedHostBeforeReturning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"forge-restart-tests-{Guid.NewGuid():N}");
        string lockPath = Path.Combine(directory, "host.lock");
        string readyPath = Path.Combine(directory, "ready");
        string scriptPath = Path.Combine(directory, "host.cmd");
        string hostScriptPath = Path.Combine(directory, "host.ps1");
        Directory.CreateDirectory(directory);
        try
        {
            WriteHostScript(scriptPath, hostScriptPath, lockPath, readyPath);
            WindowsRestartCoordinator coordinator = new(new PersistentTokenStore(), TimeSpan.FromSeconds(10));
            RestartContext restart = new(
                "token",
                scriptPath,
                [],
                directory,
                new(SemanticVersion.Parse("1.1.0"), new("windows", "x64", "portable_bundle"), UpdateSurface.Cli));

            Task<UpdateDiagnostic> operation = coordinator.RestartAsync(restart, TestContext.Current.CancellationToken).AsTask();
            for (int attempt = 0; attempt < 500 && !File.Exists(readyPath); attempt++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(readyPath), "The restarted host did not signal readiness.");
            Assert.Equal(UpdateDiagnosticCode.HandshakeFailed, (await operation).Code);
            await using (FileStream stream = await WaitForLockReleaseAsync(lockPath))
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
    [Trait("Category", "Installer")]
    public async Task TerminatesTheHostWhenTheTokenStoreFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"forge-restart-tests-{Guid.NewGuid():N}");
        string lockPath = Path.Combine(directory, "host.lock");
        string readyPath = Path.Combine(directory, "ready");
        string scriptPath = Path.Combine(directory, "host.cmd");
        string hostScriptPath = Path.Combine(directory, "host.ps1");
        Directory.CreateDirectory(directory);
        try
        {
            WriteHostScript(scriptPath, hostScriptPath, lockPath, readyPath);
            WindowsRestartCoordinator coordinator = new(new ReadyThenThrowingTokenStore(readyPath));
            RestartContext restart = new(
                "token",
                scriptPath,
                [],
                directory,
                new(SemanticVersion.Parse("1.1.0"), new("windows", "x64", "portable_bundle"), UpdateSurface.Cli));

            Assert.Equal(UpdateDiagnosticCode.RestartFailed, (await coordinator.RestartAsync(
                restart,
                TestContext.Current.CancellationToken)).Code);
            Assert.True(File.Exists(readyPath), "The restarted host did not signal readiness.");
            await using (FileStream stream = await WaitForLockReleaseAsync(lockPath))
            {
                Assert.True(stream.CanWrite);
            }
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
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

    private static void WriteHostScript(
        string commandPath,
        string scriptPath,
        string lockPath,
        string readyPath)
    {
        File.WriteAllText(
            scriptPath,
            $"$stream=[IO.File]::Open('{lockPath}','OpenOrCreate','ReadWrite','None');\r\n" +
            $"[IO.File]::WriteAllText('{readyPath}','ready');\r\n" +
            "Start-Sleep -Seconds 30");
        File.WriteAllText(
            commandPath,
            "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"");
    }

    private static async Task<FileStream> WaitForLockReleaseAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class PersistentTokenStore : IRestartTokenStore
    {
        public bool TryCreate(string token, RestartIdentity identity) => false;

        public bool TryConsume(string token, RestartIdentity identity) => false;

        public void Revoke(string token)
        {
        }

        public bool Exists(string token) => true;
    }

    private sealed class ReadyThenThrowingTokenStore(string readyPath) : IRestartTokenStore
    {
        public bool TryCreate(string token, RestartIdentity identity) => false;

        public bool TryConsume(string token, RestartIdentity identity) => false;

        public void Revoke(string token)
        {
        }

        public bool Exists(string token)
        {
            for (int attempt = 0; attempt < 500 && !File.Exists(readyPath); attempt++)
            {
                Thread.Sleep(20);
            }

            throw new IOException();
        }
    }
}
