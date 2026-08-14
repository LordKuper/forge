namespace Forge.Providers;

/// <summary>
/// Aggregates every enabled provider into one toolchain-wide status. A disabled provider is
/// excluded before any probe: ADR 0008's "disabled providers are never discovered, installed,
/// updated, authenticated, or executed" — <see cref="CheckAsync"/> and
/// <see cref="EnsureReadyAsync"/> never call any member of an <see cref="ILlmProvider"/> that
/// <see cref="ProviderCatalog.ResolveEnabled"/> excludes.
/// </summary>
public sealed class ProviderToolchainManager(ProviderCatalog catalog, IProviderEnablementSource enablement)
    : IProviderToolchainManager
{
    private readonly ProviderCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IProviderEnablementSource enablement =
        enablement ?? throw new ArgumentNullException(nameof(enablement));

    public async Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ILlmProvider> enabled = await ResolveEnabledAsync(cancellationToken).ConfigureAwait(false);
        List<ProviderStatus> statuses = new(enabled.Count);
        foreach (ILlmProvider provider in enabled)
        {
            statuses.Add(await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false));
        }

        return new(statuses);
    }

    public async Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ILlmProvider> enabled = await ResolveEnabledAsync(cancellationToken).ConfigureAwait(false);
        List<ProviderStatus> statuses = new(enabled.Count);
        foreach (ILlmProvider provider in enabled)
        {
            ProviderStatus status = await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != ProviderState.Ready)
            {
                status = await provider.InstallOrUpdateAsync(cancellationToken).ConfigureAwait(false);
            }

            statuses.Add(status);
        }

        return new(statuses);
    }

    private async Task<IReadOnlyList<ILlmProvider>> ResolveEnabledAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? enabledIds =
            await enablement.GetEnabledIdsAsync(cancellationToken).ConfigureAwait(false);
        return catalog.ResolveEnabled(enabledIds);
    }
}
