using System.Globalization;
using Forge.Application;
using Forge.Infrastructure;
using Forge.Runtime.Windows;

// A minimal harness proving Windows Job Object containment survives an abrupt kill of the SPAWNING
// process, not merely a graceful one -- something no in-process xunit test can observe about
// itself, since the xunit test host is the very process that would need to die. See
// tests/Forge.Tests/WindowsRuntime/ProcessContainmentCrashTests.cs, which spawns this probe in
// "harness" mode, waits for it to report a live grandchild, then kills the harness itself
// (Process.Kill(), no tree) to simulate an abrupt Forge Host crash.
//
// Usage:
//   Forge.ProcessContainmentProbe harness <directory> <sleepSeconds>
//     Spawns itself in "child" mode through the real, production ProcessRunner +
//     WindowsJobObjectProcessContainment path -- the exact adapter
//     Forge.Host.Windows/Forge.Cli.Windows/Forge.Desktop install at startup -- then blocks forever
//     so an external caller can kill this harness process itself.
//   Forge.ProcessContainmentProbe child <directory> <sleepSeconds>
//     Writes its own process id to <directory>/child.pid, spawns itself again in "grandchild" mode
//     (through the same production path, so descendant containment -- not just direct-child
//     containment -- is actually exercised), then sleeps.
//   Forge.ProcessContainmentProbe grandchild <directory> <sleepSeconds>
//     Writes its own process id to <directory>/grandchild.pid, then sleeps.
if (args.Length != 3 ||
    args[0] is not ("harness" or "child" or "grandchild") ||
    !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sleepSeconds))
{
    Console.Error.WriteLine(
        "Usage: Forge.ProcessContainmentProbe <harness|child|grandchild> <directory> <sleepSeconds>");
    return 64;
}

string mode = args[0];
string directory = args[1];

if (mode == "grandchild")
{
    await File.WriteAllTextAsync(
        Path.Combine(directory, "grandchild.pid"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    await Task.Delay(TimeSpan.FromSeconds(sleepSeconds));
    return 0;
}

string? selfPath = Environment.ProcessPath;
if (selfPath is null)
{
    Console.Error.WriteLine("Could not resolve this process's own executable path.");
    return 1;
}

if (mode == "child")
{
    await File.WriteAllTextAsync(
        Path.Combine(directory, "child.pid"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    // Spawned well after Attach has already landed on this process (the exact non-racy descendant
    // case round 2 review asked to exercise), through the same production ProcessRunner +
    // WindowsJobObjectProcessContainment path: proves the OS's automatic job-membership inheritance
    // actually contains a DESCENDANT, not merely the process WindowsJobObjectProcessContainment.Attach
    // was itself called on.
    ProcessRunner grandchildRunner = new(new WindowsJobObjectProcessContainment());
    Task<ProcessResult> grandchildTask = grandchildRunner.RunAsync(
        new ProcessRequest(
            selfPath, ["grandchild", directory, sleepSeconds.ToString(CultureInfo.InvariantCulture)], directory),
        null,
        CancellationToken.None);
    _ = grandchildTask.ContinueWith(
        static faulted => Console.Error.WriteLine($"Grandchild spawn failed: {faulted.Exception}"),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    await Task.Delay(TimeSpan.FromSeconds(sleepSeconds));
    return 0;
}

// The exact production adapter -- proving crash survival against anything less would not prove
// anything about the real product.
ProcessRunner runner = new(new WindowsJobObjectProcessContainment());
Task<ProcessResult> runTask = runner.RunAsync(
    new ProcessRequest(
        selfPath, ["child", directory, sleepSeconds.ToString(CultureInfo.InvariantCulture)], directory),
    null,
    CancellationToken.None);
// Observed, never awaited to completion here (the child sleeps far longer than this harness's own
// startup): a faulted spawn must be visible on stderr rather than silently leaving no child.pid for
// the test to wait on forever.
_ = runTask.ContinueWith(
    static faulted => Console.Error.WriteLine($"Child spawn failed: {faulted.Exception}"),
    CancellationToken.None,
    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
    TaskScheduler.Default);

// Blocks forever: the test kills this process itself (not gracefully) to simulate an abrupt Host
// crash, then checks whether the child (and grandchild) spawned above survived as an orphan.
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
