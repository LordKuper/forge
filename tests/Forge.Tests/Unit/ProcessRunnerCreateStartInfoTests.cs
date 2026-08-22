using Forge.Application;
using Forge.Infrastructure;

namespace Forge.UnitTests;

public sealed class ProcessRunnerCreateStartInfoTests
{
    /// <summary>Regression test: without `CreateNoWindow = true`, Windows briefly flashes a
    /// console window for every redirected child process (provider `--version`/auth checks,
    /// `git`, ...) even though Forge Host itself starts hidden. `ProcessStartInfo.CreateNoWindow`
    /// is a no-op on non-Windows, so asserting it here needs no OS gate.</summary>
    [Fact]
    public void CreateStartInfoDoesNotAllocateAConsoleWindow()
    {
        System.Diagnostics.ProcessStartInfo startInfo =
            ProcessRunner.CreateStartInfo(new ProcessRequest("git", ["status"], "."));

        Assert.True(startInfo.CreateNoWindow);
    }
}
