using System.Diagnostics;
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
    private long lastActivityTimestamp;
    private AttemptTerminationReason reason = AttemptTerminationReason.None;
    private bool disposed;

    public AttemptSupervisor(TimeSpan sessionDeadline, TimeSpan idleDeadline, CancellationToken cancellationToken)
    {
        // Validated before any disposable resource is created: a rejected constructor must leak
        // nothing.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionDeadline, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleDeadline, TimeSpan.Zero);

        this.idleDeadline = idleDeadline;
        lastActivityTimestamp = Stopwatch.GetTimestamp();
        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callerRegistration = cancellationToken.Register(() => FireUnderLock(AttemptTerminationReason.Cancelled));
        sessionTimer = new Timer(
            _ => FireUnderLock(AttemptTerminationReason.SessionTimeout),
            null, sessionDeadline, Timeout.InfiniteTimeSpan);
        // Self-rescheduling rather than reset-via-Change from OnActivityAsync: a `Timer.Change`
        // call can never recall a callback the runtime has already dispatched, so resetting on
        // every activity would still leave a window where activity arriving right as the timer
        // fires produces a spurious idle timeout. Re-deriving the remaining time from an
        // authoritative last-activity timestamp on every tick closes that window instead of
        // merely narrowing it.
        idleTimer = new Timer(_ => CheckIdle(), null, idleDeadline, Timeout.InfiniteTimeSpan);
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
    /// <paramref name="kind"/> or any event text, matching "model wording does not" reset it. A
    /// single atomic timestamp write, never blocked by (or blocking) the timer lock.</summary>
    public Task OnActivityAsync(AttemptActivityKind kind, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with <see cref="Token"/> and <see cref="OnActivityAsync"/>,
    /// translating a cancellation this supervisor itself caused (either deadline, or the caller's
    /// own token) into a classified result instead of letting <see cref="OperationCanceledException"/>
    /// propagate -- the same self-cancellation pattern <see cref="Forge.Providers.ProviderExecution"/>
    /// already uses for its own bound violations. The exception's own
    /// <see cref="OperationCanceledException.CancellationToken"/> is checked against
    /// <see cref="Token"/>, not merely whether <see cref="Reason"/> happens to be non-`None` at
    /// that moment: a cancellation <paramref name="work"/> raises for a reason of its own (an
    /// unrelated token, coincidentally overlapping with an already-latched reason) must not be
    /// misattributed to this supervisor. Any exception that fails that check propagates unchanged.
    /// </summary>
    public async Task<AttemptSupervisionResult<T>> SuperviseAsync<T>(
        Func<CancellationToken, Func<AttemptActivityKind, CancellationToken, Task>, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        CancellationToken token = Token;
        try
        {
            T value = await work(token, OnActivityAsync).ConfigureAwait(false);
            return new(Reason, value);
        }
        catch (OperationCanceledException error) when (
            Reason != AttemptTerminationReason.None && error.CancellationToken == token)
        {
            return new(Reason, default);
        }
    }

    private void CheckIdle()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivityTimestamp));
            TimeSpan remaining = idleDeadline - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                LatchAndCancel(AttemptTerminationReason.IdleTimeout);
                return;
            }

            idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Acquires <see cref="gate"/> for the whole check-disposed-then-cancel sequence, so
    /// this can never run concurrently with <see cref="Dispose"/> disposing
    /// <see cref="linkedCancellation"/> out from under it -- a
    /// <see cref="CancellationTokenSource"/> otherwise documents concurrent
    /// <c>Cancel</c>/<c>Dispose</c> as unsupported.</summary>
    private void FireUnderLock(AttemptTerminationReason candidate)
    {
        lock (gate)
        {
            if (!disposed)
            {
                LatchAndCancel(candidate);
            }
        }
    }

    /// <summary>Must run under <see cref="gate"/>.</summary>
    private void LatchAndCancel(AttemptTerminationReason candidate)
    {
        if (reason == AttemptTerminationReason.None)
        {
            reason = candidate;
        }

        try
        {
            linkedCancellation.Cancel();
        }
        catch (Exception)
        {
            // Token is public: an arbitrary third-party registration on it (or on a token further
            // linked to it) throwing must never crash this timer callback -- this supervisor's own
            // job here is only to signal cancellation, not to own every other registration's
            // behavior.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            callerRegistration.Dispose();
            sessionTimer.Dispose();
            idleTimer.Dispose();
            linkedCancellation.Dispose();
        }
    }
}
