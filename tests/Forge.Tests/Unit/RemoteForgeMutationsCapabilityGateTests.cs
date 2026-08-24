using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Host.Client;
using Forge.Presentation;

namespace Forge.UnitTests;

/// <summary>
/// ADR 0053: proves <see cref="RemoteForgeMutations"/> actually enforces capability negotiation --
/// a request whose governing capability is absent from the connected Host's handshake-advertised set
/// never reaches the wire at all, and a request whose capability is present behaves exactly as before.
/// </summary>
public sealed class RemoteForgeMutationsCapabilityGateTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RejectsARequestClientSideWhenTheConnectedHostDoesNotAdvertiseItsCapability()
    {
        // An older Host that has not yet learned `configuration.manage` -- the handshake succeeds,
        // but its advertised set is empty.
        SequencedFakeConnection connection = new([]);
        await using RemoteForgeMutations mutations = CreateMutations(connection);

        ConfigurationWriteResult result = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.CapabilityNotSupported, result.DiagnosticCode);
        // The gate gets checked after the handshake but the mutation request itself must never be
        // sent -- proving this is client-side rejection, not the Host's own generic "unknown kind".
        Assert.Equal(0, connection.RequestCount);
    }

    /// <summary>Round 1 review of PR #102: a malformed or differently-versioned Host answering the
    /// handshake with a JSON `null` (or absent) `capabilities` field must not crash. System.Text.Json
    /// passes that `null` straight through <see cref="ControlHandshakeResponse.Capabilities"/>'s
    /// non-nullable positional-record parameter at runtime, so before this fix
    /// <see cref="ForgeHostClient.HostCapabilities"/> stored `null` and this call's
    /// <c>HostCapabilities.Contains(...)</c> gate threw an uncaught <see cref="ArgumentNullException"/>
    /// -- breaking <see cref="RemoteForgeMutations"/>'s own documented "never a thrown exception a
    /// caller must specifically catch" contract.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReturnsCapabilityNotSupportedInsteadOfCrashingWhenTheHandshakeResponseCapabilitiesFieldIsNull()
    {
        SequencedFakeConnection connection = new(null);
        await using RemoteForgeMutations mutations = CreateMutations(connection);

        ConfigurationWriteResult result = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.CapabilityNotSupported, result.DiagnosticCode);
        Assert.Equal(0, connection.RequestCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProceedsWhenTheConnectedHostAdvertisesTheGoverningCapability()
    {
        SequencedFakeConnection connection = new(
            CapabilityIds.Implemented,
            request => new ControlResponse(
                request.CorrelationId,
                ControlDiagnostic.None,
                JsonSerializer.SerializeToElement(
                    new ConfigurationWriteResult(true, DiagnosticCodes.None), ControlProtocol.JsonOptions)));
        await using RemoteForgeMutations mutations = CreateMutations(connection);

        ConfigurationWriteResult result = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project, "root", "some.key", "value", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
        Assert.Equal(1, connection.RequestCount);
    }

    private static RemoteForgeMutations CreateMutations(SequencedFakeConnection connection)
    {
        ForgeHostClient client = new(
            new FakeControlTransport(connection),
            new ForgeHostClientOptions(Guid.NewGuid(), "test-instance", "1.0.0-test"));
        return new RemoteForgeMutations(client);
    }

    /// <summary>Answers the handshake first (echoing whichever capability list the test supplies --
    /// `null` simulates a Host whose handshake response omits or nulls the field), then answers every
    /// later request with <paramref name="buildRequestResponse"/> -- defaulted to a value that must
    /// never actually be invoked, since a capability-gated test never expects its request to reach
    /// this far.</summary>
    private sealed class SequencedFakeConnection(
        IReadOnlyList<string>? hostCapabilities,
        Func<ControlRequest, ControlResponse>? buildRequestResponse = null)
        : ILocalControlConnection
    {
        private readonly Func<ControlRequest, ControlResponse> buildRequestResponse = buildRequestResponse ??
            (_ => throw new InvalidOperationException("This test never expected a request to reach the wire."));
        private byte[]? pendingResponseBytes;
        private bool handshaked;

        public int RequestCount { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan deadline, CancellationToken cancellationToken)
        {
            if (!handshaked)
            {
                ControlHandshakeRequest request =
                    JsonSerializer.Deserialize<ControlHandshakeRequest>(message.Span, ControlProtocol.JsonOptions)!;
                pendingResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new ControlHandshakeResponse(
                        ControlProtocol.Version, "1.0.0-test", hostCapabilities!, ControlDiagnostic.None,
                        request.CorrelationId),
                    ControlProtocol.JsonOptions);
                handshaked = true;
                return Task.CompletedTask;
            }

            RequestCount++;
            ControlRequest realRequest =
                JsonSerializer.Deserialize<ControlRequest>(message.Span, ControlProtocol.JsonOptions)!;
            pendingResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                buildRequestResponse(realRequest), ControlProtocol.JsonOptions);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReceiveAsync(TimeSpan deadline, CancellationToken cancellationToken) =>
            Task.FromResult(pendingResponseBytes ??
                throw new InvalidOperationException("No request was sent before receiving."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeControlTransport(ILocalControlConnection connection) : ILocalControlTransport
    {
        public Task<ILocalControlConnection> ConnectAsync(
            string endpointName,
            TimeSpan deadline,
            CancellationToken cancellationToken) =>
            Task.FromResult(connection);

        public ILocalControlListener CreateListener(string endpointName) =>
            throw new NotSupportedException("This fake only exercises the client's connect path.");
    }
}
