using System.Diagnostics.CodeAnalysis;

namespace Forge.Providers;

/// <summary>
/// Indexes every registered <see cref="ILlmProvider"/> by <see cref="ProviderId"/>, in DI
/// composition order (ADR 0008: "a provider catalog indexes registrations by ProviderId and
/// rejects duplicates"). Construction throws if two providers share an id: that is a
/// composition-root wiring bug, never a runtime or user condition to recover from.
/// </summary>
public sealed class ProviderCatalog
{
    private readonly Dictionary<ProviderId, ILlmProvider> byId;

    public ProviderCatalog(IEnumerable<ILlmProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Providers = [.. providers];
        byId = new Dictionary<ProviderId, ILlmProvider>(Providers.Count);
        foreach (ILlmProvider provider in Providers)
        {
            if (!byId.TryAdd(provider.Id, provider))
            {
                throw new InvalidOperationException(
                    $"Two providers are registered for id '{provider.Id.Value}'.");
            }
        }
    }

    /// <summary>Every registered provider, in DI composition order.</summary>
    public IReadOnlyList<ILlmProvider> Providers { get; }

    public bool Contains(ProviderId id) => byId.ContainsKey(id);

    public bool TryGet(ProviderId id, [NotNullWhen(true)] out ILlmProvider? provider) =>
        byId.TryGetValue(id, out provider);

    /// <summary>
    /// Resolves the ADR 0008 provider-enablement policy: omission (<paramref name="enabledIds"/>
    /// is <see langword="null"/>) selects every registered provider in composition order; a
    /// non-null list is the exact enabled set and fallback priority, in the given order. An id
    /// with no matching registration is dropped rather than throwing — write-time validation
    /// (<c>ForgeApplication.SetConfigurationAsync</c>) is what actually stops a user from saving
    /// such a list, so a stale reference surviving here (e.g. after a Forge upgrade removes a
    /// provider) degrades gracefully instead of crashing every subsequent probe.
    /// </summary>
    public IReadOnlyList<ILlmProvider> ResolveEnabled(IReadOnlyList<string>? enabledIds)
    {
        if (enabledIds is null)
        {
            return Providers;
        }

        List<ILlmProvider> resolved = new(enabledIds.Count);
        foreach (string id in enabledIds)
        {
            if (TryGet(new ProviderId(id), out ILlmProvider? provider))
            {
                resolved.Add(provider);
            }
        }

        return resolved;
    }
}
