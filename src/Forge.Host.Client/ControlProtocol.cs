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
