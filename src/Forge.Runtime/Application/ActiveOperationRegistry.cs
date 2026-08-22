using System.Collections.Concurrent;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// Host-owned, in-memory map from a live attempt to the linked <see cref="CancellationTokenSource"/>
/// its executor supervises the attempt's provider/process run with (plan section 7.3). Exposes
/// cancellation only, never workflow policy: the stop coordinator's only lever over a live
/// operation is "make its token observe cancellation," the same mechanism
/// <see cref="AttemptSupervisor"/>'s own session/idle deadlines already use.
///
/// Every entry is lost on a Host restart by construction -- nothing here is durable. That is by
/// design: <see cref="StopOperationCoordinator"/> persists the stop intent before ever touching
/// this registry, so a Host crash between a stop request and this registry observing it converges
/// through durable state on the next tick instead of depending on this map surviving the crash.
/// </summary>
public sealed class ActiveOperationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> operations = new();

    /// <summary>Registers <paramref name="attemptId"/> as the exact operation an executor is about
    /// to run a provider/process for, before that execution starts (plan section 7.3). The returned
    /// source's <see cref="CancellationTokenSource.Token"/> is linked to
    /// <paramref name="externalToken"/>, so deadline/host-shutdown cancellation still propagates
    /// through it unchanged. The caller must call <see cref="Unregister"/> in a `finally` block once
    /// the run completes -- registering the same attempt twice without an intervening unregister is
    /// a caller bug, not a runtime condition, and throws.</summary>
    public CancellationTokenSource Register(AttemptId attemptId, CancellationToken externalToken)
    {
        ArgumentNullException.ThrowIfNull(attemptId);
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        if (!operations.TryAdd(attemptId.Value, source))
        {
            source.Dispose();
            throw new InvalidOperationException(
                $"Attempt '{attemptId.Value}' is already registered as an active operation.");
        }

        return source;
    }

    /// <summary>Removes and disposes the registration for <paramref name="attemptId"/>, if one
    /// exists. Safe to call even when nothing is registered (e.g. the run already unregistered
    /// itself, or this Host process never registered it at all).</summary>
    public void Unregister(AttemptId attemptId)
    {
        ArgumentNullException.ThrowIfNull(attemptId);
        if (operations.TryRemove(attemptId.Value, out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }

    /// <summary>Best-effort cancellation: <see langword="false"/> when no live registration exists
    /// for <paramref name="attemptId"/> -- already finished, or this Host process never registered
    /// it (e.g. after a crash and restart, where the durable stop intent an executor checks before
    /// resuming is what protects the attempt instead). Never throws for a missing or already-settled
    /// registration.</summary>
    public bool TryCancel(AttemptId attemptId)
    {
        ArgumentNullException.ThrowIfNull(attemptId);
        if (!operations.TryGetValue(attemptId.Value, out CancellationTokenSource? source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Unregistered (and disposed) concurrently with this call -- the run already finished on
            // its own, which is not a failure to report.
            return false;
        }
    }
}
