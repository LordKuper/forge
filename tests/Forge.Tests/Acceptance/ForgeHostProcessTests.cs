using System.Diagnostics;
using Forge.Application;
using Forge.Configuration;
using Forge.Host.Client;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

public sealed class ForgeHostProcessTests
{
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
                }
                catch (ArgumentException)
                {
                    // The process already exited.
                }
            }
        }
    }
}
