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
/// states without exposing provider output or identity. <c>State</c> is <see langword="null"/>
/// only for a disabled provider (<c>Enabled</c> is <see langword="false"/>) — ADR 0008: a disabled
/// provider "is never discovered, installed, updated, authenticated, or executed," so it has no
/// install-lifecycle state to report.
/// </summary>
public sealed record ProviderHealthEntry(
    string Id,
    bool Registered,
    bool Enabled,
    ProviderState? State,
    string? Version,
    bool? UpdateAvailable,
    ProviderHealthAuthentication? Authentication,
    string DiagnosticCode);

/// <summary>
/// Projects a toolchain status plus a provider catalog onto the versioned provider-health
/// contract, purely and without any new probe.
/// </summary>
public static class ProviderHealthProjector
{
    /// <summary>
    /// Projects every enabled provider <see cref="IProviderToolchainManager"/> actually probed
    /// (<paramref name="status"/>) plus every registered-but-disabled provider from
    /// <paramref name="catalog"/>. <see cref="ProviderHealthEntry.UpdateAvailable"/> and
    /// <see cref="ProviderHealthEntry.Authentication"/> are read directly off the already-computed
    /// <see cref="ProviderStatus"/> (P8.72-82) for an enabled provider; a disabled provider
    /// (present in <paramref name="catalog"/> but absent from <paramref name="status"/> —
    /// <see cref="ProviderToolchainManager"/> (P8.64-71) excludes it before ever probing it) is
    /// synthesized as a read-only, never-probed entry (P8.83-88).
    /// </summary>
    public static IReadOnlyList<ProviderHealthEntry> Project(ProviderToolchainStatus status, ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> discovered = new(status.Providers.Select(provider => provider.Id.Value), StringComparer.Ordinal);
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
            .. catalog.Providers
                .Where(provider => !discovered.Contains(provider.Id.Value))
                .Select(provider => new ProviderHealthEntry(
                    provider.Id.Value,
                    true,
                    false,
                    null,
                    null,
                    null,
                    null,
                    ProviderDiagnosticCodes.Disabled)),
        ];
    }
}
