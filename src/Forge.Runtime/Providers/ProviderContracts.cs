using Forge.Application;

namespace Forge.Providers;

public enum ProviderKind
{
    Codex,
    ClaudeCode,
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
}

public sealed record ProviderStatus(ProviderKind Kind, ProviderState State, string? Version, string DiagnosticCode)
{
    public static ProviderStatus Ready(ProviderKind kind, string version) =>
        new(kind, ProviderState.Ready, version, ProviderDiagnosticCodes.None);
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
/// One official provider CLI's toolchain lifecycle. Implementations never read project
/// configuration: provider location, version, and executable path are user/Forge-owned only.
/// </summary>
public interface IProviderStrategy
{
    ProviderKind Kind { get; }

    /// <summary>Reads the fixed, vendor-owned install path and runs `--version`. Never touches the network.</summary>
    Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>Runs the vendor's own native install or update mechanism, then rechecks.</summary>
    Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken);

    /// <summary>The absolute path to the vendor-installed executable, or null when not installed.</summary>
    Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken);
}

/// <summary>Aggregates every registered provider strategy behind the startup gate and `forge models`.</summary>
public interface IProviderToolchainManager
{
    /// <summary>Cheap, read-only, offline. Safe to call on every startup pass.</summary>
    Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Installs or updates any provider that is not ready, then rechecks all of them.</summary>
    Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken);
}
