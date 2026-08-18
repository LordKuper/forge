using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class AttemptSupervisionTests
{
    private static readonly TimeSpan LongDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(150);

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0, 1, "sessionDeadline")]
    [InlineData(-1, 1, "sessionDeadline")]
    [InlineData(1, 0, "idleDeadline")]
    [InlineData(1, -1, "idleDeadline")]
    public void ConstructorRejectsANonPositiveDeadline(double sessionSeconds, double idleSeconds, string paramName)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AttemptSupervisor(
                TimeSpan.FromSeconds(sessionSeconds), TimeSpan.FromSeconds(idleSeconds), CancellationToken.None));

        Assert.Equal(paramName, error.ParamName);
    }

    /// <summary>Regression test for the leak a prior review round found: a deadline above
    /// <c>Timer</c>'s own due-time ceiling used to throw from inside <c>new Timer(...)</c> after
    /// the linked <see cref="CancellationTokenSource"/> and registration already existed, which
    /// nothing could then dispose since the constructor never returned an instance to call
    /// <see cref="AttemptSupervisor.Dispose"/> on.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(50, 1, "sessionDeadline")]
    [InlineData(1, 50, "idleDeadline")]
    public void ConstructorRejectsADeadlineAboveTheTimerCeiling(double sessionDays, double idleDays, string paramName)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AttemptSupervisor(
                TimeSpan.FromDays(sessionDays), TimeSpan.FromDays(idleDays), CancellationToken.None));

        Assert.Equal(paramName, error.ParamName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuperviseAsyncReturnsNoneWhenTheWorkCompletesWithinBothDeadlines()
    {
        using AttemptSupervisor supervisor = new(LongDeadline, LongDeadline, CancellationToken.None);

        AttemptSupervisionResult<string> result = await supervisor.SuperviseAsync<string>(
            (_, _) => Task.FromResult("done"));

        Assert.Equal(AttemptTerminationReason.None, result.Reason);
        Assert.Equal("done", result.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SessionDeadlineCancelsTheTokenAndIsReportedAsTheReason()
    {
        using AttemptSupervisor supervisor = new(ShortDeadline, LongDeadline, CancellationToken.None);

        AttemptSupervisionResult<string> result = await supervisor.SuperviseAsync<string>(
            async (token, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable: only cancellation ends the delay.");
            });

        Assert.Equal(AttemptTerminationReason.SessionTimeout, result.Reason);
        Assert.Null(result.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IdleDeadlineFiresWhenNoActivityIsReported()
    {
        using AttemptSupervisor supervisor = new(LongDeadline, ShortDeadline, CancellationToken.None);

        AttemptSupervisionResult<string> result = await supervisor.SuperviseAsync<string>(
            async (token, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable: only cancellation ends the delay.");
            });

        Assert.Equal(AttemptTerminationReason.IdleTimeout, result.Reason);
    }

    /// <summary>ADR 0006: "Any bounded stream activity resets the idle deadline." Activity arriving
    /// faster than the idle window elapses must keep the run alive well past what a single idle
    /// window alone would allow.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ActivityResetsTheIdleDeadlineSoRepeatedActivityOutlivesASingleIdleWindow()
    {
        // A generous 5x margin between the per-beat gap (40ms) and the idle window (200ms) keeps
        // this reliable under real scheduler jitter (observed flaky on slower CI runners at a
        // tighter margin), while the 8 beats' combined ~320ms span still clearly outlives a single
        // 200ms window if activity were not actually resetting it.
        TimeSpan idleDeadline = TimeSpan.FromMilliseconds(200);
        using AttemptSupervisor supervisor = new(LongDeadline, idleDeadline, CancellationToken.None);

        AttemptSupervisionResult<int> result = await supervisor.SuperviseAsync<int>(
            async (token, onActivity) =>
            {
                int beats = 0;
                for (int i = 0; i < 8 && !token.IsCancellationRequested; i++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(40), CancellationToken.None).ConfigureAwait(false);
                    await onActivity(AttemptActivityKind.Heartbeat, token).ConfigureAwait(false);
                    beats++;
                }

                return beats;
            });

        Assert.Equal(AttemptTerminationReason.None, result.Reason);
        Assert.Equal(8, result.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheCallersOwnCancellationIsReportedDistinctlyFromEitherDeadline()
    {
        using CancellationTokenSource callerCancellation = new();
        using AttemptSupervisor supervisor = new(LongDeadline, LongDeadline, callerCancellation.Token);

        Task<AttemptSupervisionResult<string>> resultTask = supervisor.SuperviseAsync<string>(
            async (token, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable: only cancellation ends the delay.");
            });
        await callerCancellation.CancelAsync();
        AttemptSupervisionResult<string> result = await resultTask;

        Assert.Equal(AttemptTerminationReason.Cancelled, result.Reason);
    }

    /// <summary>An exception the supervised work raises for a reason unrelated to this supervisor
    /// (an ordinary provider failure surfacing as a thrown exception, not a returned
    /// <c>ProviderRunResult</c>) must propagate unchanged -- <see cref="AttemptSupervisor"/> only
    /// intercepts cancellation it caused itself.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnrelatedExceptionFromTheWorkPropagatesUnchanged()
    {
        using AttemptSupervisor supervisor = new(LongDeadline, LongDeadline, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.SuperviseAsync<string>((_, _) => throw new InvalidOperationException("boom")));
    }

    /// <summary>Regression test: an earlier version matched any <see cref="OperationCanceledException"/>
    /// once <c>Reason</c> was non-<see cref="AttemptTerminationReason.None"/>, regardless of which
    /// token the exception actually carried -- misattributing a cancellation the supervised work
    /// raised for its own, unrelated reason to this supervisor's deadline, purely because of a
    /// coincidental timing overlap. The fix checks the exception's own
    /// <see cref="OperationCanceledException.CancellationToken"/> against <see cref="AttemptSupervisor.Token"/>.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnOperationCanceledExceptionFromAnUnrelatedTokenPropagatesEvenWhileAReasonIsLatched()
    {
        using AttemptSupervisor supervisor = new(ShortDeadline, LongDeadline, CancellationToken.None);
        using CancellationTokenSource unrelated = new();
        await unrelated.CancelAsync();

        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => supervisor.SuperviseAsync<string>(
                async (_, _) =>
                {
                    // Wait for the session timeout to actually latch a reason, so the
                    // misattribution this test regresses against is a real possibility, not
                    // trivially absent because Reason was still None. Bounded, not an unconditional
                    // spin: a regression that stops latching must fail this test, not hang the run.
                    DateTime waitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (supervisor.Reason == AttemptTerminationReason.None)
                    {
                        Assert.True(
                            DateTime.UtcNow < waitDeadline,
                            "Expected the session timeout to latch a reason within 5 seconds.");
                        await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                    }

                    unrelated.Token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Unreachable: the token above is already cancelled.");
                }));

        Assert.Equal(unrelated.Token, thrown.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisposeIsSafeAfterNormalCompletionAndOnActivityAfterDisposeDoesNotThrow()
    {
        AttemptSupervisor supervisor = new(LongDeadline, LongDeadline, CancellationToken.None);
        supervisor.Dispose();

        Exception? exception = await Record.ExceptionAsync(
            () => supervisor.OnActivityAsync(AttemptActivityKind.Heartbeat, CancellationToken.None));

        Assert.Null(exception);
    }
}
