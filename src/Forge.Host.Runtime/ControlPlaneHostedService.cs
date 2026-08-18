using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Host.Client;
using Forge.Presentation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;

namespace Forge.Host;

/// <summary>
/// <paramref name="HandshakeTimeout"/> bounds how long a connected-but-silent client may take to
/// send its handshake; <paramref name="RequestTimeout"/> bounds how long an idle, already-handshaken
/// client may go without sending a request. Both default to their production values and exist as
/// overrides so a test can prove the timeout path itself fires without waiting out the real
/// deadline.
/// </summary>
public sealed record ControlPlaneOptions(
    string ProjectRoot,
    string InstanceId,
    TimeSpan? HandshakeTimeout = null,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan Handshake => HandshakeTimeout ?? TimeSpan.FromSeconds(10);

    public TimeSpan Request => RequestTimeout ?? TimeSpan.FromMinutes(5);
}

/// <summary>
/// The Host's control plane: acquires the project lease, listens for connections, and serves the handshake plus
/// (for this stage) a minimal <c>ping</c> request. Command/query dispatch onto <see cref="ForgeApplication"/>
/// lands with the snapshot/events work later in Stage 8; this proves the transport, lease, and protocol end to end.
/// </summary>
public sealed class ControlPlaneHostedService(
    ControlPlaneOptions options,
    IConfigurationRegistry registry,
    ForgeApplication application,
    ResumeSchedulerHostedService resumeScheduler,
    IHostApplicationLifetime lifetime,
    ILogger<ControlPlaneHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception> LogNotInitialized = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2000, "ControlPlaneNotInitialized"),
        "Cannot start the control plane: the project is not initialized.");

    private static readonly Action<ILogger, Guid, Exception?> LogProjectInUse = LoggerMessage.Define<Guid>(
        LogLevel.Error,
        new EventId(2001, "ControlPlaneProjectInUse"),
        "Another Host already owns project {ProjectId}.");

    private static readonly Action<ILogger, Guid, Exception?> LogLeaseAbandoned = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2002, "ControlPlaneLeaseAbandoned"),
        "Recovered an abandoned project lease for {ProjectId}; durable state will be re-validated.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogListening =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(2003, "ControlPlaneListening"),
            "Forge Host listening for project {ProjectId} on instance {InstanceId}.");

    private static readonly Action<ILogger, Exception> LogHandshakeIncomplete = LoggerMessage.Define(
        LogLevel.Debug,
        new EventId(2004, "ControlPlaneHandshakeIncomplete"),
        "The handshake did not complete.");

    private static readonly Action<ILogger, Exception> LogConnectionEnded = LoggerMessage.Define(
        LogLevel.Debug,
        new EventId(2005, "ControlPlaneConnectionEnded"),
        "A control connection ended.");

    private static readonly Action<ILogger, string, Exception> LogDispatchFailed = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(2006, "ControlPlaneDispatchFailed"),
        "Serving a '{Kind}' request failed.");

    private readonly ConcurrentDictionary<Task, byte> activeConnections = new();
    private MutexProjectLease? lease;
    private ILocalControlListener? listener;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid projectId;
        try
        {
            projectId = await ProjectIdentity.ReadProjectIdAsync(options.ProjectRoot, registry, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is YamlException or InvalidDataException or FormatException
            or ConfigurationScopeException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // Matches ProjectRootResolver's manifest-read failure filter, plus InvalidOperationException: that's
            // what ProjectIdentity.ReadProjectIdAsync itself throws for an initialized project missing a project
            // ID. Every case here means the same thing: this project cannot be served. Report it the same way
            // instead of letting an exception type this handler didn't anticipate crash the process via the
            // generic unhandled-BackgroundService-exception path.
            LogNotInitialized(logger, exception);
            lifetime.StopApplication();
            return;
        }

        // ADR 0005 says a second Host "may read but returns project_in_use for mutation." This stage's Host
        // dispatches no mutating command yet (see Dispatch below), and a losing Host cannot safely become a
        // second listener on the same pipe name without risking a client's connection landing on either
        // process nondeterministically — so for now the losing Host exits without listening at all, rather
        // than half-implementing the read path. Serving reads from a lease-less Host is deferred to the
        // multi-host isolation slice (P8.34-P8.41), which is where that behavior gets real test coverage
        // (hostile clients, stale clients, crash recovery) instead of being bolted on here.
        string leaseName = InstanceIdentity.ComputeLeaseName(projectId);
        lease = MutexProjectLease.TryAcquire(leaseName, TimeSpan.FromSeconds(2));
        if (lease is null)
        {
            LogProjectInUse(logger, projectId, null);
            lifetime.StopApplication();
            return;
        }

        if (lease.WasAbandoned)
        {
            LogLeaseAbandoned(logger, projectId, null);
        }

        // Only starts once this Host has won the project lease above — never on a losing Host,
        // which returns before this point and never mutates durable state.
        await resumeScheduler.StartAsync(stoppingToken).ConfigureAwait(false);

        string pipeName = InstanceIdentity.ComputePipeName(options.InstanceId, projectId);
        NamedPipeControlTransport transport = new();
        listener = transport.CreateListener(pipeName);
        LogListening(logger, projectId, options.InstanceId, null);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ILocalControlConnection connection;
                try
                {
                    connection = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                Task serving = ServeConnectionAsync(connection, stoppingToken);
                activeConnections[serving] = 0;
                _ = serving.ContinueWith(
                    completed => activeConnections.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // A no-op if it was never started (this Host lost the lease race above).
        await resumeScheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(activeConnections.Keys).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // Best-effort drain; a client mid-request during shutdown reconnects and retries.
        }

        lease?.Dispose();
    }

    private async Task ServeConnectionAsync(ILocalControlConnection connection, CancellationToken cancellationToken)
    {
        await using ILocalControlConnection scoped = connection;
        try
        {
            if (!await HandshakeAsync(scoped, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] requestBytes = await scoped.ReceiveAsync(options.Request, cancellationToken)
                    .ConfigureAwait(false);
                ControlRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<ControlRequest>(requestBytes, ControlProtocol.JsonOptions);
                }
                catch (JsonException exception)
                {
                    // A request envelope that isn't valid JSON at all carries no CorrelationId to
                    // reply to — the same fail-closed outcome an empty/garbage handshake gets below,
                    // rather than letting the exception escape this connection's serving task
                    // unobserved (see DispatchAsync's own IOException/UnauthorizedAccessException
                    // widening for the same class of gap on the payload side).
                    LogConnectionEnded(logger, exception);
                    return;
                }

                if (request is null)
                {
                    return;
                }

                ControlResponse response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, ControlProtocol.JsonOptions);
                await scoped.SendAsync(responseBytes, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (ControlProtocolException exception)
        {
            LogConnectionEnded(logger, exception);
        }
    }

    private async Task<bool> HandshakeAsync(ILocalControlConnection connection, CancellationToken cancellationToken)
    {
        ControlHandshakeRequest? request;
        try
        {
            byte[] requestBytes = await connection.ReceiveAsync(options.Handshake, cancellationToken)
                .ConfigureAwait(false);
            request = JsonSerializer.Deserialize<ControlHandshakeRequest>(requestBytes, ControlProtocol.JsonOptions);
        }
        catch (Exception exception) when (exception is ControlProtocolException or JsonException)
        {
            // A garbage (non-JSON) first message gets the same fail-closed outcome as an
            // incomplete/timed-out handshake — never an unobserved exception on the fire-and-forget
            // serving task (see DispatchAsync's and the request loop's identical widening above).
            LogHandshakeIncomplete(logger, exception);
            return false;
        }

        ControlDiagnostic diagnostic = request is null
            ? new(ControlDiagnosticCode.Malformed, "The handshake request could not be parsed.")
            : ControlProtocol.IsCompatible(request.ProtocolVersion, ControlProtocol.Version)
                ? ControlDiagnostic.None
                : new(
                    ControlDiagnosticCode.VersionIncompatible,
                    $"This Host supports protocol {ControlProtocol.Version}.");

        ControlHandshakeResponse response = new(
            ControlProtocol.Version,
            typeof(ControlPlaneHostedService).Assembly.GetName().Version!.ToString(3),
            // Always this Host's own real capability set — never gated on what the client declared.
            // There is no negotiation logic yet that filters by the client's list; advertising the
            // Host's actual capabilities is what closes the "always returns []" gap, and gives a
            // future client something real to check before assuming a capability is available.
            CapabilityIds.Implemented,
            diagnostic,
            request?.CorrelationId ?? Guid.Empty);
        byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, ControlProtocol.JsonOptions);
        await connection.SendAsync(responseBytes, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return diagnostic.Code == ControlDiagnosticCode.None;
    }

    private async Task<ControlResponse> DispatchAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Kind switch
            {
                ControlProtocol.PingKind => new ControlResponse(request.CorrelationId, ControlDiagnostic.None),
                ControlProtocol.GetProjectSnapshotKind =>
                    await DispatchGetProjectSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.ReadControlEventsKind =>
                    await DispatchReadControlEventsAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.RecoverStartupKind =>
                    await DispatchRecoverStartupAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.SetConfigurationKind =>
                    await DispatchSetConfigurationAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.InstallIntegrationKind =>
                    await DispatchInstallIntegrationAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.RemoveIntegrationKind =>
                    await DispatchRemoveIntegrationAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.ResolveGateKind =>
                    await DispatchResolveGateAsync(request, cancellationToken).ConfigureAwait(false),
                ControlProtocol.SupersedeAttemptKind =>
                    await DispatchSupersedeAttemptAsync(request, cancellationToken).ConfigureAwait(false),
                _ => new ControlResponse(
                    request.CorrelationId,
                    new ControlDiagnostic(ControlDiagnosticCode.Malformed, $"Unknown request kind '{request.Kind}'.")),
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException)
        {
            // A malformed request payload (bad JSON shape, unparsable sprint id) is a client error,
            // never a reason to tear down the connection or crash the Host.
            return new ControlResponse(
                request.CorrelationId,
                new ControlDiagnostic(ControlDiagnosticCode.Malformed, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unlike the client-caused branch above, this means the Host itself failed to read its
            // own durable state (a locked or unreadable sprint journal) — never the client's fault.
            // The detail stays generic rather than echoing exception.Message: that message can
            // contain a local filesystem path, which the client-caused branch's messages never do.
            LogDispatchFailed(logger, request.Kind, exception);
            return new ControlResponse(
                request.CorrelationId,
                new ControlDiagnostic(ControlDiagnosticCode.InternalError, "The Host could not complete this request."));
        }
    }

    private async Task<ControlResponse> DispatchGetProjectSnapshotAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        SnapshotDetail detail = SnapshotDetail.Summary;
        Guid? sprintId = null;
        if (request.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            if (payload.TryGetProperty("detail", out JsonElement detailElement) &&
                detailElement.ValueKind == JsonValueKind.String)
            {
                detail = detailElement.GetString() switch
                {
                    "full" => SnapshotDetail.Full,
                    "summary" or null => SnapshotDetail.Summary,
                    string other => throw new InvalidDataException($"Unknown snapshot detail '{other}'."),
                };
            }

            if (payload.TryGetProperty("sprint_id", out JsonElement sprintIdElement) &&
                sprintIdElement.ValueKind == JsonValueKind.String)
            {
                sprintId = sprintIdElement.GetGuid();
            }
        }

        ProjectSnapshot snapshot = await application
            .GetProjectSnapshotAsync(options.ProjectRoot, detail, sprintId, cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(snapshot, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }

    private async Task<ControlResponse> DispatchReadControlEventsAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        if (request.Payload is { ValueKind: JsonValueKind.Object } payload &&
            payload.TryGetProperty("cursor", out JsonElement cursorElement) &&
            cursorElement.ValueKind != JsonValueKind.Null)
        {
            // A `cursor` field present with a non-string value (a number, an object, ...) is a
            // malformed request, not "no cursor supplied" — silently falling back to the latter
            // would make the Host quietly replay every event from the beginning instead of
            // reporting the request as invalid. The outer catch in DispatchAsync turns this into a
            // Malformed response.
            cursor = cursorElement.ValueKind == JsonValueKind.String
                ? cursorElement.GetString()
                : throw new InvalidDataException("The 'cursor' field must be a string.");
        }

        ControlEventsPage page = await application
            .ReadControlEventsAsync(options.ProjectRoot, cursor, cancellationToken)
            .ConfigureAwait(false);
        // A stale cursor is a business-level outcome the page's own `diagnostic_code` already
        // carries (matching how a blocked/failed startup travels inside ProjectSnapshot rather than
        // as a protocol diagnostic) — the request itself was well-formed and got a well-formed
        // response, so the transport-level diagnostic here stays None.
        using JsonDocument document = JsonDocument.Parse(ControlEventsJson.Serialize(page));
        return new(request.CorrelationId, ControlDiagnostic.None, document.RootElement.Clone());
    }

    private async Task<ControlResponse> DispatchRecoverStartupAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        RecoverStartupRequest? payload = request.Payload is { } value
            ? value.Deserialize<RecoverStartupRequest>(ControlProtocol.JsonOptions)
            : null;
        if (payload is null)
        {
            throw new InvalidDataException("The recover_startup payload is required.");
        }

        // Always this Host's own project — a project's Host never recovers another project, so the
        // request carries no project root of its own (matching GetProjectSnapshot/ReadControlEvents).
        RecoverStartupResult result = await application
            .RecoverStartupAsync(options.ProjectRoot, payload.Confirmed, cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(result, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }

    private async Task<ControlResponse> DispatchSetConfigurationAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        SetConfigurationRequest? payload = request.Payload is { } value
            ? value.Deserialize<SetConfigurationRequest>(ControlProtocol.JsonOptions)
            : null;
        if (payload is null)
        {
            throw new InvalidDataException("The set_configuration payload is required.");
        }

        if (payload.Scope != "project")
        {
            // User-scope configuration is not project state (ADR 0005 protects `.forge/`, not the
            // user's own config file) and is never routed through a project's Host — a client
            // sending one here has a routing bug, not a legitimate request.
            throw new InvalidDataException($"A project Host cannot set '{payload.Scope}'-scope configuration.");
        }

        if (payload.Key is null)
        {
            // The record's `Key` is non-nullable, but nothing stops a non-conforming client from
            // sending `"key": null` on the wire — reject it as malformed rather than passing null
            // into ForgeApplication.SetConfigurationAsync, whose own `key` parameter is not
            // null-checked (it always comes from a well-formed CLI Argument<string>).
            throw new InvalidDataException("The 'key' field is required.");
        }

        ConfigurationWriteResult result = await application
            .SetConfigurationAsync(
                ConfigurationScope.Project,
                options.ProjectRoot,
                payload.Key,
                payload.RawValue,
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(result, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }

    private async Task<ControlResponse> DispatchInstallIntegrationAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        IntegrationWriteRequest? payload = request.Payload is { } value
            ? value.Deserialize<IntegrationWriteRequest>(ControlProtocol.JsonOptions)
            : null;
        if (payload is null)
        {
            throw new InvalidDataException("The install_integration payload is required.");
        }

        // Always this Host's own project, matching recover_startup/set_configuration.
        IntegrationWriteResult result = await application
            .InstallIntegrationAsync(options.ProjectRoot, payload.Confirmed, cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(result, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }

    private async Task<ControlResponse> DispatchRemoveIntegrationAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        IntegrationWriteRequest? payload = request.Payload is { } value
            ? value.Deserialize<IntegrationWriteRequest>(ControlProtocol.JsonOptions)
            : null;
        if (payload is null)
        {
            throw new InvalidDataException("The remove_integration payload is required.");
        }

        IntegrationWriteResult result = await application
            .RemoveIntegrationAsync(options.ProjectRoot, payload.Confirmed, cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(result, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }

    private async Task<ControlResponse> DispatchResolveGateAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        ResolveGateRequest? payload = request.Payload is { } value
            ? value.Deserialize<ResolveGateRequest>(ControlProtocol.JsonOptions)
            : null;
        if (payload is null || payload.NodeId is null)
        {
            throw new InvalidDataException("The resolve_gate payload is required.");
        }

        // Always this Host's own project, matching every other mutation dispatch here; the sprint
        // and node it targets travel in the payload since a Host manages every sprint of its one
        // project, not just one.
        NodeActionResult result = await application
            .ResolveGateAsync(
                options.ProjectRoot, payload.SprintId, payload.NodeId, payload.Approved, payload.Confirmed,
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(result, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }

    private async Task<ControlResponse> DispatchSupersedeAttemptAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        SupersedeAttemptRequest? payload = request.Payload is { } value
            ? value.Deserialize<SupersedeAttemptRequest>(ControlProtocol.JsonOptions)
            : null;
        if (payload is null || payload.Instruction is null)
        {
            throw new InvalidDataException("The supersede_attempt payload is required.");
        }

        CompleteAttemptResult result = await application
            .SupersedeAttemptAsync(
                options.ProjectRoot, payload.SprintId, payload.AttemptId, payload.Instruction, payload.Confirmed,
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement responsePayload = JsonSerializer.SerializeToElement(result, StatusJson.Options);
        return new(request.CorrelationId, ControlDiagnostic.None, responsePayload);
    }
}
