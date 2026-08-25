using Forge.Application;
using Forge.Infrastructure;

// Stands in for the real Host process for
// `ProcessRunnerTests.AnAbruptHostProcessKillLeavesNoOrphanedProviderProcess`
// (tests/Forge.Tests/Integration/ProcessRunnerTests.cs): uses the exact production
// Forge.Infrastructure.ProcessRunner -- the same class SprintScheduler's own provider invocation
// path uses -- to spawn a long-lived "provider" stand-in child and then blocks on it. A caller kills
// *this* process abruptly (never ProcessRunner's own cancellation path, and never the spawned child
// directly) to observe whether ProcessRunner's containment (once
// `feature/process-group-containment` lands) still reclaims the child once this process -- the
// "Host" stand-in -- dies without warning. Routing through the real ProcessRunner (rather than a
// hand-rolled `Process.Start`) matters: once containment lands inside ProcessRunner itself, only a
// child it actually spawned is a candidate for that containment to reclaim.
//
// Usage: Forge.ProcessContainmentProbe <providerPidPath> <readyPath> <sleepSeconds>
// Writes the provider's own pid to <providerPidPath> and signals <readyPath> once the provider has
// started, then blocks until the provider exits (normally after <sleepSeconds>, or -- once this
// process is killed and containment reclaims it -- early).
if (args.Length != 3 || !int.TryParse(args[2], out int sleepSeconds))
{
    Console.Error.WriteLine("Usage: Forge.ProcessContainmentProbe <providerPidPath> <readyPath> <sleepSeconds>");
    return 64;
}

string providerPidPath = args[0];
string readyPath = args[1];

string fileName;
string[] arguments;
if (OperatingSystem.IsWindows())
{
    fileName = "powershell.exe";
    arguments =
    [
        "-NoProfile",
        "-Command",
        $"[IO.File]::WriteAllText('{providerPidPath}', $PID); " +
        $"[IO.File]::WriteAllText('{readyPath}','ready'); " +
        $"Start-Sleep -Seconds {sleepSeconds}",
    ];
}
else
{
    fileName = "/bin/sh";
    arguments = ["-c", $"echo $$ > '{providerPidPath}'; touch '{readyPath}'; sleep {sleepSeconds}"];
}

ProcessRunner runner = new();
await runner.RunAsync(
    new ProcessRequest(fileName, arguments, Path.GetTempPath()), null, CancellationToken.None).ConfigureAwait(false);
return 0;
