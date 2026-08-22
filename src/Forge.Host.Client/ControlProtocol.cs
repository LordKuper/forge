using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Client;

/// <summary>Stable, machine-readable outcome of a control-plane operation; never a raw exception message.</summary>
public enum ControlDiagnosticCode
{
    None,
    Unavailable,
    HandshakeTimedOut,
    VersionIncompatible,
    MessageTooLarge,
    Malformed,
    Timeout,
    ProjectInUse,
    ConnectionClosed,
    Canceled,

    /// <summary>A well-formed request the Host could not complete because of a server-side failure
    /// (e.g. an unreadable journal file) that was never the client's fault — distinct from
    /// <see cref="Malformed"/>, which always means the request itself was invalid.</summary>
    InternalError,
}

public sealed record ControlDiagnostic(ControlDiagnosticCode Code, string Detail)
{
    public static ControlDiagnostic None { get; } = new(ControlDiagnosticCode.None, string.Empty);
}

/// <summary>The first message on every connection. An incompatible major version is rejected before any project access.</summary>
public sealed record ControlHandshakeRequest(
    string ProtocolVersion,
    string ClientVersion,
    string InstanceId,
    IReadOnlyList<string> Capabilities,
    Guid CorrelationId);

public sealed record ControlHandshakeResponse(
    string ProtocolVersion,
    string HostVersion,
    IReadOnlyList<string> Capabilities,
    ControlDiagnostic Diagnostic,
    Guid CorrelationId);

/// <summary>
/// The generic envelope every command/query after the handshake uses. <see cref="Kind"/> names the operation
/// (e.g. <c>ping</c>); later Stage 8 work adds more kinds without changing this shape.
/// </summary>
public sealed record ControlRequest(string Kind, Guid CorrelationId, JsonElement? Payload = null);

public sealed record ControlResponse(Guid CorrelationId, ControlDiagnostic Diagnostic, JsonElement? Payload = null);

/// <summary><see cref="ControlProtocol.RecoverStartupKind"/>'s request payload. The Host always
/// recovers its own project (it is already scoped to exactly one), so no project root travels on
/// the wire.</summary>
public sealed record RecoverStartupRequest(bool Confirmed);

/// <summary><see cref="ControlProtocol.SetConfigurationKind"/>'s request payload. Only
/// <c>"project"</c> is ever valid here — user-scope configuration is not project state and is never
/// routed through a project's Host (see <c>Forge.Application.RemoteForgeMutations</c>).</summary>
public sealed record SetConfigurationRequest(string Scope, string Key, string? RawValue);

/// <summary>Shared by both <see cref="ControlProtocol.InstallIntegrationKind"/> and
/// <see cref="ControlProtocol.RemoveIntegrationKind"/> — identical shape to
/// <see cref="RecoverStartupRequest"/> for the same reason (ADR 0011: the Host always acts on its
/// own project, and re-derives integration state fresh on every call, so no project root or
/// expected-state-version travels on the wire).</summary>
public sealed record IntegrationWriteRequest(bool Confirmed);

/// <summary><see cref="ControlProtocol.ResolveGateKind"/>'s request payload (ADR 0005/0018:
/// human-only `workflow.review`). No expected node version travels on the wire — the Host derives
/// it from a fresh state read of <see cref="SprintId"/>/<see cref="NodeId"/> instead, the same
/// reason <see cref="SupersedeAttemptRequest"/> carries no attempt version.</summary>
public sealed record ResolveGateRequest(Guid SprintId, string NodeId, bool Approved, bool Confirmed);

/// <summary><see cref="ControlProtocol.SupersedeAttemptKind"/>'s request payload (ADR 0005/0018:
/// human-only `attempt.supersede`).</summary>
public sealed record SupersedeAttemptRequest(Guid SprintId, Guid AttemptId, string Instruction, bool Confirmed);

/// <summary><see cref="ControlProtocol.StopCurrentOperationKind"/>'s request payload (ADR 0044:
/// human-only `workflow.stop_operation`). No expected sprint/attempt version travels on the wire --
/// the Host derives both from a fresh state read and rejects a stale one internally, the same
/// reason <see cref="SupersedeAttemptRequest"/> carries no attempt version.</summary>
public sealed record StopCurrentOperationRequest(Guid SprintId, Guid AttemptId, bool Confirmed);

/// <summary>One <c>ConfirmNodeRequest.Evidence</c> entry. <see cref="Kind"/> is one of
/// <c>"inspection"</c>/<c>"execution"</c>/<c>"existing_check"</c> (matching
/// <c>confirmation-result.schema.json</c>'s own vocabulary) — a primitive string, not
/// <c>Forge.Domain.ConfirmationEvidenceKind</c>, since <c>Forge.Host.Client</c> has no reference to
/// <c>Forge.Domain</c>.</summary>
public sealed record ConfirmationEvidenceEntry(string Kind, string Description);

/// <summary><see cref="ControlProtocol.ConfirmNodeKind"/>'s request payload (the human-only
/// `workflow.confirm` capability). No expected node version travels on the wire — same reason as
/// <see cref="ResolveGateRequest"/>. <see cref="Outcome"/> is <see langword="true"/> for
/// <c>Confirmed</c>, <see langword="false"/> for <c>NotConfirmed</c>.</summary>
public sealed record ConfirmNodeRequest(
    Guid SprintId,
    string NodeId,
    bool Outcome,
    string DefinitionOfDone,
    IReadOnlyList<ConfirmationEvidenceEntry> Evidence,
    bool Confirmed);

/// <summary><see cref="ControlProtocol.RecordTestWorkKind"/>'s request payload (the human-only
/// `workflow.test_work` capability). No expected node version travels on the wire — same reason as
/// <see cref="ResolveGateRequest"/>. <see cref="Outcome"/> is <see langword="true"/> for
/// <c>TestsAdded</c>, <see langword="false"/> for <c>NoNewTestsJustified</c>.</summary>
public sealed record RecordTestWorkRequest(
    Guid SprintId,
    string NodeId,
    bool Outcome,
    string Justification,
    bool Confirmed);

/// <summary><see cref="ControlProtocol.FinalizeSprintKind"/>'s request payload (the human-only
/// `workflow.finalize` capability). No expected node version travels on the wire — same reason as
/// <see cref="ResolveGateRequest"/>.</summary>
public sealed record FinalizeSprintRequest(Guid SprintId, string NodeId, bool Confirmed);

/// <summary><see cref="ControlProtocol.CreateSprintKind"/>'s request payload. Empty: the Host
/// always creates from its own project's canonical graph, and mints its own idempotency key, so
/// nothing travels on the wire — see <c>Forge.Application.ForgeApplication.CreateSprintAsync</c>.</summary>
public sealed record CreateSprintRequest;

/// <summary>Shared by <see cref="ControlProtocol.RunSprintKind"/> and
/// <see cref="ControlProtocol.ResumeSprintKind"/> — neither is confirmable, so the target sprint id
/// is the only field either needs.</summary>
public sealed record SprintIdRequest(Guid SprintId);

/// <summary><see cref="ControlProtocol.CancelSprintKind"/>'s request payload — unlike
/// <see cref="SprintIdRequest"/>, cancellation is an ordinary destructive mutation (see
/// <see cref="IntegrationWriteRequest"/>), so it also carries <see cref="Confirmed"/>.</summary>
public sealed record CancelSprintRequest(Guid SprintId, bool Confirmed);

public static class ControlProtocol
{
    /// <summary>The control-plane wire protocol's own version, independent of the Forge product version.</summary>
    public const string Version = "1.0.0";

    public const string PingKind = "ping";

    /// <summary>`GetProjectSnapshot(detail, sprint_id?)` — ADR 0005's authoritative read model.
    /// Request payload: <c>{"detail"?: "summary"|"full", "sprint_id"?: uuid}</c>. Response payload:
    /// a `project-snapshot.schema.json` instance.</summary>
    public const string GetProjectSnapshotKind = "get_project_snapshot";

    /// <summary>`ReadControlEvents` — one bounded incremental read from the durable per-sprint
    /// journals. Request payload: <c>{"cursor"?: string}</c>. Response payload: a
    /// `control-event-page.schema.json` instance.</summary>
    public const string ReadControlEventsKind = "read_control_events";

    /// <summary>ADR 0005: the Host owns every `.forge/` mutation — recovering a project whose
    /// configuration cannot be read is one. Request payload: a <see cref="RecoverStartupRequest"/>.
    /// Response payload: <c>{"succeeded": bool, "check"?: string, "diagnostic_code": string}</c>.</summary>
    public const string RecoverStartupKind = "recover_startup";

    /// <summary>ADR 0005: the Host owns every `.forge/` mutation — writing a project configuration
    /// value is one (user-scope configuration is not routed here; see
    /// <see cref="SetConfigurationRequest"/>). Response payload:
    /// <c>{"succeeded": bool, "diagnostic_code": string}</c>.</summary>
    public const string SetConfigurationKind = "set_configuration";

    /// <summary>ADR 0011: writes `CLAUDE.md`/`AGENTS.md` for every enabled provider. Request
    /// payload: an <see cref="IntegrationWriteRequest"/>. Response payload:
    /// <c>{"artifacts": [...], "diagnostic_code": string}</c>.</summary>
    public const string InstallIntegrationKind = "install_integration";

    /// <summary>ADR 0011: deletes a Forge-owned `CLAUDE.md`/`AGENTS.md` for every enabled provider.
    /// Request payload: an <see cref="IntegrationWriteRequest"/>. Response payload:
    /// <c>{"artifacts": [...], "diagnostic_code": string}</c>.</summary>
    public const string RemoveIntegrationKind = "remove_integration";

    /// <summary>ADR 0005/0018: the human-only `workflow.review` capability — approves or rejects an
    /// `awaiting_human` gate node. Request payload: a <see cref="ResolveGateRequest"/>. Response
    /// payload: a `NodeActionResult` instance (<c>{"succeeded": bool, "node"?: {...}, "diagnostic_code": string}</c>).</summary>
    public const string ResolveGateKind = "resolve_gate";

    /// <summary>ADR 0005/0018: the human-only `attempt.supersede` capability — cancels a
    /// non-terminal attempt and creates a linked replacement. Request payload: a
    /// <see cref="SupersedeAttemptRequest"/>. Response payload: a `CompleteAttemptResult` instance
    /// (same shape as <see cref="ResolveGateKind"/>'s response).</summary>
    public const string SupersedeAttemptKind = "supersede_attempt";

    /// <summary>ADR 0044: the human-only `workflow.stop_operation` capability -- durably records a
    /// stop intent for the sprint's exact active attempt and cancels it without settling the sprint
    /// as failed or consuming automatic retry budget. Request payload: a
    /// <see cref="StopCurrentOperationRequest"/>. Response payload: a `StopOperationResult` instance
    /// (<c>{"succeeded": bool, "diagnostic_code": string}</c>).</summary>
    public const string StopCurrentOperationKind = "stop_current_operation";

    /// <summary>The human-only `workflow.confirm` capability — records whether a sprint's
    /// `confirmation` node's implementation meets its definition of done, and settles that node's
    /// own attempt to a terminal state in the same call (no executor exists for this role). Request
    /// payload: a <see cref="ConfirmNodeRequest"/>. Response payload: a `RecordConfirmationResult`
    /// instance (<c>{"succeeded": bool, "confirmation"?: {...}, "diagnostic_code": string}</c>).</summary>
    public const string ConfirmNodeKind = "confirm_node";

    /// <summary>The human-only `workflow.test_work` capability — records whether new tests were
    /// added to protect the scope, or a justified decision was made that none were needed, and
    /// settles that node's own attempt to a terminal state in the same call (no executor exists for
    /// this role either). Request payload: a <see cref="RecordTestWorkRequest"/>. Response payload:
    /// a `RecordTestWorkResult` instance (<c>{"succeeded": bool, "test_work"?: {...}, "diagnostic_code": string}</c>).</summary>
    public const string RecordTestWorkKind = "record_test_work";

    /// <summary>The human-only `workflow.finalize` capability — merges a sprint's isolated
    /// integration branch into the project's own default branch and, on success, completes the
    /// sprint. Request payload: a <see cref="FinalizeSprintRequest"/>. Response payload: a
    /// `FinalizeSprintResult` instance (<c>{"succeeded": bool, "node"?: {...}, "sprint"?: {...}, "diagnostic_code": string}</c>).</summary>
    public const string FinalizeSprintKind = "finalize_sprint";

    /// <summary>Creates a sprint from the project's canonical `implementation-critical` graph (ADR
    /// 0001). Request payload: a <see cref="CreateSprintRequest"/>. Response payload: a
    /// `CreateSprintResult` instance (<c>{"succeeded": bool, "sprint_id"?: uuid, "diagnostic_code": string}</c>).</summary>
    public const string CreateSprintKind = "create_sprint";

    /// <summary>Advances a sprint one legal hop (`draft` to `ready`, then `ready` to `running`).
    /// Request payload: a <see cref="SprintIdRequest"/>. Response payload: a
    /// `SprintTransitionResult` instance (same shape as <see cref="ResumeSprintKind"/>'s response).</summary>
    public const string RunSprintKind = "run_sprint";

    /// <summary>Un-blocks a `blocked` sprint back to `ready`. Request payload: a
    /// <see cref="SprintIdRequest"/>. Response payload: a `SprintTransitionResult` instance
    /// (<c>{"succeeded": bool, "sprint"?: {...}, "diagnostic_code": string}</c>).</summary>
    public const string ResumeSprintKind = "resume_sprint";

    /// <summary>Cancels a sprint. Request payload: a <see cref="CancelSprintRequest"/>. Response
    /// payload: a `SprintTransitionResult` instance (same shape as
    /// <see cref="ResumeSprintKind"/>'s response).</summary>
    public const string CancelSprintKind = "cancel_sprint";

    // Matches Forge.Application.StatusJson/Forge.Configuration.ConfigurationSchemaCodec's snake_case convention
    // for wire compatibility with the existing contracts. Duplicated rather than shared: Forge.Host.Client is
    // deliberately a leaf with no ProjectReference, since CLI/Desktop composition roots pull it in without
    // needing the whole workflow engine.
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Whether two protocol versions are compatible: an exact major-version match.</summary>
    public static bool IsCompatible(string requested, string supported)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        ArgumentException.ThrowIfNullOrWhiteSpace(supported);
        return string.Equals(MajorVersion(requested), MajorVersion(supported), StringComparison.Ordinal);
    }

    private static string MajorVersion(string version)
    {
        int separator = version.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? version : version[..separator];
    }
}
