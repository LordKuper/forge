using System.Diagnostics;
using Forge.Runtime.Posix;
using Microsoft.Extensions.Logging;

namespace Forge.IntegrationTests;

/// <summary>
/// Round 2 review's finding 7: settles, on a real POSIX runner (this file lives in Forge.Tests'
/// neutral net10.0 TFM group, not WindowsRuntime/, specifically so it actually executes on the
/// ubuntu-24.04/macos-14 legs of the portable CI matrix rather than merely compiling there),
/// whether <see cref="PosixProcessGroupContainment"/>'s `setpgid` call takes effect.
///
/// Investigation (see that type's own doc comment) concluded it does not, on every currently
/// supported .NET version: .NET's Unix process-launch implementation synchronously waits, inside the
/// native `Process.Start` call itself, for the child to reach `execve` before returning control to
/// managed code -- so by the time <c>Attach</c> runs, the child has, by construction, already
/// exec'd, and POSIX specifies `setpgid` fails with `EACCES` in exactly that case.
///
/// This test proves that failure is now OBSERVABLE (logged, exactly once across repeated calls)
/// rather than silently discarded -- the concrete, testable half of the finding. It deliberately
/// does not assert the exact OS-level process-group id, which would encode a kernel/runtime
/// implementation detail this test does not need in order to prove the fix; if a future .NET version
/// changes the synchronization above and `setpgid` starts succeeding, this test fails loudly, which
/// is the right outcome -- it would force the doc comment/CHANGELOG's honest "non-functional" claim
/// to be revisited rather than silently going stale.
/// </summary>
[Collection("External process tests")]
public sealed class PosixProcessGroupContainmentTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AttachNeverThrowsAndLogsTheFailureExactlyOnceAcrossRepeatedCalls()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        RecordingLogger logger = new();
        PosixProcessGroupContainment containment = new(logger);

        using (Process first = StartSleeper())
        {
            using IDisposable firstHandle = containment.Attach(first);
            Assert.NotNull(firstHandle);
            KillIfAlive(first);
        }

        using (Process second = StartSleeper())
        {
            using IDisposable secondHandle = containment.Attach(second);
            Assert.NotNull(secondHandle);
            KillIfAlive(second);
        }

        await Task.CompletedTask;

        IReadOnlyList<string> entries = logger.Snapshot();
        int warnings = entries.Count(entry =>
            entry.StartsWith("ProcessContainmentSetpgidFailed", StringComparison.Ordinal) ||
            entry.StartsWith("ProcessContainmentLibcUnavailable", StringComparison.Ordinal));

        Assert.True(
            warnings == 1,
            $"Expected exactly one containment warning across two Attach calls (setpgid is expected to fail " +
            $"every time -- see this class's own doc comment), got {warnings}: {string.Join("; ", entries)}");
    }

    private static Process StartSleeper()
    {
        Process? process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sleep 30" },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        return process;
    }

    private static void KillIfAlive(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup only: a process that raced to exit on its own between the
            // HasExited check and Kill() must never fail this test.
        }
    }

    private sealed class RecordingLogger : ILogger<PosixProcessGroupContainment>
    {
        private readonly List<string> entries = [];

        public IReadOnlyList<string> Snapshot()
        {
            lock (entries)
            {
                return [.. entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (entries)
            {
                entries.Add($"{eventId.Name}: {formatter(state, exception)}");
            }
        }
    }
}
