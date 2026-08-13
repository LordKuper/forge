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
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ClientReadsTheProjectSnapshotAndControlEventsFromARealHostProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Same Windows-only process-spawn scope as ClientDiscoversStartsAndPingsARealHostProcess.
            return;
        }

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
        string executablePath = Path.Combine(AppContext.BaseDirectory, "Forge.Host.exe");
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
    public async Task ClientDiscoversStartsAndPingsARealHostProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The published Windows bundle ships Forge.Host.exe next to forge.exe; the client-launcher contract
            // this proves end to end is Windows-only for the MVP, matching every other process-spawn acceptance
            // test in this suite.
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        string executablePath = Path.Combine(AppContext.BaseDirectory, "Forge.Host.exe");
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
            ControlResponse response = await client.PingAsync(cancellationToken);
            Assert.Equal(ControlDiagnosticCode.None, response.Diagnostic.Code);
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
}
