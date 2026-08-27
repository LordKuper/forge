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
    // ponytail: every phase shares one fixed MVP policy (sandbox/permission/allowlist), and effort
    // only distinguishes review from the other two. ADR 0042 added the allowlist half of ADR 0006's
    // "project model policy" (SprintOrchestrator.CreateSprintAsync refuses creation via
    // ModelPolicyGate before this class ever runs) but not per-phase model *selection* -- every
    // phase still resolves the same fixed ILlmProvider.DefaultModel. Revisit once that exists.
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

        // Each DISTINCT provider's model is read exactly ONCE per freeze and reused everywhere it
        // appears — including the single-provider case, where review falls back to the implementation
        // provider and must therefore record the same model, not a second reading of it.
        // ADR 0063 lets an adapter resolve its DefaultModel from the vendor during a provider check,
        // so the property can change value within one process lifetime; re-reading it per profile
        // would let a refresh landing mid-freeze produce a sprint whose implementation profile and
        // review lineage disagree about which model the implementation ran on.
        string implementationModel = ModelFor(implementationProvider, catalog);
        string reviewModel = string.Equals(reviewProvider, implementationProvider, StringComparison.Ordinal)
            ? implementationModel
            : ModelFor(reviewProvider, catalog);

        return new Dictionary<ExecutionPhase, ExecutionProfile>
        {
            [ExecutionPhase.Planning] = BuildProfile(
                ExecutionPhase.Planning, implementationProvider, implementationModel, "medium", 1800, 180, null),
            [ExecutionPhase.Implementation] = BuildProfile(
                ExecutionPhase.Implementation, implementationProvider, implementationModel, "medium", 1800, 180, null),
            [ExecutionPhase.Review] = BuildProfile(
                ExecutionPhase.Review, reviewProvider, reviewModel, "high", 3600, 300,
                new ExecutionLineage(implementationProvider, implementationModel, achievedIndependence)),
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

    private static ExecutionProfile BuildProfile(
        ExecutionPhase phase,
        string provider,
        string model,
        string effort,
        int sessionDeadlineSeconds,
        int idleDeadlineSeconds,
        ExecutionLineage? lineage) =>
        new(
            ExecutionProfile.ContractVersion,
            phase,
            provider,
            model,
            effort,
            SandboxPolicy,
            PermissionPolicy,
            CapabilityAllowlist,
            sessionDeadlineSeconds,
            idleDeadlineSeconds,
            lineage);

    private static string ModelFor(string providerId, ProviderCatalog catalog) =>
        catalog.TryGet(new ProviderId(providerId), out ILlmProvider? provider)
            ? provider.DefaultModel
            : throw new InvalidOperationException(
                $"Frozen provider '{providerId}' is not registered in the provider catalog.");
}
