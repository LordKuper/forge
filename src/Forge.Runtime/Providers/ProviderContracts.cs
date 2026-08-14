using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Application;

namespace Forge.Providers;

/// <summary>A provider-neutral identifier (ADR 0008: "the core contains no provider enum, [or]
/// concrete provider identifier"). Each adapter owns its own id value (e.g. "codex",
/// "claude_code"); the core never hardcodes one.</summary>
[JsonConverter(typeof(ProviderIdJsonConverter))]
public sealed record ProviderId(string Value);

/// <summary>Serializes/deserializes <see cref="ProviderId"/> as a plain JSON string (its
/// <see cref="ProviderId.Value"/>), matching the flat `id` shape every other provider-facing
/// contract (e.g. <c>ProviderHealthEntry.Id</c>) already uses instead of a nested object.</summary>
public sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
{
    public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A provider id must be a non-null string."));

    public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// Declares the same states as the `provider_toolchain` state machine in
/// `docs/contracts/v1/state-machines.json`. Install/update today is a single synchronous call
/// with no event bus to observe mid-flight, so only <see cref="Missing"/>, <see cref="Ready"/>,
/// and <see cref="Failed"/> are ever produced; <see cref="Installing"/>, <see cref="Updating"/>,
/// and <see cref="Rechecking"/> are reserved for future asynchronous progress reporting.
/// </summary>
public enum ProviderState
{
    Missing,
    Installing,
    Updating,
    Rechecking,
    Ready,
    Failed,
}

public static class ProviderDiagnosticCodes
{
    public const string None = "none";
    public const string Missing = "provider_missing";
    public const string VersionUnsupported = "provider_version_unsupported";
    public const string UpdateFailed = "provider_update_failed";

    /// <summary>Reserved for the P8.72-82 authentication check ADR 0008 describes: "Missing
    /// authentication blocks model work with `provider_authentication_required`." Not produced by
    /// any code yet — only the versioned contract (<see cref="ProviderHealthAuthentication"/>)
    /// names it ahead of that stage.</summary>
    public const string AuthenticationRequired = "provider_authentication_required";

    /// <summary>Reserved for the P8.72-82 authentication check: "a probe failure uses
    /// `provider_authentication_check_failed`." See <see cref="AuthenticationRequired"/>.</summary>
    public const string AuthenticationCheckFailed = "provider_authentication_check_failed";
}

public sealed record ProviderStatus(ProviderId Id, ProviderState State, string? Version, string DiagnosticCode)
{
    public static ProviderStatus Ready(ProviderId id, string version) =>
        new(id, ProviderState.Ready, version, ProviderDiagnosticCodes.None);
}

public sealed record ProviderToolchainStatus(IReadOnlyList<ProviderStatus> Providers)
{
    public bool Ready => Providers.Count > 0 && Providers.All(provider => provider.State == ProviderState.Ready);

    public string DiagnosticCode =>
        Providers.FirstOrDefault(provider => provider.State != ProviderState.Ready)?.DiagnosticCode ??
            ProviderDiagnosticCodes.None;

    /// <summary>Maps to the shared <see cref="DiagnosticCodes"/> used by startup checks and CLI reporting.</summary>
    public string SharedDiagnosticCode
    {
        get
        {
            if (Ready)
            {
                return DiagnosticCodes.None;
            }

            bool needsRepair = Providers.Any(provider =>
                provider.DiagnosticCode is ProviderDiagnosticCodes.UpdateFailed or
                    ProviderDiagnosticCodes.VersionUnsupported);
            return needsRepair ? DiagnosticCodes.ProviderUpdateFailed : DiagnosticCodes.ProviderPreflightPending;
        }
    }
}

/// <summary>
/// One complete provider integration (ADR 0008): local discovery, install/update, and bounded
/// execution. Implementations never read project configuration — provider location, version, and
/// executable path are user/Forge-owned only — and never leak vendor identity beyond
/// <see cref="Id"/>.
/// </summary>
public interface ILlmProvider
{
    ProviderId Id { get; }

    /// <summary>Reads the fixed, vendor-owned install path and runs `--version`. Never touches the network.</summary>
    Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>Runs the vendor's own native install or update mechanism, then rechecks.</summary>
    Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken);

    /// <summary>The absolute path to the vendor-installed executable, or null when not installed.</summary>
    Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken);

    /// <summary>Runs one bounded, non-interactive prompt and returns its parsed, redacted result.</summary>
    Task<ProviderRunResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>Aggregates every registered provider into one toolchain-wide status.</summary>
public interface IProviderToolchainManager
{
    /// <summary>Cheap, read-only, offline. Safe to call on every startup pass.</summary>
    Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Installs or updates any provider that is not ready, then rechecks all of them.</summary>
    Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken);
}
