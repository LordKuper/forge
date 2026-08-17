using Forge.Domain;

namespace Forge.Application;

/// <summary>ADR 0006: "The durable outcome distinguishes provider_idle_timeout,
/// provider_session_timeout, user cancellation, and ordinary provider failure."</summary>
public enum AttemptTerminationReason
{
    /// <summary>Neither deadline fired and the caller's own token was never cancelled; whatever
    /// outcome the supervised work itself produced (success or an ordinary provider failure)
    /// stands as-is.</summary>
    None,
    IdleTimeout,
    SessionTimeout,
    Cancelled,
}

/// <summary><paramref name="Value"/> is only meaningful when <paramref name="Reason"/> is
/// <see cref="AttemptTerminationReason.None"/> -- the supervised work completed (successfully or
/// not) on its own. Any other reason means the work was forcibly cancelled mid-flight and its own
/// result (if it produced one at all) is not the authoritative outcome; <paramref name="Reason"/>
/// is.</summary>
public sealed record AttemptSupervisionResult<T>(AttemptTerminationReason Reason, T? Value);

/// <summary>
/// ADR 0006's two frozen per-attempt deadlines: "an absolute session deadline and an idle
/// deadline. Any bounded stream activity resets the idle deadline; model wording does not...
/// Cancellation or either deadline terminates the entire owned process tree." The actual
/// process-tree termination is already <see cref="Forge.Infrastructure.ProcessRunner"/>'s own
/// `Process.Kill(entireProcessTree: true)`, reached the same way any other cancellation reaches
/// it: through <see cref="Token"/>. This class only decides *why* a run was terminated, distinctly
/// from an ordinary provider failure that never involved either deadline or the caller's own
/// cancellation -- first cause wins.
/// </summary>
public sealed class AttemptSupervisor : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource linkedCancellation;
    private readonly CancellationTokenRegistration callerRegistration;
    private readonly Timer sessionTimer;
    private readonly Timer idleTimer;
    private readonly TimeSpan idleDeadline;
    private AttemptTerminationReason reason = AttemptTerminationReason.None;
    private bool disposed;

    public AttemptSupervisor(TimeSpan sessionDeadline, TimeSpan idleDeadline, CancellationToken cancellationToken)
    {
        this.idleDeadline = idleDeadline;
        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callerRegistration = cancellationToken.Register(() => Latch(AttemptTerminationReason.Cancelled));
        sessionTimer = new Timer(
            _ => Fire(AttemptTerminationReason.SessionTimeout), null, sessionDeadline, Timeout.InfiniteTimeSpan);
        idleTimer = new Timer(
            _ => Fire(AttemptTerminationReason.IdleTimeout), null, idleDeadline, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The token to hand to the supervised work in place of the caller's own token.
    /// Cancelling the caller's token also cancels this one (it is linked), so a genuinely
    /// cancellation-aware piece of work needs only ever look at this single token.</summary>
    public CancellationToken Token => linkedCancellation.Token;

    /// <summary><see cref="AttemptTerminationReason.None"/> until a deadline fires or the caller's
    /// token is cancelled; latched permanently to whichever cause happened first.</summary>
    public AttemptTerminationReason Reason
    {
        get
        {
            lock (gate)
            {
                return reason;
            }
        }
    }

    /// <summary>Pass as the supervised work's activity callback (e.g. `ILlmProvider.RunAsync`'s
    /// `onActivity`): "any bounded stream activity resets the idle deadline" -- every parsed
    /// provider event, regardless of kind, counts; this deliberately never inspects
    /// <paramref name="kind"/> or any event text, matching "model wording does not" reset it.
    /// </summary>
    public Task OnActivityAsync(AttemptActivityKind kind, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!disposed)
            {
                idleTimer.Change(idleDeadline, Timeout.InfiniteTimeSpan);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with <see cref="Token"/> and <see cref="OnActivityAsync"/>,
    /// translating a cancellation this supervisor itself caused (either deadline, or the caller's
    /// own token) into a classified result instead of letting <see cref="OperationCanceledException"/>
    /// propagate -- the same self-cancellation pattern <see cref="Forge.Providers.ProviderExecution"/>
    /// already uses for its own bound violations. An exception <paramref name="work"/> raises for
    /// any other reason still propagates unchanged.
    /// </summary>
    public async Task<AttemptSupervisionResult<T>> SuperviseAsync<T>(
        Func<CancellationToken, Func<AttemptActivityKind, CancellationToken, Task>, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            T value = await work(Token, OnActivityAsync).ConfigureAwait(false);
            return new(Reason, value);
        }
        catch (OperationCanceledException) when (Reason != AttemptTerminationReason.None)
        {
            return new(Reason, default);
        }
    }

    private void Latch(AttemptTerminationReason candidate)
    {
        lock (gate)
        {
            if (reason == AttemptTerminationReason.None)
            {
                reason = candidate;
            }
        }
    }

    private void Fire(AttemptTerminationReason candidate)
    {
        Latch(candidate);
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
        }

        try
        {
            linkedCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with this timer callback firing; nothing left to cancel.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }

        callerRegistration.Dispose();
        sessionTimer.Dispose();
        idleTimer.Dispose();
        linkedCancellation.Dispose();
    }
}
