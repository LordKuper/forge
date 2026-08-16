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
/// Nothing in this codebase re-visits a sprint node once its dependency settles outside of a
/// scheduler-driven call (e.g. a node completion recorded directly, bypassing
/// <see cref="SprintScheduler"/>'s own callers). This periodically calls
/// <see cref="SprintScheduler.AdvanceGraphAsync"/> for every sprint in the Host's own project — the
/// same idempotent, safe-to-call-repeatedly re-entry point every other state change already uses —
/// so a node left `pending` with satisfied dependencies is promoted to `ready` without a human or
/// client having to notice and retry it.
/// </summary>
/// <remarks>
/// This does not itself act on <see cref="RouteDecision.Outcome"/> == <see cref="RouteOutcome.Deferred"/>
/// or <c>resume_not_before</c>: no attempt executor exists yet to start a fresh attempt once a
/// routing deferral elapses (that is Stage 11's territory), so there is nothing here for this
/// service to re-enqueue on that axis today. It is deliberately scoped to whatever
/// <see cref="SprintScheduler.AdvanceGraphAsync"/> already does — `pending` → `ready` promotion,
/// plus that same call's existing human-gate and sprint-state synchronization side effects.
/// <para>
/// Holds no per-sprint memory of what it last saw: every tick re-derives entirely from durable
/// state, so a Host restart loses nothing. <see cref="ControlPlaneHostedService"/> owns this
/// service's lifetime directly — starting it only after winning the project lease and stopping it
/// before releasing that lease — so a Host that loses the lease race never runs a tick at all.
/// </para>
/// </remarks>
public sealed class ResumeSchedulerHostedService(
    ResumeSchedulerOptions options,
    ISprintStore store,
    SprintScheduler scheduler,
    ILogger<ResumeSchedulerHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception> LogListFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2010, "ResumeSchedulerListFailed"),
        "Re-deriving sprint readiness failed while listing this project's sprints; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogSprintFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2011, "ResumeSchedulerSprintFailed"),
        "Re-deriving sprint readiness failed for sprint {SprintId}; continuing with the rest.");

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
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogListFailed(logger, exception);
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException)
            {
                // One sprint's unreadable journal, or a race with its own concurrent deletion
                // (RequireStateAsync/RequireDefinitionAsync throw InvalidOperationException when a
                // sprint ListAsync just returned no longer loads), must never stop every other
                // sprint in the project from being re-derived — matches ControlPlaneHostedService's
                // own per-connection failure isolation.
                LogSprintFailed(logger, sprintId.Value, exception);
            }
        }
    }
}
