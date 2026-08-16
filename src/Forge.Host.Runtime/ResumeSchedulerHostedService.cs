using Forge.Application;
using Forge.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Host;

/// <summary>
/// How often <see cref="ResumeSchedulerHostedService"/> re-derives sprint readiness. Configurable
/// only so a test can prove the loop itself fires without waiting out the real interval.
/// </summary>
public sealed record ResumeSchedulerOptions(string ProjectRoot, TimeSpan? PollInterval = null)
{
    public TimeSpan Interval => PollInterval ?? TimeSpan.FromSeconds(15);
}

/// <summary>
/// ADR 0006: "Forge Host re-enqueues it idempotently after the timestamp" — a
/// <see cref="RouteDecision.Outcome"/> of <see cref="RouteOutcome.Deferred"/> leaves its node ready
/// but routing-deferred, and nothing else in this codebase re-visits it once the deferral's
/// <c>resume_not_before</c> elapses. This periodically calls <see cref="SprintScheduler.AdvanceGraphAsync"/>
/// for every sprint in the Host's own project — the same idempotent, safe-to-call-repeatedly
/// re-entry point every other state change already uses — so a routing decision that becomes
/// resumable again is picked back up without a human or client having to notice and retry it.
/// </summary>
/// <remarks>
/// Deliberately holds no per-sprint memory of what it last saw: like <see cref="RoutingLedger"/>
/// itself, every tick re-derives entirely from durable state, so a Host restart loses nothing and
/// two Hosts racing for the same project's lease can never double-fire — the losing Host never
/// starts this service at all (<see cref="ControlPlaneHostedService"/> stops it before listening).
/// </remarks>
public sealed class ResumeSchedulerHostedService(
    ResumeSchedulerOptions options,
    ISprintStore store,
    SprintScheduler scheduler,
    ILogger<ResumeSchedulerHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception> LogTickFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2010, "ResumeSchedulerTickFailed"),
        "Re-deriving sprint readiness failed for one sprint; continuing with the rest.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Interval);
        do
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SprintId> sprintIds;
        try
        {
            sprintIds = await store.ListAsync(options.ProjectRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogTickFailed(logger, exception);
            return;
        }

        foreach (SprintId sprintId in sprintIds)
        {
            try
            {
                await scheduler.AdvanceGraphAsync(options.ProjectRoot, sprintId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // One sprint's unreadable journal must never stop every other sprint in the
                // project from being re-derived — matches ControlPlaneHostedService's own
                // per-connection failure isolation.
                LogTickFailed(logger, exception);
            }
        }
    }
}
