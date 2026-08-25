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
//     Writes its own process id to <directory>/child.pid, waits until the harness confirms Attach has
//     returned, then spawns itself again in "grandchild" mode through a PLAIN, uncontained
//     ProcessRunner. The grandchild's only route into any job is therefore the OS's automatic
//     job-membership inheritance from this process -- not a second containment layer.
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
    await WriteFileAtomicallyAsync(
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
    await WriteFileAtomicallyAsync(
        Path.Combine(directory, "child.pid"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    string attachedPath = Path.Combine(directory, "attached");
    for (int attempt = 0; attempt < 200 && !File.Exists(attachedPath); attempt++)
    {
        await Task.Delay(50);
    }

    if (!File.Exists(attachedPath))
    {
        Console.Error.WriteLine("The harness did not confirm process-containment attachment.");
        return 1;
    }

    // The marker is written only after the harness's RunAsync call has returned control, whose
    // synchronous prefix includes Attach. The grandchild therefore starts outside the accepted
    // Attach-after-Start race window, through a plain ProcessRunner with no containment of its own.
    ProcessRunner grandchildRunner = new();
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
await WriteFileAtomicallyAsync(Path.Combine(directory, "attached"), "attached");
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

static async Task WriteFileAtomicallyAsync(string path, string contents)
{
    string temporaryPath = path + ".tmp";
    await File.WriteAllTextAsync(temporaryPath, contents);
    File.Move(temporaryPath, path);
}
