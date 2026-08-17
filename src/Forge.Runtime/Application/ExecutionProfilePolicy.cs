using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

/// <summary>
/// Resolves and validates the three frozen <see cref="ExecutionProfile"/>s ADR 0006 requires
/// (ADR 0014). Pure and deterministic given already-frozen inputs (<see cref="SprintDefinition.FrozenProviders"/>
/// and the registered <see cref="ProviderCatalog"/>) — no I/O, no clock, so <see cref="SprintOrchestrator.CreateSprintAsync"/>
/// can call it once at freeze time and get the exact same result on any resumed retry.
/// </summary>
public static class ExecutionProfilePolicy
{
    /// <summary>
    /// Capability ids ADR 0006 calls "human-only" by their <c>docs/contracts/v1/capabilities.json</c>
    /// permission naming convention (<c>human_gate_confirm</c>, <c>human_attempt_supersede_confirm</c>)
    /// — a model node's own <see cref="ExecutionProfile.CapabilityAllowlist"/> must never contain
    /// one, matching "a node cannot widen them or invoke human-only commands." Neither capability
    /// has an application-layer implementation yet (`workflow.review`'s human decision is
    /// <c>SprintScheduler.ResolveHumanGateAsync</c>; `attempt.supersede` is P11.48-P11.55's own
    /// item), but both ids are already real, committed contract entries — not invented here.
    /// </summary>
    public static readonly IReadOnlyCollection<string> HumanOnlyCapabilityIds =
        new HashSet<string>(StringComparer.Ordinal) { "workflow.review", "attempt.supersede" };

    // ponytail: every phase shares one fixed MVP policy (sandbox/permission/allowlist), and effort
    // only distinguishes review from the other two — no per-project model policy configuration
    // exists yet (ADR 0006 describes one; nothing built it). Revisit once it does.
    private const string SandboxPolicy = "workspace-write";
    private const string PermissionPolicy = "never";
    private static readonly IReadOnlyList<string> CapabilityAllowlist =
        [ContextCapabilityIds.GitShow, ContextCapabilityIds.GitGrep];

    /// <summary>
    /// Freezes exactly three profiles — <see cref="ExecutionPhase.Planning"/>,
    /// <see cref="ExecutionPhase.Implementation"/>, and <see cref="ExecutionPhase.Review"/> — from
    /// <paramref name="frozenProviders"/> (already ADR 0008's ordered candidate intersection) and
    /// <paramref name="catalog"/>. Planning and implementation both use the highest-priority
    /// candidate; review prefers the highest-priority candidate whose id differs from the
    /// implementation phase's provider (lineage independence), falling back to the same provider —
    /// recording <see cref="ExecutionLineage.AchievedIndependence"/> either way, never blocking.
    /// </summary>
    public static IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> Freeze(
        IReadOnlyList<string> frozenProviders,
        ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(frozenProviders);
        ArgumentNullException.ThrowIfNull(catalog);
        if (frozenProviders.Count == 0)
        {
            throw new ArgumentException(
                "At least one frozen provider is required to freeze execution profiles.", nameof(frozenProviders));
        }

        string implementationProvider = frozenProviders[0];
        (string reviewProvider, bool achievedIndependence) =
            SelectReviewProvider(frozenProviders, implementationProvider);

        return new Dictionary<ExecutionPhase, ExecutionProfile>
        {
            [ExecutionPhase.Planning] = BuildProfile(
                ExecutionPhase.Planning, implementationProvider, catalog, "medium", 1800, 180, null),
            [ExecutionPhase.Implementation] = BuildProfile(
                ExecutionPhase.Implementation, implementationProvider, catalog, "medium", 1800, 180, null),
            [ExecutionPhase.Review] = BuildProfile(
                ExecutionPhase.Review, reviewProvider, catalog, "high", 3600, 300,
                new ExecutionLineage(
                    implementationProvider, ModelFor(implementationProvider, catalog), achievedIndependence)),
        };
    }

    /// <summary>
    /// Best-effort reviewer/implementation lineage separation (ADR 0006/0008): the first candidate
    /// with a different provider id than <paramref name="implementationProvider"/>, in priority
    /// order, or <paramref name="implementationProvider"/> itself when none exists (a single-provider
    /// configuration completes review; reduced separation is recorded, never a gate).
    /// </summary>
    public static (string Provider, bool AchievedIndependence) SelectReviewProvider(
        IReadOnlyList<string> candidates,
        string implementationProvider)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(implementationProvider);
        string? distinct = candidates.FirstOrDefault(
            candidate => !string.Equals(candidate, implementationProvider, StringComparison.Ordinal));
        return distinct is not null ? (distinct, true) : (implementationProvider, false);
    }

    /// <summary>The <see cref="ExecutionPhase"/> a node's <see cref="NodeRole"/> runs under, or
    /// <see langword="null"/> for a role with no model phase (ADR 0006: finalization is
    /// deterministic code, not a model phase; the same holds for every other non-model role in the
    /// built-in graph — intake, confirmation, test-work, human approval).</summary>
    public static ExecutionPhase? PhaseFor(NodeRole role) => role switch
    {
        NodeRole.Planning => ExecutionPhase.Planning,
        NodeRole.Implementation => ExecutionPhase.Implementation,
        NodeRole.Review => ExecutionPhase.Review,
        _ => null,
    };

    /// <summary>True when <paramref name="capabilityId"/> is present in <paramref name="profile"/>'s
    /// allowlist and is not itself human-only — the "cannot widen them or invoke human-only
    /// commands" check a future node executor enforces per request, not just at freeze time.</summary>
    public static bool IsCapabilityAllowed(ExecutionProfile profile, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilityId);
        return !HumanOnlyCapabilityIds.Contains(capabilityId) &&
            profile.CapabilityAllowlist.Contains(capabilityId, StringComparer.Ordinal);
    }

    private static ExecutionProfile BuildProfile(
        ExecutionPhase phase,
        string provider,
        ProviderCatalog catalog,
        string effort,
        int sessionDeadlineSeconds,
        int idleDeadlineSeconds,
        ExecutionLineage? lineage)
    {
        // A human-only id can never reach an allowlist this policy itself builds (`CapabilityAllowlist`
        // is a fixed constant above) — asserted defensively so a future edit to that constant fails
        // loudly here instead of silently shipping a widened allowlist.
        System.Diagnostics.Debug.Assert(
            CapabilityAllowlist.All(id => !HumanOnlyCapabilityIds.Contains(id)),
            "The fixed capability allowlist must never contain a human-only capability id.");
        return new(
            ExecutionProfile.ContractVersion,
            phase,
            provider,
            ModelFor(provider, catalog),
            effort,
            SandboxPolicy,
            PermissionPolicy,
            CapabilityAllowlist,
            sessionDeadlineSeconds,
            idleDeadlineSeconds,
            lineage);
    }

    private static string ModelFor(string providerId, ProviderCatalog catalog) =>
        catalog.TryGet(new ProviderId(providerId), out ILlmProvider? provider)
            ? provider.DefaultModel
            : throw new InvalidOperationException(
                $"Frozen provider '{providerId}' is not registered in the provider catalog.");
}
