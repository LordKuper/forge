namespace Forge.Providers;

/// <summary>Aggregates every registered provider into one toolchain-wide status.</summary>
public sealed class ProviderToolchainManager(IEnumerable<ILlmProvider> providers) : IProviderToolchainManager
{
    private readonly IReadOnlyList<ILlmProvider> providers =
        providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));

    public async Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken)
    {
        List<ProviderStatus> statuses = [];
        foreach (ILlmProvider provider in providers)
        {
            statuses.Add(await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false));
        }

        return new(statuses);
    }

    public async Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        List<ProviderStatus> statuses = [];
        foreach (ILlmProvider provider in providers)
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
}
