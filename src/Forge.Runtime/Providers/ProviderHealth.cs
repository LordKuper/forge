namespace Forge.Providers;

/// <summary>Local authentication readiness for one provider's final executable — ADR 0008's
/// "every enabled provider must report local authentication readiness." <see langword="null"/>
/// means not yet determined: the P8.72-82 authentication check this field describes has not been
/// implemented yet, so every current projection reports it as unknown rather than guessing.</summary>
public enum ProviderHealthAuthentication
{
    Required,
    CheckFailed,
    Ready,
}

/// <summary>
/// One provider's normalized, presentation-safe health — the `provider-health.schema.json`
/// contract ADR 0008 assigns to the project snapshot, CLI, and Desktop, distinguishing registered,
/// enabled, disabled, missing, current, update-available, authentication-required, and ready
/// states without exposing provider output or identity.
/// </summary>
public sealed record ProviderHealthEntry(
    string Id,
    bool Registered,
    bool Enabled,
    ProviderState State,
    string? Version,
    bool? UpdateAvailable,
    ProviderHealthAuthentication? Authentication,
    string DiagnosticCode);

/// <summary>
/// Projects the current <see cref="ProviderToolchainStatus"/> onto the versioned provider-health
/// contract, purely and without any new probe — <see cref="ProviderHealthEntry.UpdateAvailable"/>
/// and <see cref="ProviderHealthEntry.Authentication"/> are read directly off the already-computed
/// <see cref="ProviderStatus"/> (P8.72-82). Every provider <see cref="IProviderToolchainManager"/>
/// discovers is, by construction, both registered and enabled — <see cref="ProviderToolchainManager"/>
/// (P8.64-71) already excludes a disabled provider before ever probing it, so this projection never
/// sees one. Surfacing disabled providers as distinct entries is P8.83-88's job.
/// </summary>
public static class ProviderHealthProjector
{
    public static IReadOnlyList<ProviderHealthEntry> Project(ProviderToolchainStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return
        [
            .. status.Providers.Select(provider => new ProviderHealthEntry(
                provider.Id.Value,
                true,
                true,
                provider.State,
                provider.Version,
                provider.UpdateAvailable,
                provider.Authentication?.State,
                provider.DiagnosticCode)),
        ];
    }
}
