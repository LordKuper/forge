using System.Text.Json;
using Forge.Host.Client;

namespace Forge.UnitTests;

/// <summary>
/// Proves <see cref="ForgeHostClient"/> validates the handshake response itself instead of trusting
/// it at face value — regression coverage for the 2026-08-15 audit's P8.9-P8.17 finding that neither
/// the echoed correlation id nor the Host's claimed protocol version was ever checked.
/// </summary>
public sealed class ForgeHostClientHandshakeTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureConnectedRejectsAHandshakeResponseWithAMismatchedCorrelationId()
    {
        // A fixed correlation id that (with overwhelming probability) never equals whatever GUID
        // the client generates for its own request — proving the client actually compares them
        // instead of accepting any well-formed response.
        EchoingFakeConnection connection = new(_ => new ControlHandshakeResponse(
            ControlProtocol.Version,
            "1.0.0",
            [],
            ControlDiagnostic.None,
            Guid.NewGuid()));
        await using ForgeHostClient client = new(
            new FakeControlTransport(connection),
            new ForgeHostClientOptions(Guid.NewGuid(), "test-instance", "1.0.0-test"));

        ControlDiagnostic diagnostic = await client.EnsureConnectedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ControlDiagnosticCode.Malformed, diagnostic.Code);
        Assert.False(client.IsConnected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureConnectedRejectsAnIncompatibleProtocolVersionEvenWhenTheHostReportsNoDiagnostic()
    {
        // A misbehaving or corrupted Host could report Diagnostic.Code == None while echoing a
        // protocol version this client doesn't actually support — the client must catch this
        // independently rather than trusting the Host's own self-reported diagnostic.
        EchoingFakeConnection connection = new(request => new ControlHandshakeResponse(
            "99.0.0",
            "1.0.0",
            [],
            ControlDiagnostic.None,
            request.CorrelationId));
        await using ForgeHostClient client = new(
            new FakeControlTransport(connection),
            new ForgeHostClientOptions(Guid.NewGuid(), "test-instance", "1.0.0-test"));

        ControlDiagnostic diagnostic = await client.EnsureConnectedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ControlDiagnosticCode.VersionIncompatible, diagnostic.Code);
        Assert.False(client.IsConnected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureConnectedAcceptsAWellFormedResponseThatEchoesTheRequestCorrectly()
    {
        EchoingFakeConnection connection = new(request => new ControlHandshakeResponse(
            ControlProtocol.Version,
            "1.0.0",
            [],
            ControlDiagnostic.None,
            request.CorrelationId));
        await using ForgeHostClient client = new(
            new FakeControlTransport(connection),
            new ForgeHostClientOptions(Guid.NewGuid(), "test-instance", "1.0.0-test"));

        ControlDiagnostic diagnostic = await client.EnsureConnectedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, diagnostic.Code);
        Assert.True(client.IsConnected);
    }

    /// <summary>Builds its handshake response lazily from the actual request the client sent, so a
    /// test can echo the real correlation id back correctly while still controlling every other
    /// field.</summary>
    private sealed class EchoingFakeConnection(Func<ControlHandshakeRequest, ControlHandshakeResponse> buildResponse)
        : ILocalControlConnection
    {
        private byte[]? pendingResponseBytes;

        public Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan deadline, CancellationToken cancellationToken)
        {
            ControlHandshakeRequest request =
                JsonSerializer.Deserialize<ControlHandshakeRequest>(message.Span, ControlProtocol.JsonOptions)!;
            pendingResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
                buildResponse(request),
                ControlProtocol.JsonOptions);
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
