using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Host.Client;
using Forge.Presentation;

namespace Forge.UnitTests;

/// <summary>
/// Plan 12.6's Host-connectivity status-row indicator is sourced from <see cref="HostConnectivityMonitor"/>,
/// which <see cref="RemoteForgeMutations"/> reports into after every real connection attempt (see that
/// type's own remarks: it is never a fresh probe, only ever the outcome of an actual mutation). These
/// tests prove the wiring end to end -- a successful connection reports <see langword="true"/>, an
/// unreachable Host reports <see langword="false"/> -- rather than trusting the monitor is ever
/// actually fed real data.
/// </summary>
public sealed class RemoteForgeMutationsConnectivityTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReportsConnectedIntoTheMonitorWhenAMutationSucceeds()
    {
        SucceedingConnection connection = new();
        HostConnectivityMonitor monitor = new();
        Guid projectId = Guid.NewGuid();
        await using RemoteForgeMutations mutations = CreateMutations(connection, monitor, projectId);

        await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.NotNull(monitor.LastObserved(projectId));
        Assert.True(monitor.LastObserved(projectId)!.Connected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReportsDisconnectedIntoTheMonitorWhenTheHostIsUnreachable()
    {
        HostConnectivityMonitor monitor = new();
        Guid projectId = Guid.NewGuid();
        ForgeHostClient client = new(
            new UnreachableTransport(),
            new ForgeHostClientOptions(projectId, "test-instance", "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client, connectivityMonitor: monitor);

        await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.NotNull(monitor.LastObserved(projectId));
        Assert.False(monitor.LastObserved(projectId)!.Connected);
    }

    /// <summary>PR #106 review finding 3 (regression test): a framing/timeout failure on the SECOND
    /// request -- the actual mutation, sent after a successful connect/handshake -- used to bypass
    /// connectivity reporting entirely. <see cref="ForgeHostClient.SendAsync"/> drops the connection
    /// and rethrows a <see cref="ControlProtocolException"/>, and the pre-fix
    /// <see cref="RemoteForgeMutations.SetConfigurationAsync"/> caught that exception and returned
    /// without ever reporting into the monitor -- leaving it holding the earlier "connected" reading
    /// from <c>EnsureConnectedAsync</c>, so the status row would keep showing "Connected to Host." for
    /// up to the staleness window after the connection had actually died. This proves the fix: even
    /// though the connect/handshake itself succeeds, a subsequent send failure must still report
    /// <see langword="false"/>.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReportsDisconnectedIntoTheMonitorWhenTheConnectionDropsMidRequest()
    {
        FailingSendConnection connection = new();
        HostConnectivityMonitor monitor = new();
        Guid projectId = Guid.NewGuid();
        await using RemoteForgeMutations mutations = CreateMutations(connection, monitor, projectId);

        await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.NotNull(monitor.LastObserved(projectId));
        Assert.False(monitor.LastObserved(projectId)!.Connected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NeverThrowsWhenNoMonitorIsSupplied()
    {
        // The monitor parameter is optional (RemoteForgeMutations' own primary-constructor default):
        // a caller that does not care about connectivity reporting (e.g. the CLI) must keep working
        // exactly as before.
        SucceedingConnection connection = new();
        await using RemoteForgeMutations mutations = CreateMutations(connection, connectivityMonitor: null, Guid.NewGuid());

        ConfigurationWriteResult result = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    private static RemoteForgeMutations CreateMutations(
        ILocalControlConnection connection, IHostConnectivityMonitor? connectivityMonitor, Guid projectId)
    {
        ForgeHostClient client = new(
            new FakeControlTransport(connection),
            new ForgeHostClientOptions(projectId, "test-instance", "1.0.0-test"));
        return new RemoteForgeMutations(client, connectivityMonitor: connectivityMonitor);
    }

    /// <summary>Answers the handshake advertising every capability, then answers every mutation with
    /// a bare success payload -- this file only cares about connectivity reporting, not the mutation
    /// result's own content.</summary>
    private sealed class SucceedingConnection : ILocalControlConnection
    {
        private byte[]? pendingResponseBytes;
        private bool handshaked;

        public Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan deadline, CancellationToken cancellationToken)
        {
            if (!handshaked)
            {
                ControlHandshakeRequest request =
                    JsonSerializer.Deserialize<ControlHandshakeRequest>(message.Span, ControlProtocol.JsonOptions)!;
                pendingResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ControlHandshakeResponse(
                        ControlProtocol.Version, "1.0.0-test", CapabilityIds.Implemented, ControlDiagnostic.None,
                        request.CorrelationId),
                    ControlProtocol.JsonOptions);
                handshaked = true;
                return Task.CompletedTask;
            }

            ControlRequest realRequest =
                JsonSerializer.Deserialize<ControlRequest>(message.Span, ControlProtocol.JsonOptions)!;
            pendingResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                new ControlResponse(
                    realRequest.CorrelationId,
                    ControlDiagnostic.None,
                    JsonSerializer.SerializeToElement(
                        new ConfigurationWriteResult(true, DiagnosticCodes.None), ControlProtocol.JsonOptions)),
                ControlProtocol.JsonOptions);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReceiveAsync(TimeSpan deadline, CancellationToken cancellationToken) =>
            Task.FromResult(pendingResponseBytes ??
                throw new InvalidOperationException("No request was sent before receiving."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Answers the handshake advertising every capability -- same as
    /// <see cref="SucceedingConnection"/> -- but throws a <see cref="ControlProtocolException"/> on
    /// the SECOND request (the actual mutation), simulating a framing/timeout failure mid-request
    /// (PR #106 review finding 3): the connection was genuinely usable a moment ago, then drops.</summary>
    private sealed class FailingSendConnection : ILocalControlConnection
    {
        private byte[]? pendingResponseBytes;
        private bool handshaked;

        public Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan deadline, CancellationToken cancellationToken)
        {
            if (!handshaked)
            {
                ControlHandshakeRequest request =
                    JsonSerializer.Deserialize<ControlHandshakeRequest>(message.Span, ControlProtocol.JsonOptions)!;
                pendingResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ControlHandshakeResponse(
                        ControlProtocol.Version, "1.0.0-test", CapabilityIds.Implemented, ControlDiagnostic.None,
                        request.CorrelationId),
                    ControlProtocol.JsonOptions);
                handshaked = true;
                return Task.CompletedTask;
            }

            throw new ControlProtocolException(ControlDiagnosticCode.Timeout, "Simulated mid-request connection drop.");
        }

        public Task<byte[]> ReceiveAsync(TimeSpan deadline, CancellationToken cancellationToken) =>
            Task.FromResult(pendingResponseBytes ??
                throw new InvalidOperationException("No request was sent before receiving."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeControlTransport(ILocalControlConnection connection) : ILocalControlTransport
    {
        public Task<ILocalControlConnection> ConnectAsync(
            string endpointName, TimeSpan deadline, CancellationToken cancellationToken) =>
            Task.FromResult(connection);

        public ILocalControlListener CreateListener(string endpointName) =>
            throw new NotSupportedException("This fake only exercises the client's connect path.");
    }

    /// <summary>Simulates "nothing is listening" -- the exact condition <see cref="ForgeHostClient"/>'s
    /// own remarks describe for a cold, not-yet-started Host.</summary>
    private sealed class UnreachableTransport : ILocalControlTransport
    {
        public Task<ILocalControlConnection> ConnectAsync(
            string endpointName, TimeSpan deadline, CancellationToken cancellationToken) =>
            throw new ControlProtocolException(ControlDiagnosticCode.Unavailable, "No Host is listening.");

        public ILocalControlListener CreateListener(string endpointName) =>
            throw new NotSupportedException("This fake only exercises the client's connect path.");
    }
}
