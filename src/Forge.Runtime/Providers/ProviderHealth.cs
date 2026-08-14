using Forge.Domain;

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
/// contract, purely and without any new probe. <see cref="ProviderHealthEntry.UpdateAvailable"/>
/// and <see cref="ProviderHealthEntry.Authentication"/> are always <see langword="null"/> today: no
/// enablement filter (P8.64-71) or update/authentication check (P8.72-82) exists yet, so this
/// stage only versions the contract shape those stages will populate. Every provider
/// <see cref="IProviderToolchainManager"/> discovers is, by construction, both registered and
/// enabled — nothing yet filters or disables one.
/// </summary>
public static class ProviderHealthProjector
{
    public static IReadOnlyList<ProviderHealthEntry> Project(ProviderToolchainStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return
        [
            .. status.Providers.Select(provider => new ProviderHealthEntry(
                WorkflowStateNames.ToSnakeCase(provider.Kind),
                true,
                true,
                provider.State,
                provider.Version,
                null,
                null,
                provider.DiagnosticCode)),
        ];
    }
}
