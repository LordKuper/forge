using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host.Client;
using Forge.Presentation;

namespace Forge.Application;

/// <summary>
/// The client-side half of ADR 0005's "one Host owns mutations": every method here round-trips
/// through <see cref="ForgeHostClient"/> instead of ever executing the mutation locally. Only
/// project-scope <see cref="SetConfigurationAsync"/> calls are ever routed here — user-scope
/// configuration is not project state and stays local (see <see cref="IForgeMutations"/>'s own
/// remarks). Connecting is the caller's responsibility (<see cref="ForgeHostClient.EnsureConnectedAsync"/>
/// with a launcher, so a missing Host is started); a request sent before that, or a connection or
/// protocol failure, reports <see cref="DiagnosticCodes.HostUnavailable"/> — never a thrown
/// exception a caller must specifically catch, and never a silent local fallback. ADR 0053: a request
/// whose <see cref="CapabilityByKind"/> capability is absent from <see cref="ForgeHostClient.HostCapabilities"/>
/// reports <see cref="DiagnosticCodes.CapabilityNotSupported"/> instead and is never actually sent.
/// Disposing this disposes the underlying <see cref="ForgeHostClient"/>.
/// </summary>
public sealed class RemoteForgeMutations(
    ForgeHostClient client, StartHostAsync? startHost = null, IHostConnectivityMonitor? connectivityMonitor = null)
    : IForgeMutations, IAsyncDisposable
{
    private readonly ForgeHostClient client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>ADR 0053's hand-maintained `ControlRequest.Kind` -> `CapabilityIds` gate table:
    /// every <see cref="ControlProtocol"/> kind this class ever sends, whose governing capability is
    /// already in <see cref="CapabilityIds.Implemented"/>. Drift against `capabilities.json` is
    /// caught by <c>CapabilityNegotiationMappingTests</c>, not at runtime -- see that test's own
    /// remarks for why this stays hand-written instead of loaded from the contract file. A kind
    /// absent here is never gated: that covers both "no capability governs it" (<c>ping</c>,
    /// `recover_startup` -- neither has a `capabilities.json` entry at all) and "its capability is
    /// still reserved" (`workflow.stop_operation`, `sprint.move_stage`, ...) -- gating the latter
    /// would reject a request against a Host that actually serves it today, only because
    /// `CapabilityIds.Implemented` has not been widened yet (ADR 0049/0050/0051's own "separable
    /// cleanup" precedent). Internal (not private) so that test can verify it directly rather than
    /// through reflection.</summary>
    internal static readonly Dictionary<string, string> CapabilityByKind =
        new(StringComparer.Ordinal)
        {
            [ControlProtocol.GetProjectSnapshotKind] = CapabilityIds.ProjectSnapshot,
            [ControlProtocol.ReadControlEventsKind] = CapabilityIds.ControlEvents,
            [ControlProtocol.SetConfigurationKind] = CapabilityIds.ConfigurationManage,
            [ControlProtocol.InstallIntegrationKind] = CapabilityIds.IntegrationSkill,
            [ControlProtocol.RemoveIntegrationKind] = CapabilityIds.IntegrationSkill,
            [ControlProtocol.ResolveGateKind] = CapabilityIds.WorkflowReview,
            [ControlProtocol.SupersedeAttemptKind] = CapabilityIds.AttemptSupersede,
            [ControlProtocol.ConfirmNodeKind] = CapabilityIds.WorkflowConfirm,
            [ControlProtocol.RecordTestWorkKind] = CapabilityIds.WorkflowTestWork,
            [ControlProtocol.FinalizeSprintKind] = CapabilityIds.WorkflowFinalize,
            [ControlProtocol.CreateSprintKind] = CapabilityIds.SprintManage,
            [ControlProtocol.RunSprintKind] = CapabilityIds.SprintManage,
            [ControlProtocol.ResumeSprintKind] = CapabilityIds.SprintManage,
            [ControlProtocol.CancelSprintKind] = CapabilityIds.SprintManage,
        };

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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, DiagnosticCodeFor(response.Diagnostic));
    }

    public async Task<CreateSprintResult> CreateSprintAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new CreateSprintRequest(), ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.CreateSprintKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<CreateSprintResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
    }

    public async Task<MoveStageResult> MoveSprintToStageAsync(
        string? projectRoot,
        Guid sprintId,
        string targetStageId,
        long expectedStateVersion,
        string? assessmentToken,
        string? reason,
        bool confirmed,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new MoveSprintToStageRequest(
                sprintId, targetStageId, expectedStateVersion, assessmentToken, reason, confirmed, idempotencyKey),
            ControlProtocol.JsonOptions);
        ControlResponse response = await SendAsync(ControlProtocol.MoveSprintToStageKind, payload, cancellationToken)
            .ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<MoveStageResult>(ControlProtocol.JsonOptions) ??
                new(false, null, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, null, DiagnosticCodeFor(response.Diagnostic));
    }

    /// <summary>Post-release timeline gap closure (ADR 0054). Unlike the human-only capabilities
    /// above, <see cref="ControlProtocol.PostSprintMessageKind"/> stays absent from
    /// <see cref="CapabilityByKind"/> -- it is a reserved capability (matching
    /// `sprint.timeline`/`workflow.stop_operation`/`sprint.move_stage`'s own precedent), so this
    /// request is never gated client-side even though it is fully served today.</summary>
    public async Task<PostSprintMessageResult> PostSprintMessageAsync(
        string? projectRoot, Guid sprintId, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new PostSprintMessageRequest(sprintId, text), ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.PostSprintMessageKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<PostSprintMessageResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : new(false, null, DiagnosticCodeFor(response.Diagnostic));
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
            : IntegrationWriteResult.Empty(DiagnosticCodeFor(response.Diagnostic));
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
            if (connected.Code != ControlDiagnosticCode.None)
            {
                return new(Guid.Empty, connected);
            }

            // Checked only once connected, against THIS Host's own just-negotiated set -- never a
            // stale value from an earlier connection (ForgeHostClient.HostCapabilities resets on
            // every disconnect). A missing capability is rejected here, before the request ever
            // reaches the wire (plan section 9.2).
            if (CapabilityByKind.TryGetValue(kind, out string? capability) &&
                !client.HostCapabilities.Contains(capability, StringComparer.Ordinal))
            {
                return new(
                    Guid.Empty,
                    new ControlDiagnostic(
                        ControlDiagnosticCode.CapabilityNotSupported,
                        $"The connected Host does not advertise the '{capability}' capability this request needs."));
            }

            return await client.SendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ControlProtocolException exception)
        {
            return new(Guid.Empty, new(exception.Code, exception.Message));
        }
        finally
        {
            // Plan 12.6's Host-connectivity status-row indicator: report the real, CURRENT outcome of
            // THIS attempt (see HostConnectivityMonitor's own remarks -- it never probes on its own),
            // keyed by this client's own project (PR #106 review finding 5) so one project's
            // connectivity is never attributed to another's status row. In a `finally` rather than
            // only after a successful EnsureConnectedAsync (PR #106 review finding 3): a framing/
            // timeout failure from client.SendAsync above drops the connection and rethrows, caught by
            // the catch block above, which returns without ever reaching an inline report call after
            // that point -- so the monitor kept whatever the EARLIER successful EnsureConnectedAsync
            // reported, and the status row showed "Connected to Host." for up to the staleness window
            // after the connection had actually died. `client.IsConnected` already reflects
            // DropConnectionAsync's effect by the time this `finally` runs (it awaits that call before
            // rethrowing), so reporting it here -- on every exit path, success or failure -- always
            // reflects this attempt's real, current state.
            connectivityMonitor?.Report(client.ProjectId, client.IsConnected, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Collapses every non-success wire diagnostic to one typed-result field.
    /// <see cref="ControlDiagnosticCode.CapabilityNotSupported"/> (ADR 0053) surfaces as its own
    /// distinct <see cref="DiagnosticCodes.CapabilityNotSupported"/>; every other code (unreachable
    /// Host, malformed response, timeout, ...) keeps collapsing to the existing generic
    /// <see cref="DiagnosticCodes.HostUnavailable"/> -- callers never needed, and still don't need,
    /// to tell those apart.</summary>
    private static string DiagnosticCodeFor(ControlDiagnostic diagnostic) =>
        diagnostic.Code == ControlDiagnosticCode.CapabilityNotSupported
            ? DiagnosticCodes.CapabilityNotSupported
            : DiagnosticCodes.HostUnavailable;

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
