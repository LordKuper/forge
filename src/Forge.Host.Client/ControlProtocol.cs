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

    /// <summary>The client resolved this request's <see cref="ControlRequest.Kind"/> to a capability
    /// absent from the Host's own handshake-advertised set and refused to send it — never returned
    /// by the Host itself, since anything the Host actually dispatches has, by definition, that
    /// capability. Distinct from <see cref="Malformed"/>'s "unknown kind": the client recognizes the
    /// kind fine, it just knows in advance this particular Host cannot serve it (an older Host talking
    /// to a newer client).</summary>
    CapabilityNotSupported,
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

/// <summary><see cref="ControlProtocol.CreateSprintKind"/>'s request payload. The Host always
/// creates from its own project's canonical graph and mints its own idempotency key, so
/// <see cref="Title"/> — the operator's optional short label for the new sprint (ADR 0057) — is the
/// only field that travels on the wire; see
/// <c>Forge.Application.ForgeApplication.CreateSprintAsync</c>. Additive and optional: a
/// pre-ADR-0057 client sends no payload at all, which the Host tolerates as "no title" rather than
/// rejecting.</summary>
public sealed record CreateSprintRequest(string? Title = null);

/// <summary>Shared by <see cref="ControlProtocol.RunSprintKind"/> and
/// <see cref="ControlProtocol.ResumeSprintKind"/> — neither is confirmable, so the target sprint id
/// is the only field either needs.</summary>
public sealed record SprintIdRequest(Guid SprintId);

/// <summary><see cref="ControlProtocol.CancelSprintKind"/>'s request payload — unlike
/// <see cref="SprintIdRequest"/>, cancellation is an ordinary destructive mutation (see
/// <see cref="IntegrationWriteRequest"/>), so it also carries <see cref="Confirmed"/>.</summary>
public sealed record CancelSprintRequest(Guid SprintId, bool Confirmed);

/// <summary><see cref="ControlProtocol.AssessStageTransitionKind"/>'s request payload (plan section
/// 8.1: read-only `workflow.assess_stage_transition`). No expected version travels on the wire — a
/// query has nothing to gate optimistic concurrency against; the response itself carries the
/// expected state version a subsequent <see cref="MoveSprintToStageRequest"/> must present.</summary>
public sealed record AssessStageTransitionRequest(Guid SprintId, string TargetStageId);

/// <summary><see cref="ControlProtocol.MoveSprintToStageKind"/>'s request payload (ADR 0046:
/// human-only `sprint.move_stage`). <see cref="AssessmentToken"/>/<see cref="ExpectedStateVersion"/>
/// are bound to the project/sprint/target/current-revision/state-version an
/// <see cref="AssessStageTransitionRequest"/> just returned (plan section 8.5); the Host recomputes
/// both fresh immediately before mutating and rejects any mismatch. <see cref="Reason"/> is
/// mandatory for a rewind, ignored for an advance. <see cref="IdempotencyKey"/> makes a repeated
/// commit a safe no-op that never records a second stage revision.</summary>
public sealed record MoveSprintToStageRequest(
    Guid SprintId,
    string TargetStageId,
    long ExpectedStateVersion,
    string? AssessmentToken,
    string? Reason,
    bool Confirmed,
    Guid IdempotencyKey);

/// <summary><see cref="ControlProtocol.GetWorkspaceSummaryKind"/>'s request payload (plan section
/// 6.2, Slice 4). Names no project: the Host always reports its own project's bounded summary row --
/// the client-side catalog fan-out across projects lives entirely outside any one Host (ADR 0049).
/// <see cref="IncludeDiffStats"/> (ADR 0069) opts into `ProjectWorkspaceSummary`'s per-sprint
/// `diff_stat`, the one member of that row a Host must spawn `git` processes to answer. It defaults to
/// <see langword="false"/>, so an absent payload -- including one sent by a client built before this
/// field existed -- gets exactly the cheap row every earlier release answered with (PR #126 review
/// finding 2).</summary>
public sealed record GetWorkspaceSummaryRequest(bool IncludeDiffStats = false);

/// <summary><see cref="ControlProtocol.GetSprintTimelineKind"/>'s request payload (plan section 6.3,
/// Slice 4). No expected version travels on the wire -- a query has nothing to gate optimistic
/// concurrency against, matching <see cref="AssessStageTransitionRequest"/>.</summary>
public sealed record GetSprintTimelineRequest(Guid SprintId, string? Cursor);

/// <summary><see cref="ControlProtocol.GetAvailableActionsKind"/>'s request payload (plan section
/// 6.4, Slice 4). <see cref="SprintId"/> is <see langword="null"/> for the project-level action set.
/// </summary>
public sealed record GetAvailableActionsRequest(Guid? SprintId);

/// <summary><see cref="ControlProtocol.GetProviderQuotaStatusKind"/>'s request payload (plan section
/// 6.5, Slice 7). Empty, like <see cref="GetWorkspaceSummaryRequest"/>: quota is a toolchain-wide
/// reading with no sprint/project-specific parameter.</summary>
public sealed record GetProviderQuotaStatusRequest;

/// <summary><see cref="ControlProtocol.PostSprintMessageKind"/>'s request payload (post-release
/// timeline gap closure, ADR 0054). No expected version travels on the wire -- a message post never
/// conflicts with concurrent workflow progress, matching <see cref="ResolveGateRequest"/>'s own
/// "the Host derives it from a fresh state read" reasoning. Not confirmable: posting a message is
/// additive, matching <see cref="SprintIdRequest"/>'s own run/resume commands.</summary>
public sealed record PostSprintMessageRequest(Guid SprintId, string Text);

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
    /// 0001), optionally under an operator-supplied title (ADR 0057). Request payload: a
    /// <see cref="CreateSprintRequest"/>, or none at all from a client predating that title.
    /// Response payload: a `CreateSprintResult` instance
    /// (<c>{"succeeded": bool, "sprint_id"?: uuid, "diagnostic_code": string}</c>).</summary>
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

    /// <summary>Plan section 8.1: the read-only `workflow.assess_stage_transition` query. Request
    /// payload: an <see cref="AssessStageTransitionRequest"/>. Response payload: a
    /// `StageTransitionAssessment` instance.</summary>
    public const string AssessStageTransitionKind = "assess_stage_transition";

    /// <summary>ADR 0046: the human-only `sprint.move_stage` capability — commits an already-assessed
    /// advance or rewind. Request payload: a <see cref="MoveSprintToStageRequest"/>. Response
    /// payload: a `MoveStageResult` instance
    /// (<c>{"succeeded": bool, "sprint"?: {...}, "target_node"?: {...}, "diagnostic_code": string}</c>).</summary>
    public const string MoveSprintToStageKind = "move_sprint_to_stage";

    /// <summary>Plan section 6.2's reserved `workspace.summary` query (Slice 4, ADR 0043/0049): one
    /// project's bounded sidebar/status-header row. Request payload: a
    /// <see cref="GetWorkspaceSummaryRequest"/>. Response payload: a `ProjectWorkspaceSummary`
    /// instance.</summary>
    public const string GetWorkspaceSummaryKind = "get_workspace_summary";

    /// <summary>Plan section 6.3's reserved `sprint.timeline` query (Slice 4, ADR 0043/0049): a
    /// bounded, cursor-paged projection of one sprint's existing workflow journal. Request payload: a
    /// <see cref="GetSprintTimelineRequest"/>. Response payload: a `SprintTimelinePage` instance.
    /// </summary>
    public const string GetSprintTimelineKind = "get_sprint_timeline";

    /// <summary>Plan section 6.4's reserved `workspace.available_actions` query (Slice 4, ADR
    /// 0043/0049). Request payload: a <see cref="GetAvailableActionsRequest"/>. Response payload:
    /// <c>{"actions": [...]}</c>.</summary>
    public const string GetAvailableActionsKind = "get_available_actions";

    /// <summary>Plan section 6.5's reserved `provider.quota_status` query (Slice 7, ADR 0043/0052):
    /// every enabled and registered-but-disabled provider's quota reading. Request payload: a
    /// <see cref="GetProviderQuotaStatusRequest"/>. Response payload: a `ProviderQuotaStatus`
    /// instance. ADR 0052 found no provider integration in this codebase exposes a verified quota
    /// signal, so every reading is currently `unknown`.</summary>
    public const string GetProviderQuotaStatusKind = "get_provider_quota_status";

    /// <summary>Post-release timeline gap closure (plan section 4.3/6.3, ADR 0054): the reserved
    /// `sprint.post_message` capability -- appends a bounded user-posted message to the sprint's own
    /// append-only journal. Request payload: a <see cref="PostSprintMessageRequest"/>. Response
    /// payload: a `PostSprintMessageResult` instance
    /// (<c>{"succeeded": bool, "state"?: {...}, "diagnostic_code": string}</c>). Not yet in
    /// <c>CapabilityIds.Implemented</c>, matching ADR 0049/0050/0051's own precedent for
    /// `sprint.timeline`/`workflow.stop_operation`/`sprint.move_stage`.</summary>
    public const string PostSprintMessageKind = "post_sprint_message";

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
