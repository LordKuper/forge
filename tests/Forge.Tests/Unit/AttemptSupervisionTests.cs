using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class AttemptSupervisionTests
{
    private static readonly TimeSpan LongDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(75);

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
        using AttemptSupervisor supervisor = new(LongDeadline, ShortDeadline, CancellationToken.None);

        AttemptSupervisionResult<int> result = await supervisor.SuperviseAsync<int>(
            async (token, onActivity) =>
            {
                int beats = 0;
                // Each beat arrives well inside the idle window, and there are enough beats that
                // their total span exceeds one idle window several times over.
                for (int i = 0; i < 6 && !token.IsCancellationRequested; i++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(30), CancellationToken.None).ConfigureAwait(false);
                    await onActivity(AttemptActivityKind.Heartbeat, token).ConfigureAwait(false);
                    beats++;
                }

                return beats;
            });

        Assert.Equal(AttemptTerminationReason.None, result.Reason);
        Assert.Equal(6, result.Value);
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
