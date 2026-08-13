using System.Text.Json;

namespace Forge.Host.Client;

public sealed record ForgeHostClientOptions(
    Guid ProjectId,
    string InstanceId,
    string ClientVersion,
    IReadOnlyList<string>? Capabilities = null,
    TimeSpan? ConnectTimeout = null,
    TimeSpan? HandshakeTimeout = null,
    TimeSpan? RequestTimeout = null);

/// <summary>Launches the Host process when discovery finds nothing listening. Supplied by the composition root.</summary>
public delegate Task StartHostAsync(CancellationToken cancellationToken);

/// <summary>
/// The client SDK: connects to a project's Host, handshakes, and sends framed requests, all with bounded
/// deadlines and stable diagnostics. Discovery is a bounded connect attempt; a caller that wants "start if not
/// running" supplies <see cref="StartHostAsync"/> to <see cref="EnsureConnectedAsync"/>. A dropped connection is
/// never retried silently — the next <see cref="EnsureConnectedAsync"/> call reconnects explicitly.
/// </summary>
public sealed class ForgeHostClient(ILocalControlTransport transport, ForgeHostClientOptions options)
    : IAsyncDisposable
{
    private readonly ILocalControlTransport transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly ForgeHostClientOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly string endpointName = InstanceIdentity.ComputePipeName(options.InstanceId, options.ProjectId);
    private static readonly TimeSpan[] StartBackoff =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    private ILocalControlConnection? connection;

    public bool IsConnected => connection is not null;

    public async Task<ControlDiagnostic> EnsureConnectedAsync(
        StartHostAsync? startHost,
        CancellationToken cancellationToken)
    {
        if (connection is not null)
        {
            return ControlDiagnostic.None;
        }

        ControlDiagnostic diagnostic = await TryHandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (diagnostic.Code != ControlDiagnosticCode.Unavailable || startHost is null)
        {
            return diagnostic;
        }

        await startHost(cancellationToken).ConfigureAwait(false);

        // The Host takes a moment to start listening after the process launches; poll with a short backoff
        // instead of a single immediate retry.
        foreach (TimeSpan delay in StartBackoff)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            diagnostic = await TryHandshakeAsync(cancellationToken).ConfigureAwait(false);
            if (diagnostic.Code != ControlDiagnosticCode.Unavailable)
            {
                break;
            }
        }

        return diagnostic;
    }

    public async Task<ControlResponse> SendAsync(
        string kind,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (connection is null)
        {
            throw new InvalidOperationException("Call EnsureConnectedAsync before sending a request.");
        }

        TimeSpan requestTimeout = options.RequestTimeout ?? TimeSpan.FromSeconds(30);
        ControlRequest request = new(kind, Guid.NewGuid(), payload);
        try
        {
            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, ControlProtocol.JsonOptions);
            await connection.SendAsync(requestBytes, requestTimeout, cancellationToken).ConfigureAwait(false);
            byte[] responseBytes = await connection.ReceiveAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ControlResponse>(responseBytes, ControlProtocol.JsonOptions) ??
                throw new ControlProtocolException(ControlDiagnosticCode.Malformed, "The response was empty.");
        }
        catch (ControlProtocolException)
        {
            // A framing/timeout failure leaves the connection unusable; the next EnsureConnectedAsync reconnects.
            await DropConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<ControlResponse> PingAsync(CancellationToken cancellationToken) =>
        SendAsync(ControlProtocol.PingKind, null, cancellationToken);

    public async ValueTask DisposeAsync() => await DropConnectionAsync().ConfigureAwait(false);

    private async Task<ControlDiagnostic> TryHandshakeAsync(CancellationToken cancellationToken)
    {
        TimeSpan connectTimeout = options.ConnectTimeout ?? TimeSpan.FromSeconds(5);
        TimeSpan handshakeTimeout = options.HandshakeTimeout ?? TimeSpan.FromSeconds(5);
        ILocalControlConnection candidate;
        try
        {
            candidate = await transport.ConnectAsync(endpointName, connectTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ControlProtocolException exception)
        {
            return new ControlDiagnostic(exception.Code, exception.Message);
        }

        try
        {
            ControlHandshakeRequest request = new(
                ControlProtocol.Version,
                options.ClientVersion,
                options.InstanceId,
                options.Capabilities ?? [],
                Guid.NewGuid());
            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, ControlProtocol.JsonOptions);
            await candidate.SendAsync(requestBytes, handshakeTimeout, cancellationToken).ConfigureAwait(false);
            byte[] responseBytes = await candidate.ReceiveAsync(handshakeTimeout, cancellationToken)
                .ConfigureAwait(false);
            ControlHandshakeResponse response =
                JsonSerializer.Deserialize<ControlHandshakeResponse>(responseBytes, ControlProtocol.JsonOptions) ??
                    throw new ControlProtocolException(ControlDiagnosticCode.Malformed, "The handshake response was empty.");

            if (response.Diagnostic.Code != ControlDiagnosticCode.None)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                return response.Diagnostic;
            }

            connection = candidate;
            return ControlDiagnostic.None;
        }
        catch (ControlProtocolException exception)
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            return new ControlDiagnostic(exception.Code, exception.Message);
        }
        catch (JsonException exception)
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            return new ControlDiagnostic(ControlDiagnosticCode.Malformed, exception.Message);
        }
    }

    private async Task DropConnectionAsync()
    {
        if (connection is null)
        {
            return;
        }

        ILocalControlConnection dropped = connection;
        connection = null;
        await dropped.DisposeAsync().ConfigureAwait(false);
    }
}
