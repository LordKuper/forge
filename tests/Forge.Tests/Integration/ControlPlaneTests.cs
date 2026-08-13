using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host;
using Forge.Host.Client;
using Forge.Infrastructure;
using Forge.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.IntegrationTests;

public sealed class ControlPlaneTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task HandshakeAndPingRoundTrip()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);
        string instanceId = InstanceIdentity.CreateEphemeral();

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));

        ControlDiagnostic connected = await client.EnsureConnectedAsync(null, cancellationToken);
        Assert.Equal(ControlDiagnosticCode.None, connected.Code);
        Assert.True(client.IsConnected);

        ControlResponse response = await client.PingAsync(cancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IncompatibleProtocolVersionIsRejectedBeforeAnyProjectAccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();
        await using ILocalControlConnection connection = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken);
        ControlHandshakeRequest request = new("99.0.0", "1.0.0-test", instanceId, [], Guid.NewGuid());
        await connection.SendAsync(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, ControlProtocol.JsonOptions),
            TimeSpan.FromSeconds(5),
            cancellationToken);
        byte[] responseBytes = await connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken);
        ControlHandshakeResponse response = System.Text.Json.JsonSerializer
            .Deserialize<ControlHandshakeResponse>(responseBytes, ControlProtocol.JsonOptions)!;

        Assert.Equal(ControlDiagnosticCode.VersionIncompatible, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DiscoveryReportsUnavailableWhenNoHostIsListening()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(Guid.NewGuid(), InstanceIdentity.CreateEphemeral(), "1.0.0-test")
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(300),
            });

        ControlDiagnostic diagnostic = await client.EnsureConnectedAsync(null, cancellationToken);

        Assert.Equal(ControlDiagnosticCode.Unavailable, diagnostic.Code);
        Assert.False(client.IsConnected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASecondHostForTheSameProjectExitsInsteadOfCompetingForTheListener()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost first = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        // host.StartAsync() returns once ExecuteAsync starts running, not once it has won the lease — connect a
        // client first so "first" is confirmed listening (and therefore already holds the lease) before "second"
        // starts, or the two Hosts could race for the mutex in either order.
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.False(first.IsStopping);

        await using ControlPlaneHost second = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        // The second Host could not acquire the project lease, so it stops itself rather than becoming a
        // competing listener on the same pipe name (see the comment in ControlPlaneHostedService.ExecuteAsync).
        // MutexProjectLease.TryAcquire's own contended timeout is 2s, so give this real time to resolve.
        Assert.True(await second.WaitForStoppingAsync(TimeSpan.FromSeconds(10), cancellationToken));
        Assert.False(first.IsStopping);

        // The first Host is still the sole listener and keeps serving.
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASecondHostForTheSameProjectUnderADifferentInstanceIdStillExitsInsteadOfCompeting()
    {
        // ADR 0005: "Release, development, test, CLI, and Desktop instances use the same lease
        // namespace, so distinct instance data roots cannot become concurrent writers of one
        // `.forge/` tree." Unlike ASecondHostForTheSameProjectExitsInsteadOfCompetingForTheListener
        // (which reuses one instance id for both Hosts), this proves the lease itself spans two
        // *different* instance ids for the same project — each with its own distinct pipe name, so
        // this is specifically testing the lease, not an incidental pipe-name collision.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string firstInstanceId = InstanceIdentity.CreateEphemeral();
        string secondInstanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost first = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            firstInstanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, firstInstanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.False(first.IsStopping);

        await using ControlPlaneHost second = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            secondInstanceId,
            cancellationToken);

        Assert.True(await second.WaitForStoppingAsync(TimeSpan.FromSeconds(10), cancellationToken));
        Assert.False(first.IsStopping);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClientReconnectsAfterTheHostRestarts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));

        await using (ControlPlaneHost first = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken))
        {
            Assert.Equal(
                ControlDiagnosticCode.None,
                (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
            Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
        }

        // The first Host stopped (and released the project lease); pinging the now-dead connection must fail,
        // and the client must not retry silently.
        await Assert.ThrowsAsync<ControlProtocolException>(() => client.PingAsync(cancellationToken));
        Assert.False(client.IsConnected);

        await using ControlPlaneHost second = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ANonStringCursorValueIsRejectedAsMalformedInsteadOfSilentlyReplayingFromScratch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.ReadControlEventsKind,
            JsonSerializer.SerializeToElement(new { cursor = 123 }),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.Malformed, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ANullCursorValueStillMeansNoCursorSupplied()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.ReadControlEventsKind,
            JsonSerializer.SerializeToElement(new { cursor = (string?)null }),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AJournalReadFailureReportsAnInternalErrorInsteadOfHangingTheConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            // FileShare.None sharing-violation enforcement (this test's fault-injection mechanism) is
            // reliably a hard failure only on Windows; .NET's FileStream does not emulate it on Unix.
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        string eventsPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "events.jsonl");
        await using FileStream exclusiveLock =
            new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.GetProjectSnapshotKind,
            JsonSerializer.SerializeToElement(new { detail = "full", sprint_id = sprintId.Value }),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.InternalError, response.Diagnostic.Code);

        // The connection itself must still be usable afterward — a locked file must not have killed
        // the connection or the Host.
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AGarbagePayloadAfterAValidHandshakeEndsOnlyThatConnectionWithoutAffectingTheHost()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();

        await using (ILocalControlConnection connection = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken))
        {
            ControlHandshakeRequest handshake =
                new(ControlProtocol.Version, "1.0.0-test", instanceId, [], Guid.NewGuid());
            await connection.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(handshake, ControlProtocol.JsonOptions),
                TimeSpan.FromSeconds(5),
                cancellationToken);
            await connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken);

            // A well-formed frame whose payload is not JSON at all — not even a malformed request
            // envelope, just garbage bytes.
            await connection.SendAsync(
                Encoding.UTF8.GetBytes("this is not json"),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            // The Host closes this one connection (no CorrelationId to reply to) instead of hanging
            // or letting the JsonException escape unobserved.
            await Assert.ThrowsAsync<ControlProtocolException>(
                () => connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        // The Host itself is unaffected: a fresh, well-behaved client is still served normally.
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AClientThatNeverHandshakesIsDroppedAfterItsDeadlineAndTheHostKeepsServing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            new ControlPlaneOptions(environment.ProjectRoot, instanceId, HandshakeTimeout: TimeSpan.FromMilliseconds(300)),
            cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();

        await using (ILocalControlConnection silent = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // Never sends a handshake. The Host's own receive deadline (300ms) fires and it closes
            // this connection server-side rather than holding it open indefinitely; that closure is
            // what unblocks this pending client-side receive.
            await Assert.ThrowsAsync<ControlProtocolException>(
                () => silent.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AClientThatDisconnectsWhileIdleDoesNotAffectOtherConnections()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClientOptions clientOptions = new(projectId, instanceId, "1.0.0-test");

        await using (ForgeHostClient doomed = new(new NamedPipeControlTransport(), clientOptions))
        {
            Assert.Equal(
                ControlDiagnosticCode.None,
                (await doomed.EnsureConnectedAsync(null, cancellationToken)).Code);
            Assert.Equal(ControlDiagnosticCode.None, (await doomed.PingAsync(cancellationToken)).Diagnostic.Code);
        }

        // "doomed" disposed abruptly (simulating a crashed client) while the Host was idly waiting
        // for its next request; the Host must notice the closed connection and move on rather than
        // leaking a stuck serving task or affecting anyone else.
        await using ForgeHostClient client = new(new NamedPipeControlTransport(), clientOptions);
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
        Assert.False(host.IsStopping);
    }
}

/// <summary>Runs <see cref="ControlPlaneHostedService"/> in-process against a real project directory for tests.</summary>
internal sealed class ControlPlaneHost : IAsyncDisposable
{
    private readonly IHost host;

    private ControlPlaneHost(IHost host) => this.host = host;

    /// <summary>True once <see cref="ControlPlaneHostedService"/> has called <c>StopApplication</c> — e.g. it lost the lease.</summary>
    public bool IsStopping =>
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

    /// <summary>Polls <see cref="IsStopping"/> until it turns true or <paramref name="timeout"/> elapses.</summary>
    public async Task<bool> WaitForStoppingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (!IsStopping)
            {
                await Task.Delay(20, linked.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    public static Task<ControlPlaneHost> StartAsync(
        string projectRoot,
        string instanceId,
        CancellationToken cancellationToken) =>
        StartAsync(new ControlPlaneOptions(projectRoot, instanceId), cancellationToken);

    public static async Task<ControlPlaneHost> StartAsync(
        ControlPlaneOptions options,
        CancellationToken cancellationToken)
    {
        IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(options);
                // Matches Forge.Host/Program.cs: without this override, every in-process test Host
                // would share one Debug-build IEnvironmentPaths instead of this test's own ephemeral
                // instance id, writing into the real developer machine's %LOCALAPPDATA%.
                services.AddSingleton<IEnvironmentPaths>(new SystemEnvironmentPaths(options.InstanceId));
                services.AddHostedService<ControlPlaneHostedService>();
            })
            .Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        return new ControlPlaneHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        host.Dispose();
    }
}
