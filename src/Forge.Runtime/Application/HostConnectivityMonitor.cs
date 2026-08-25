using System.Collections.Concurrent;

namespace Forge.Application;

/// <summary>
/// One immutable Host-reachability reading (PR #106 review finding 2): a reference-typed snapshot
/// rather than separate <c>Connected</c>/<c>ObservedAt</c> fields, so a reader can never observe one
/// half of a new reading paired with the other half of a stale one -- reference assignment (and
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>'s own internal synchronization) is atomic where two
/// independently-written struct fields would not be.
/// </summary>
public sealed record HostConnectivityReading(bool Connected, DateTimeOffset ObservedAt);

/// <summary>
/// The most recent Forge Host reachability <see cref="RemoteForgeMutations"/> actually observed
/// while attempting a mutation, keyed by project id (plan 12.6: the status row "distinguishes...
/// Host connectivity"; PR #106 review finding 5: Forge Hosts are per-project -- one pipe per
/// <c>InstanceIdentity.ComputePipeName(instanceId, projectId)</c>, matching <see cref="Forge.Host.Client.ForgeHostClient"/>'s
/// own scoping -- so a single process-global reading would let a mutation against project A's Host
/// misreport project B's, which may be unreachable, never started, or crashed).
/// This is deliberately never a live probe: ADR 0005 starts the Host lazily "on the first actual
/// mutation," so a read-only surface (the Desktop sidebar) must not force that launch side effect
/// just to render a status indicator. <see cref="LastObserved(Guid)"/> is <see langword="null"/> for a
/// project until the first mutation attempt against that project's Host this process makes -- an
/// honest "not yet determined," the same convention this codebase already uses for
/// <see cref="Forge.Providers.ProviderHealthEntry.Authentication"/>'s null state and ADR 0052's quota
/// <c>Unknown</c>.
/// </summary>
public interface IHostConnectivityMonitor
{
    HostConnectivityReading? LastObserved(Guid projectId);

    void Report(Guid projectId, bool connected, DateTimeOffset observedAt);
}

/// <summary>
/// Process-lifetime shared instance: one <see cref="HostConnectivityMonitor"/> is threaded through
/// every <see cref="RemoteForgeMutations"/> a composition root creates (via
/// <see cref="HostMutationsFactory"/>) so <see cref="Report(Guid, bool, DateTimeOffset)"/> always reflects the outcome of the most
/// recent real mutation attempt for each project independently, from any project, across the whole
/// process. <see cref="ConcurrentDictionary{TKey,TValue}"/> gives both per-project scoping (finding 5)
/// and thread-safe reads/writes (finding 2) without a separate lock: writes from thread-pool
/// continuations off <see cref="Forge.Host.Client.ForgeHostClient.EnsureConnectedAsync"/> and reads
/// from the UI thread can never observe a torn value.
/// </summary>
public sealed class HostConnectivityMonitor : IHostConnectivityMonitor
{
    private readonly ConcurrentDictionary<Guid, HostConnectivityReading> readings = new();

    public HostConnectivityReading? LastObserved(Guid projectId) =>
        readings.TryGetValue(projectId, out HostConnectivityReading? reading) ? reading : null;

    public void Report(Guid projectId, bool connected, DateTimeOffset observedAt) =>
        readings[projectId] = new HostConnectivityReading(connected, observedAt);
}
