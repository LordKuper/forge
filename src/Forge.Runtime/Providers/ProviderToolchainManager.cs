namespace Forge.Providers;

/// <summary>Aggregates every registered provider strategy into one toolchain-wide status.</summary>
public sealed class ProviderToolchainManager(IEnumerable<IProviderStrategy> strategies) : IProviderToolchainManager
{
    private readonly IReadOnlyList<IProviderStrategy> strategies =
        strategies?.ToArray() ?? throw new ArgumentNullException(nameof(strategies));

    public async Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken)
    {
        List<ProviderStatus> statuses = [];
        foreach (IProviderStrategy strategy in strategies)
        {
            statuses.Add(await strategy.DiscoverAsync(cancellationToken).ConfigureAwait(false));
        }

        return new(statuses);
    }

    public async Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        List<ProviderStatus> statuses = [];
        foreach (IProviderStrategy strategy in strategies)
        {
            ProviderStatus status = await strategy.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != ProviderState.Ready)
            {
                status = await strategy.InstallOrUpdateAsync(cancellationToken).ConfigureAwait(false);
            }

            statuses.Add(status);
        }

        return new(statuses);
    }
}
