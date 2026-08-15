using System.Diagnostics;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host.Client;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

public sealed class ForgeHostProcessTests
{
    // ADR 0005: the process/lease/protocol contract is platform-neutral, proven here against
    // Forge.Host.TestHost — the same Forge.Host.Runtime with no real (Windows-only) ILlmProvider
    // wired in. A real provider's end-to-end behavior is exercised separately, Windows-only,
    // against Forge.Host.Windows.
    private static readonly string ExecutableName =
        "Forge.Host.TestHost" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ClientReadsTheProjectSnapshotAndControlEventsFromARealHostProcess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;

        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        string executablePath = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        Assert.True(File.Exists(executablePath), $"'{executablePath}' must ship next to the test binaries.");

        int hostProcessId = -1;
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        try
        {
            ControlDiagnostic diagnostic = await client.EnsureConnectedAsync(
                async ct => hostProcessId = await ForgeHostLauncher
                    .StartAsync(executablePath, environment.ProjectRoot, instanceId, ct)
                    .ConfigureAwait(false),
                cancellationToken);
            Assert.Equal(ControlDiagnosticCode.None, diagnostic.Code);

            ControlResponse snapshotResponse = await client.SendAsync(
                ControlProtocol.GetProjectSnapshotKind,
                JsonSerializer.SerializeToElement(new { detail = "full" }),
                cancellationToken);
            Assert.Equal(ControlDiagnosticCode.None, snapshotResponse.Diagnostic.Code);
            JsonElement snapshotPayload = Assert.IsType<JsonElement>(snapshotResponse.Payload!.Value);
            Assert.True(snapshotPayload.GetProperty("project").GetProperty("initialized").GetBoolean());
            JsonElement sprints = snapshotPayload.GetProperty("sprints");
            Assert.Equal(1, sprints.GetArrayLength());
            Assert.Equal(sprintId.Value, sprints[0].GetProperty("id").GetGuid());

            ControlResponse eventsResponse = await client.SendAsync(
                ControlProtocol.ReadControlEventsKind,
                null,
                cancellationToken);
            Assert.Equal(ControlDiagnosticCode.None, eventsResponse.Diagnostic.Code);
            JsonElement eventsPayload = Assert.IsType<JsonElement>(eventsResponse.Payload!.Value);
            Assert.True(eventsPayload.GetProperty("events").GetArrayLength() > 0);
            Assert.Equal("none", eventsPayload.GetProperty("diagnostic_code").GetString());
        }
        finally
        {
            if (hostProcessId > 0)
            {
                try
                {
                    using Process process = Process.GetProcessById(hostProcessId);
                    process.Kill(true);
                    process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    // The process already exited between GetProcessById and Kill/WaitForExit.
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ASuccessorHostRecoversTheProjectLeaseAfterTheFirstHostCrashes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        string executablePath = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        Assert.True(File.Exists(executablePath), $"'{executablePath}' must ship next to the test binaries.");

        // Two different instance ids for the same project — proving both that the project lease is
        // shared across instances (ADR 0005) and that a *crashed* (not gracefully stopped) prior
        // owner's abandoned named-mutex lease is still recoverable by a real successor process.
        string firstInstanceId = InstanceIdentity.CreateEphemeral();
        string secondInstanceId = InstanceIdentity.CreateEphemeral();
        int firstProcessId = -1;
        int secondProcessId = -1;
        try
        {
            await using (ForgeHostClient firstClient = new(
                new NamedPipeControlTransport(),
                new ForgeHostClientOptions(projectId, firstInstanceId, "1.0.0-test")))
            {
                ControlDiagnostic connected = await firstClient.EnsureConnectedAsync(
                    async ct => firstProcessId = await ForgeHostLauncher
                        .StartAsync(executablePath, environment.ProjectRoot, firstInstanceId, ct)
                        .ConfigureAwait(false),
                    cancellationToken);
                Assert.Equal(ControlDiagnosticCode.None, connected.Code);
                Assert.Equal(ControlDiagnosticCode.None, (await firstClient.PingAsync(cancellationToken)).Diagnostic.Code);
            }

            // Kill, not a graceful stop: the mutex is never released, so it becomes abandoned at the
            // OS level — the exact scenario MutexProjectLease.TryAcquire's AbandonedMutexException
            // handling exists for, which nothing in this suite exercised with a real process before.
            using (Process first = Process.GetProcessById(firstProcessId))
            {
                first.Kill(true);
                Assert.True(first.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds));
            }

            await using ForgeHostClient secondClient = new(
                new NamedPipeControlTransport(),
                new ForgeHostClientOptions(projectId, secondInstanceId, "1.0.0-test"));
            ControlDiagnostic secondConnected = await secondClient.EnsureConnectedAsync(
                async ct => secondProcessId = await ForgeHostLauncher
                    .StartAsync(executablePath, environment.ProjectRoot, secondInstanceId, ct)
                    .ConfigureAwait(false),
                cancellationToken);

            Assert.Equal(ControlDiagnosticCode.None, secondConnected.Code);
            Assert.Equal(ControlDiagnosticCode.None, (await secondClient.PingAsync(cancellationToken)).Diagnostic.Code);
        }
        finally
        {
            TryKillProcess(firstProcessId);
            TryKillProcess(secondProcessId);
        }
    }

    private static void TryKillProcess(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(true);
            process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The process already exited between GetProcessById and Kill/WaitForExit.
        }
    }
}
