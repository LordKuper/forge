using Forge.Updater.Windows;

namespace Forge.InstallerTests;

/// <summary>Every other install/update/rollback test substitutes a fake for
/// <see cref="IWindowsHostSelfTester"/> (Stage 13's P13.11-P13.24 audit found this was the one
/// real-process boundary in the whole update pipeline with no test against the real
/// implementation). These exercise the genuine <see cref="WindowsHostSelfTester"/> against real
/// child processes — success, failure, and a hung process past its deadline — since a fake can
/// only prove <c>WindowsUpdateStrategy</c> reacts correctly to a verdict, never that the verdict
/// itself is computed correctly from a real <c>Process</c>.</summary>
[Collection("External process tests")]
public sealed class WindowsHostSelfTesterTests
{
    [Fact]
    [Trait("Category", "Installer")]
    public async Task VerifyAsyncReturnsTrueForAProcessThatExitsSuccessfully()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string script = WriteScript("@echo off\r\nexit /b 0");
        try
        {
            WindowsHostSelfTester tester = new(TimeSpan.FromSeconds(10));
            bool result = await tester.VerifyAsync(script, TestContext.Current.CancellationToken);
            Assert.True(result);
        }
        finally
        {
            DeleteScript(script);
        }
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task VerifyAsyncReturnsFalseForAProcessThatExitsWithAFailureCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string script = WriteScript("@echo off\r\nexit /b 1");
        try
        {
            WindowsHostSelfTester tester = new(TimeSpan.FromSeconds(10));
            bool result = await tester.VerifyAsync(script, TestContext.Current.CancellationToken);
            Assert.False(result);
        }
        finally
        {
            DeleteScript(script);
        }
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task VerifyAsyncKillsAndReturnsFalseForAProcessThatOutlivesItsDeadline()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string script = WriteScript("@echo off\r\npowershell.exe -NoProfile -Command \"Start-Sleep -Seconds 30\"");
        try
        {
            WindowsHostSelfTester tester = new(TimeSpan.FromMilliseconds(500));
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool result = await tester.VerifyAsync(script, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.False(result);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(25),
                $"VerifyAsync took {stopwatch.Elapsed}, close to the 30s script sleep instead of its own 500ms deadline — the hung process was not actually killed.");
        }
        finally
        {
            DeleteScript(script);
        }
    }

    private static string WriteScript(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"forge-self-tester-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteScript(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
