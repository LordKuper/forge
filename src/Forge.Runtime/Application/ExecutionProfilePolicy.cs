using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

/// <summary>
/// Resolves and validates the three frozen <see cref="ExecutionProfile"/>s ADR 0006 requires
/// (ADR 0014). <see cref="Freeze"/> is pure and deterministic given already-frozen inputs
/// (<see cref="SprintDefinition.FrozenProviders"/> and one already-resolved model per provider) — no
/// I/O, no clock, so <see cref="SprintOrchestrator.CreateSprintAsync"/> can call it once at freeze
/// time and get the exact same result on any resumed retry. <see cref="ResolveModelsAsync"/> is the
/// one member that is not: producing those already-resolved models is what asks each adapter to
/// refresh (ADR 0063), and it is deliberately a separate, earlier step for that reason.
/// </summary>
public static class ExecutionProfilePolicy
{
    // ponytail: every phase shares one fixed MVP policy (sandbox/permission/allowlist), and effort
    // only distinguishes review from the other two. ADR 0042 added the allowlist half of ADR 0006's
    // "project model policy" but not per-phase model *selection*: ResolveModelsAsync below resolves
    // ONE model per provider -- refreshing it live from the vendor where the adapter can (ADR 0063) --
    // and every phase of that provider then freezes that same value, which ModelPolicyGate validates
    // in SprintOrchestrator.CreateSprintAsync between those two steps. Revisit once real per-project,
    // per-phase model selection exists.
    private const string SandboxPolicy = "workspace-write";
    private const string PermissionPolicy = "never";
    private static readonly IReadOnlyList<string> CapabilityAllowlist =
        [ContextCapabilityIds.GitShow, ContextCapabilityIds.GitGrep];

    /// <summary>
    /// Freezes exactly three profiles — <see cref="ExecutionPhase.Planning"/>,
    /// <see cref="ExecutionPhase.Implementation"/>, and <see cref="ExecutionPhase.Review"/> — from
    /// <paramref name="frozenProviders"/> (already ADR 0008's ordered candidate intersection) and
    /// <paramref name="models"/>. Planning and implementation both use the highest-priority
    /// candidate; review prefers the highest-priority candidate whose id differs from the
    /// implementation phase's provider (lineage independence), falling back to the same provider —
    /// recording <see cref="ExecutionLineage.AchievedIndependence"/> either way, never blocking.
    ///
    /// Takes <paramref name="models"/> already resolved by <see cref="ResolveModelsAsync"/> rather than
    /// reading <see cref="ILlmProvider.DefaultModel"/> itself, and deliberately has no
    /// catalog-taking overload: ADR 0063 makes that property resolvable at runtime, so every read is
    /// a separate answer. <see cref="SprintOrchestrator.CreateSprintAsync"/> must validate the exact
    /// value it is about to freeze (<see cref="ModelPolicyGate"/>) with durable I/O in between, and
    /// keeping the resolution in the caller's hands is what makes it structurally impossible for the
    /// approved model and the frozen model to be two different readings.
    /// </summary>
    public static IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> Freeze(
        IReadOnlyList<string> frozenProviders,
        IReadOnlyDictionary<string, string> models)
    {
        ArgumentNullException.ThrowIfNull(frozenProviders);
        ArgumentNullException.ThrowIfNull(models);
        if (frozenProviders.Count == 0)
        {
            throw new ArgumentException(
                "At least one frozen provider is required to freeze execution profiles.", nameof(frozenProviders));
        }

        string implementationProvider = frozenProviders[0];
        (string reviewProvider, bool achievedIndependence) =
            SelectReviewProvider(frozenProviders, implementationProvider);

        // One model per DISTINCT provider, reused everywhere it appears — including the
        // single-provider case, where review falls back to the implementation provider and must
        // therefore record the same model, not a second reading of it. A lineage claiming the
        // implementation ran on a model the implementation profile does not name is unreadable
        // evidence.
        string implementationModel = ModelFor(implementationProvider, models);
        string reviewModel = string.Equals(reviewProvider, implementationProvider, StringComparison.Ordinal)
            ? implementationModel
            : ModelFor(reviewProvider, models);

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

    /// <summary>
    /// Refreshes and then reads each distinct frozen provider's
    /// <see cref="ILlmProvider.DefaultModel"/>, exactly once per provider. This is the single
    /// resolution point ADR 0063 requires, and both halves of it matter:
    ///
    /// The refresh is here rather than left to the provider-capability pass because that pass does
    /// not run in every process that creates a sprint. The Forge Host serves Desktop and remote
    /// sprint creation and never probes providers, so its own adapter instances would freeze the
    /// unresolved sentinel indefinitely if this method only read. Asking each provider to refresh
    /// itself here makes resolution reachable from every creating process by construction instead of
    /// depending on which one happens to have run a capability check. It is cheap: an adapter
    /// throttles through its own cross-process cache, so a fresh entry — including one another
    /// process wrote — returns without any vendor work, and a real probe happens only when the answer
    /// is genuinely stale, which is exactly when one is needed. A failed refresh is not an error
    /// here: the adapter leaves <see cref="ILlmProvider.DefaultModel"/> as it was (its unresolved
    /// sentinel, or the last known-good value) and creation proceeds on that, subject to
    /// <see cref="ModelPolicyGate"/> like any other model.
    ///
    /// The single read is what makes the resolved value one answer: the property is resolvable at
    /// runtime, so every read of it is a separate answer, and a sprint is one decision about one set
    /// of models.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveModelsAsync(
        IReadOnlyList<string> frozenProviders,
        ProviderCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frozenProviders);
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<string, string> models = new(StringComparer.Ordinal);
        foreach (string providerId in frozenProviders)
        {
            if (models.ContainsKey(providerId))
            {
                continue;
            }

            if (!catalog.TryGet(new ProviderId(providerId), out ILlmProvider? provider))
            {
                throw new InvalidOperationException(
                    $"Frozen provider '{providerId}' is not registered in the provider catalog.");
            }

            // Sequential, matching ProviderToolchainManager's own loop: a refresh may spawn a vendor
            // process, and two at once buys nothing on a path that runs once per sprint creation.
            // `bypassCache: false` — sprint creation honours the throttle; only `forge models
            // --refresh` bypasses it.
            await provider.RefreshDefaultModelAsync(false, cancellationToken).ConfigureAwait(false);
            models.Add(providerId, provider.DefaultModel);
        }

        return models;
    }

    /// <summary>
    /// Applies an operator's explicit per-sprint model choice (ADR 0066) on top of the map
    /// <see cref="ResolveModelsAsync"/> just produced, replacing only
    /// <paramref name="providerId"/>'s entry. Returns <see langword="null"/> when the request is not
    /// selectable, which the caller turns into a refusal before anything is written.
    ///
    /// One entry, not all of them, because a model id is provider-specific: ADR 0014 already freezes
    /// one model per DISTINCT provider, so overriding the primary provider's entry is what reaches
    /// planning, implementation, and — in the single-provider case, where review falls back to that
    /// same provider — review and its lineage too. A review phase that ran on a genuinely different
    /// provider keeps that provider's own resolved default, because the requested id means nothing to
    /// it. Nothing downstream changes: the caller's existing <see cref="ModelPolicyGate"/> check and
    /// <see cref="Freeze"/> both read this map, so the requested model is gated and frozen by exactly
    /// the code the default already goes through, and omitting the request leaves that path untouched.
    ///
    /// Two checks, in order. <c>NormalizeModelName</c> is unconditional: a requested id becomes both a
    /// vendor command-line argument and durable sprint state, and it arrives from a caller rather than
    /// from a vendor, so it earns at least the hygiene a probed value gets. The enumeration check is
    /// conditional on there BEING an enumeration — an empty
    /// <see cref="ILlmProvider.ListModelsAsync"/> means the vendor could not be asked, not that it
    /// offers nothing, and refusing every explicit choice whenever a vendor probe is unavailable would
    /// trade a rare bad run for a common blocked one. The check that actually protects policy,
    /// <see cref="ModelPolicyGate"/>, is unconditional and runs regardless.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>?> ApplyRequestedModelAsync(
        IReadOnlyDictionary<string, string> models,
        string providerId,
        string requestedModel,
        ProviderCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(requestedModel);
        ArgumentNullException.ThrowIfNull(catalog);
        if (ProviderInstallation.NormalizeModelName(requestedModel) is not { } requested)
        {
            return null;
        }

        if (!catalog.TryGet(new ProviderId(providerId), out ILlmProvider? provider))
        {
            throw new InvalidOperationException(
                $"Frozen provider '{providerId}' is not registered in the provider catalog.");
        }

        IReadOnlyList<string> selectable = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (selectable.Count > 0 && !selectable.Contains(requested, StringComparer.Ordinal))
        {
            return null;
        }

        return new Dictionary<string, string>(models, StringComparer.Ordinal) { [providerId] = requested };
    }

    private static string ModelFor(string providerId, IReadOnlyDictionary<string, string> models) =>
        models.TryGetValue(providerId, out string? model)
            ? model
            : throw new InvalidOperationException(
                $"Frozen provider '{providerId}' is not registered in the provider catalog.");
}
