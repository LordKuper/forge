namespace Forge.Application;

/// <summary>
/// The most recent Forge Host reachability <see cref="RemoteForgeMutations"/> actually observed
/// while attempting a mutation (plan 12.6: the status row "distinguishes... Host connectivity").
/// This is deliberately never a live probe: ADR 0005 starts the Host lazily "on the first actual
/// mutation," so a read-only surface (the Desktop sidebar) must not force that launch side effect
/// just to render a status indicator. <see cref="LastObserved"/> is <see langword="null"/> until the
/// first mutation attempt this process makes -- an honest "not yet determined," the same convention
/// this codebase already uses for <see cref="Forge.Providers.ProviderHealthEntry.Authentication"/>'s
/// null state and ADR 0052's quota <c>Unknown</c>.
/// </summary>
public interface IHostConnectivityMonitor
{
    (bool Connected, DateTimeOffset ObservedAt)? LastObserved { get; }

    void Report(bool connected, DateTimeOffset observedAt);
}

/// <summary>
/// Process-lifetime shared instance: one <see cref="HostConnectivityMonitor"/> is threaded through
/// every <see cref="RemoteForgeMutations"/> a composition root creates (via
/// <see cref="HostMutationsFactory"/>) so <see cref="Report"/> always reflects the outcome of the
/// most recent real mutation attempt, from any project, across the whole process.
/// </summary>
public sealed class HostConnectivityMonitor : IHostConnectivityMonitor
{
    private (bool Connected, DateTimeOffset ObservedAt)? lastObserved;

    public (bool Connected, DateTimeOffset ObservedAt)? LastObserved => lastObserved;

    public void Report(bool connected, DateTimeOffset observedAt) => lastObserved = (connected, observedAt);
}
