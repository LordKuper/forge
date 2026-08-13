using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Host;
using Forge.Host.Client;
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

    public static async Task<ControlPlaneHost> StartAsync(
        string projectRoot,
        string instanceId,
        CancellationToken cancellationToken)
    {
        IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new ControlPlaneOptions(projectRoot, instanceId));
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
