using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host.Client;

namespace Forge.Application;

/// <summary>
/// The client-side half of ADR 0005's "one Host owns mutations": every method here round-trips
/// through <see cref="ForgeHostClient"/> instead of ever executing the mutation locally. Only
/// project-scope <see cref="SetConfigurationAsync"/> calls are ever routed here — user-scope
/// configuration is not project state and stays local (see <see cref="IForgeMutations"/>'s own
/// remarks). Connecting is the caller's responsibility (<see cref="ForgeHostClient.EnsureConnectedAsync"/>
/// with a launcher, so a missing Host is started); a request sent before that, or a connection or
/// protocol failure, reports <see cref="DiagnosticCodes.HostUnavailable"/> — never a thrown
/// exception a caller must specifically catch, and never a silent local fallback. Disposing this
/// disposes the underlying <see cref="ForgeHostClient"/>.
/// </summary>
public sealed class RemoteForgeMutations(ForgeHostClient client, StartHostAsync? startHost = null)
    : IForgeMutations, IAsyncDisposable
{
    private readonly ForgeHostClient client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new RecoverStartupRequest(confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.RecoverStartupKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<RecoverStartupResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    public async Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (scope != ConfigurationScope.Project)
        {
            // A programming error in the caller, not a runtime condition — see IForgeMutations'
            // remarks on why user scope is never routed here.
            throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "Only project-scope configuration routes through the Host.");
        }

        JsonElement payload = JsonSerializer.SerializeToElement(
            new SetConfigurationRequest("project", key, rawValue),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.SetConfigurationKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<ConfigurationWriteResult>(ControlProtocol.JsonOptions) ??
                new(false, DiagnosticCodes.HostUnavailable)
            : new(false, DiagnosticCodes.HostUnavailable);
    }

    public Task<IntegrationWriteResult> InstallIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        SendIntegrationRequestAsync(ControlProtocol.InstallIntegrationKind, confirmed, cancellationToken);

    public Task<IntegrationWriteResult> RemoveIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        SendIntegrationRequestAsync(ControlProtocol.RemoveIntegrationKind, confirmed, cancellationToken);

    public async Task<NodeActionResult> ResolveGateAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool approved,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new ResolveGateRequest(sprintId, nodeId, approved, confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.ResolveGateKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<NodeActionResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    public async Task<RecordConfirmationResult> ConfirmNodeAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        ConfirmationOutcome outcome,
        string definitionOfDone,
        IReadOnlyList<ConfirmationEvidence> evidence,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(definitionOfDone);
        ArgumentNullException.ThrowIfNull(evidence);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new ConfirmNodeRequest(
                sprintId,
                nodeId,
                outcome == ConfirmationOutcome.Confirmed,
                definitionOfDone,
                [.. evidence.Select(item => new ConfirmationEvidenceEntry(EvidenceKindWireValue(item.Kind), item.Description))],
                confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.ConfirmNodeKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<RecordConfirmationResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    public async Task<RecordTestWorkResult> RecordTestWorkAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        TestWorkOutcome outcome,
        string justification,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(justification);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new RecordTestWorkRequest(sprintId, nodeId, outcome == TestWorkOutcome.TestsAdded, justification, confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.RecordTestWorkKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<RecordTestWorkResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    private static string EvidenceKindWireValue(ConfirmationEvidenceKind kind) => kind switch
    {
        ConfirmationEvidenceKind.Inspection => "inspection",
        ConfirmationEvidenceKind.Execution => "execution",
        ConfirmationEvidenceKind.ExistingCheck => "existing_check",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown confirmation evidence kind."),
    };

    public async Task<FinalizeSprintResult> FinalizeSprintAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new FinalizeSprintRequest(sprintId, nodeId, confirmed), ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.FinalizeSprintKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<FinalizeSprintResult>(ControlProtocol.JsonOptions) ??
                new(false, null, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, null, DiagnosticCodes.HostUnavailable);
    }

    public async Task<CompleteAttemptResult> SupersedeAttemptAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        string instruction,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new SupersedeAttemptRequest(sprintId, attemptId, instruction, confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.SupersedeAttemptKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<CompleteAttemptResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    public async Task<StopOperationResult> StopCurrentOperationAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new StopCurrentOperationRequest(sprintId, attemptId, confirmed), ControlProtocol.JsonOptions);
        ControlResponse response = await SendAsync(ControlProtocol.StopCurrentOperationKind, payload, cancellationToken)
            .ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<StopOperationResult>(ControlProtocol.JsonOptions) ??
                new(false, DiagnosticCodes.HostUnavailable)
            : new(false, DiagnosticCodes.HostUnavailable);
    }

    public async Task<CreateSprintResult> CreateSprintAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new CreateSprintRequest(), ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.CreateSprintKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<CreateSprintResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    public Task<SprintTransitionResult> RunSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        SendSprintIdRequestAsync(ControlProtocol.RunSprintKind, sprintId, cancellationToken);

    public Task<SprintTransitionResult> ResumeSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        SendSprintIdRequestAsync(ControlProtocol.ResumeSprintKind, sprintId, cancellationToken);

    public async Task<SprintTransitionResult> CancelSprintAsync(
        string? projectRoot, Guid sprintId, bool confirmed, CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new CancelSprintRequest(sprintId, confirmed), ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.CancelSprintKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<SprintTransitionResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    private async Task<SprintTransitionResult> SendSprintIdRequestAsync(
        string kind, Guid sprintId, CancellationToken cancellationToken)
    {
        JsonElement payload =
            JsonSerializer.SerializeToElement(new SprintIdRequest(sprintId), ControlProtocol.JsonOptions);
        ControlResponse response = await SendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<SprintTransitionResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    private async Task<IntegrationWriteResult> SendIntegrationRequestAsync(
        string kind,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new IntegrationWriteRequest(confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<IntegrationWriteResult>(ControlProtocol.JsonOptions) ??
                IntegrationWriteResult.Empty(DiagnosticCodes.HostUnavailable)
            : IntegrationWriteResult.Empty(DiagnosticCodes.HostUnavailable);
    }

    private async Task<ControlResponse> SendAsync(
        string kind,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            ControlDiagnostic connected = await client.EnsureConnectedAsync(startHost, cancellationToken)
                .ConfigureAwait(false);
            return connected.Code != ControlDiagnosticCode.None
                ? new(Guid.Empty, connected)
                : await client.SendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ControlProtocolException exception)
        {
            return new(Guid.Empty, new(exception.Code, exception.Message));
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
