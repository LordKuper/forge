using Forge.Application;
using Forge.Providers;

namespace Forge.Tests.Support;

/// <summary>Reports a fixed lookup result without ever touching the network.</summary>
internal sealed class FakeReleaseSource(ProviderReleaseLookupResult result) : IProviderReleaseSource
{
    public Task<ProviderReleaseLookupResult> FetchLatestVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(result);
}

/// <summary>An in-memory, per-<see cref="ProviderId"/> cache — a fresh instance behaves exactly
/// like the adapter tests' old always-null stub until something is actually written to it.</summary>
internal sealed class FakeReleaseCache : IProviderReleaseCache
{
    private readonly Dictionary<string, ProviderReleaseCacheEntry> entries = new(StringComparer.Ordinal);

    public Task<ProviderReleaseCacheEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken) =>
        Task.FromResult(entries.TryGetValue(id.Value, out ProviderReleaseCacheEntry? entry) ? entry : null);

    public Task WriteAsync(ProviderId id, ProviderReleaseCacheEntry entry, CancellationToken cancellationToken)
    {
        entries[id.Value] = entry;
        return Task.CompletedTask;
    }
}

/// <summary>Acquires immediately (or never, with <paramref name="acquires"/> false) without any
/// real cross-process contention.</summary>
internal sealed class FakeInstallLock(bool acquires = true) : IProviderInstallLock
{
    public Task<IProviderInstallLease?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult<IProviderInstallLease?>(acquires ? new NoOpLease() : null);

    private sealed class NoOpLease : IProviderInstallLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Returns a fixed response body without ever touching the network.</summary>
internal sealed class FakeNetworkClient(string body) : INetworkClient
{
    public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
}

/// <summary>Simulates a network-layer failure (unreachable host, DNS failure, ...).</summary>
internal sealed class ThrowingNetworkClient : INetworkClient
{
    public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
        throw new HttpRequestException("The endpoint is unreachable.");
}
