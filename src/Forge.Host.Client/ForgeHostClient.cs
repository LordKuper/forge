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
public sealed class ForgeHostClient : IAsyncDisposable
{
    // A listening pipe accepts near-instantly; only "nothing is listening" makes ConnectAsync wait out its
    // deadline. Discovery/backoff probes use this short timeout instead of options.ConnectTimeout so a cold
    // start (no Host yet) fails fast into the start-and-poll path rather than blocking for seconds per attempt.
    private static readonly TimeSpan DiscoveryProbeTimeout = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan[] StartBackoff =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    private readonly ILocalControlTransport transport;
    private readonly ForgeHostClientOptions options;
    private readonly string endpointName;
    private ILocalControlConnection? connection;

    public ForgeHostClient(ILocalControlTransport transport, ForgeHostClientOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        endpointName = InstanceIdentity.ComputePipeName(this.options.InstanceId, this.options.ProjectId);
    }

    public bool IsConnected => connection is not null;

    /// <summary>The project this client's Host connection is scoped to (PR #106 review finding 5):
    /// the same id <see cref="InstanceIdentity.ComputePipeName"/> already keys the pipe endpoint by,
    /// so any caller that needs to attribute an observation to "this client's Host" (e.g. connectivity
    /// reporting) uses the same identity the connection itself is already scoped to.</summary>
    public Guid ProjectId => options.ProjectId;

    /// <summary>The Host's own handshake-advertised capability set (<c>CapabilityIds.Implemented</c>
    /// on whichever version answered) — empty until a handshake succeeds, and refreshed by every
    /// subsequent one. Never the client's own requested set: a caller that wants to know what this
    /// Host actually supports before dispatching reads this, not <see cref="ForgeHostClientOptions.Capabilities"/>.</summary>
    public IReadOnlyList<string> HostCapabilities { get; private set; } = [];

    public async Task<ControlDiagnostic> EnsureConnectedAsync(
        StartHostAsync? startHost,
        CancellationToken cancellationToken)
    {
        if (connection is not null)
        {
            return ControlDiagnostic.None;
        }

        // Without a launcher there is nothing to poll for; give this single attempt the caller's full timeout.
        TimeSpan firstAttemptTimeout = startHost is null
            ? options.ConnectTimeout ?? TimeSpan.FromSeconds(5)
            : DiscoveryProbeTimeout;
        ControlDiagnostic diagnostic = await TryHandshakeAsync(firstAttemptTimeout, cancellationToken)
            .ConfigureAwait(false);
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
            diagnostic = await TryHandshakeAsync(DiscoveryProbeTimeout, cancellationToken).ConfigureAwait(false);
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

    private async Task<ControlDiagnostic> TryHandshakeAsync(TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
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

            // Never trust ANY field on the response — including its own Diagnostic — until it's
            // confirmed to actually answer THIS handshake. Checking Diagnostic first would let a
            // response that doesn't correlate (stale, foreign, or forged) stand unexamined as long as
            // it carried a plausible-looking diagnostic.
            if (response.CorrelationId != request.CorrelationId)
            {
                return await RejectAsync(
                        candidate,
                        new ControlDiagnostic(
                            ControlDiagnosticCode.Malformed,
                            "The handshake response's correlation id did not match the request."))
                    .ConfigureAwait(false);
            }

            if (response.Diagnostic.Code != ControlDiagnosticCode.None)
            {
                return await RejectAsync(candidate, response.Diagnostic).ConfigureAwait(false);
            }

            // The Host's own claimed protocol version must actually be one this client understands,
            // checked independently of its Diagnostic field, which a misbehaving or corrupted Host
            // could report as None while echoing an incompatible — or missing/blank, which
            // IsCompatible itself would reject with an ArgumentException rather than a diagnostic —
            // version.
            if (string.IsNullOrWhiteSpace(response.ProtocolVersion) ||
                !ControlProtocol.IsCompatible(response.ProtocolVersion, ControlProtocol.Version))
            {
                return await RejectAsync(
                        candidate,
                        new ControlDiagnostic(
                            ControlDiagnosticCode.VersionIncompatible,
                            $"This client supports protocol {ControlProtocol.Version}; the Host reported '{response.ProtocolVersion}'."))
                    .ConfigureAwait(false);
            }

            connection = candidate;
            // Never trust ANY field on the response (see above): System.Text.Json passes a JSON
            // `null`/absent `capabilities` straight through this non-nullable positional-record
            // parameter at runtime, regardless of the declared type. Coalescing here keeps
            // HostCapabilities.Contains callers (RemoteForgeMutations.SendAsync) safe from a
            // NullReferenceException a malformed or differently-versioned Host could trigger.
            HostCapabilities = response.Capabilities ?? [];
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
        catch
        {
            // A cancellation (or anything else unanticipated) mid-handshake must not leak the connection.
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A rejected handshake never keeps its candidate connection — every rejection branch in
    /// <see cref="TryHandshakeAsync"/> routes through this so the dispose-then-return pairing can't
    /// drift out of sync as branches are added.</summary>
    private static async Task<ControlDiagnostic> RejectAsync(
        ILocalControlConnection candidate,
        ControlDiagnostic diagnostic)
    {
        await candidate.DisposeAsync().ConfigureAwait(false);
        return diagnostic;
    }

    private async Task DropConnectionAsync()
    {
        if (connection is null)
        {
            return;
        }

        ILocalControlConnection dropped = connection;
        connection = null;
        // A stale capability set from a since-dropped connection must never outlive it -- the next
        // successful handshake (possibly against a different Host) repopulates this fresh.
        HostCapabilities = [];
        await dropped.DisposeAsync().ConfigureAwait(false);
    }
}
