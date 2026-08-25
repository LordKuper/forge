using Forge.Application;

namespace Forge.UnitTests;

/// <summary>
/// PR #106 review finding 2: <see cref="HostConnectivityMonitor"/> used to store a single
/// <c>(bool Connected, DateTimeOffset ObservedAt)?</c> struct field, written from thread-pool
/// continuations off <c>ForgeHostClient.EnsureConnectedAsync</c> and read from the UI thread with no
/// lock, <see langword="volatile"/>, or <see cref="System.Threading.Volatile"/> call. A struct that
/// wide is not written atomically, so a concurrent reader could observe a torn value: a new
/// <c>Connected</c> paired with a stale <c>ObservedAt</c>, or vice versa -- exactly the pairing
/// <see cref="Forge.Desktop.Presentation.SidebarViewModel"/>'s staleness check depends on being
/// self-consistent. The fix stores an immutable <see cref="HostConnectivityReading"/> record in a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>, so every read is a
/// single atomic reference load of a value that was fully constructed before it was ever published.
/// </summary>
public sealed class HostConnectivityMonitorTests
{
    /// <summary>Deterministic (non-flaky) proof of atomicity: each reported reading's
    /// <c>ObservedAt</c> encodes exactly which <c>Connected</c> value belongs with it (even ticks ->
    /// connected, odd ticks -> disconnected), so any reader that ever observes a mismatched pair --
    /// the actual torn-read defect -- fails a hard assertion instead of merely "looking different."
    /// One writer and one reader race for a large number of iterations with no external
    /// synchronization between them, matching the real production shape (one thread-pool continuation
    /// reporting, the UI thread reading).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReportAndLastObservedNeverExposeATornReadingUnderConcurrentAccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HostConnectivityMonitor monitor = new();
        Guid projectId = Guid.NewGuid();
        const int iterations = 100_000;
        const int writerCount = 4;
        const int readerCount = 4;
        monitor.Report(projectId, connected: true, DateTimeOffset.UnixEpoch);

        IEnumerable<Task> writers = Enumerable.Range(0, writerCount).Select(_ => Task.Run(
            () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    DateTimeOffset observedAt = DateTimeOffset.UnixEpoch.AddTicks(i);
                    bool connected = i % 2 == 0;
                    monitor.Report(projectId, connected, observedAt);
                }
            },
            cancellationToken));

        IEnumerable<Task> readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(
            () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    HostConnectivityReading? reading = monitor.LastObserved(projectId);
                    if (reading is { } value)
                    {
                        long ticksSinceEpoch = value.ObservedAt.Ticks - DateTimeOffset.UnixEpoch.Ticks;
                        bool expectedConnected = ticksSinceEpoch % 2 == 0;
                        Assert.Equal(expectedConnected, value.Connected);
                    }
                }
            },
            cancellationToken));

        await Task.WhenAll([.. writers, .. readers]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LastObservedIsNullForAProjectThatWasNeverReported()
    {
        HostConnectivityMonitor monitor = new();

        Assert.Null(monitor.LastObserved(Guid.NewGuid()));
    }
}
