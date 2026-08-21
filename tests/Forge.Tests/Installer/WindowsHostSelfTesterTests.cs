using Forge.Updater.Windows;

namespace Forge.InstallerTests;

/// <summary>Every install/update/rollback test other than <c>WindowsUpdateStrategyTests</c>'s own
/// <c>HostSelfTestStopsAfterItsDeadline</c> substitutes a fake for <see cref="IWindowsHostSelfTester"/>.
/// That existing test already covers the real <see cref="WindowsHostSelfTester"/> against a real
/// hung process and its kill-on-deadline path (round 1 review of PR #85 found the Stage 13 audit
/// this file was added for had wrongly claimed that path was untested — it was pre-existing
/// coverage this file's own first draft duplicated). The actually-uncovered paths were the two
/// terminal exit codes: these two tests fill that gap against a real, quickly-exiting child
/// process, since a fake can only prove <c>WindowsUpdateStrategy</c> reacts correctly to a
/// verdict, never that the verdict itself is computed correctly from a real <c>Process</c>'s exit
/// code.</summary>
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
