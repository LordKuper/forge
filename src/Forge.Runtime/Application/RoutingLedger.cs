using System.Collections.Concurrent;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// Sprint-scoped fallback policy reconstructed from routing records in the sprint journal. A
/// routed call consumes one shared retry unit; authentication and policy exclusions refund it;
/// transient failures drive a provider/model/surface circuit breaker.
/// </summary>
/// <remarks>
/// ponytail: fixed sprint-local limits avoid configuration and cross-sprint coordination. Promote
/// them only if evaluation data shows concurrent sprints amplify a provider outage.
/// </remarks>
public sealed class RoutingLedger(ISprintStore store, IClock clock)
{
    public const int DefaultRetryBudget = 10;
    public const int DefaultFailureThreshold = 3;
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(2);

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public async Task<RouteDecision> DecideAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        AttemptId attemptId,
        HealthKey key,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetOrAdd(sprintId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = clock.UtcNow;
            IReadOnlyList<RouteDecision> decisions =
                await store.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            RetryBudgetRecord budget = BuildBudget(sprintId, decisions);
            CircuitBreakerRecord? breaker = BuildBreaker(key, decisions);
            DateTimeOffset? deferredUntil = BuildDeferredUntil(key, decisions);
            RouteOutcome outcome = budget.Remaining <= 0
                ? RouteOutcome.BudgetExhausted
                : breaker is { State: CircuitState.Open, CooldownUntil: { } cooldown } && now < cooldown
                    ? RouteOutcome.CircuitOpen
                    : deferredUntil is { } until && now < until
                        ? RouteOutcome.Deferred
                        : RouteOutcome.Routed;
            RouteDecision decision = new(
                Guid.NewGuid(), sprintId, nodeId, attemptId, key, outcome, null, now,
                outcome == RouteOutcome.Deferred ? deferredUntil : null);
            await store.AppendRouteDecisionAsync(projectRoot, decision, cancellationToken).ConfigureAwait(false);
            return decision;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RecordOutcomeAsync(
        string projectRoot,
        SprintId sprintId,
        RouteDecision decision,
        bool succeeded,
        FailureClass? failureClass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Outcome != RouteOutcome.Routed)
        {
            return;
        }

        SemaphoreSlim gate = Locks.GetOrAdd(sprintId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RouteOutcome outcome = !succeeded && failureClass is FailureClass.Auth or FailureClass.Policy
                ? RouteOutcome.Excluded
                : succeeded ? RouteOutcome.Succeeded : RouteOutcome.Failed;
            await store.AppendRouteDecisionAsync(
                projectRoot,
                new(
                    Guid.NewGuid(), sprintId, decision.NodeId, decision.AttemptId, decision.Key,
                    outcome, failureClass, clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Finalizes a routed decision as a retryable rate-limit deferral (ADR 0006): the attempt is
    /// abandoned, the key stays unroutable through <paramref name="resumeNotBefore"/>, and the
    /// consumed budget unit is not refunded — repeated deferral still exhausts the shared budget
    /// like any other failure, so it cannot spin forever for free. Unlike a breaker trip, this never
    /// changes <see cref="CircuitBreakerRecord"/> state: a rate limit says nothing about whether the
    /// provider itself is healthy, so the same key keeps being preferred once it resumes.
    /// </summary>
    public async Task RecordDeferralAsync(
        string projectRoot,
        SprintId sprintId,
        RouteDecision decision,
        DateTimeOffset resumeNotBefore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Outcome != RouteOutcome.Routed)
        {
            return;
        }

        SemaphoreSlim gate = Locks.GetOrAdd(sprintId.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.AppendRouteDecisionAsync(
                projectRoot,
                new(
                    Guid.NewGuid(), sprintId, decision.NodeId, decision.AttemptId, decision.Key,
                    RouteOutcome.Deferred, FailureClass.Transient, clock.UtcNow, resumeNotBefore),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The soonest <see cref="RouteDecision.ResumeNotBefore"/> still ahead of now across
    /// every key this sprint has deferred, or <see langword="null"/> if nothing is currently
    /// deferred — the value <see cref="StatusAdvisor"/> attaches to <c>RoutingStatus</c>. Re-derived
    /// fresh from durable decisions on every call, so a Host restart recovers it for free: there is
    /// no live timer to lose or double-fire.</summary>
    public async Task<DateTimeOffset?> GetResumeNotBeforeAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        IReadOnlyList<RouteDecision> decisions =
            await store.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        List<DateTimeOffset> pending =
        [
            .. decisions
                .Select(item => item.Key)
                .Distinct()
                .Select(key => BuildDeferredUntil(key, decisions))
                .Where(until => until is { } value && now < value)
                .Select(until => until!.Value),
        ];
        return pending.Count == 0 ? null : pending.Min();
    }

    public async Task<CircuitBreakerRecord?> GetCircuitBreakerAsync(
        string projectRoot,
        SprintId sprintId,
        HealthKey key,
        CancellationToken cancellationToken) =>
        BuildBreaker(
            key,
            await store.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false));

    public async Task<RetryBudgetRecord> GetRetryBudgetAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        BuildBudget(
            sprintId,
            await store.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false));

    public Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        store.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken);

    private static RetryBudgetRecord BuildBudget(SprintId sprintId, IEnumerable<RouteDecision> decisions)
    {
        int consumed = 0;
        foreach (RouteDecision decision in decisions)
        {
            if (decision.Outcome == RouteOutcome.Routed)
            {
                consumed++;
            }
            else if (decision.Outcome == RouteOutcome.Excluded)
            {
                consumed = Math.Max(0, consumed - 1);
            }
        }

        return new(sprintId, DefaultRetryBudget, consumed);
    }

    private static CircuitBreakerRecord? BuildBreaker(
        HealthKey key,
        IEnumerable<RouteDecision> decisions)
    {
        CircuitBreakerRecord? current = null;
        foreach (RouteDecision decision in decisions.Where(item => item.Key == key))
        {
            if (decision.Outcome == RouteOutcome.Routed &&
                current is { State: CircuitState.Open, CooldownUntil: { } cooldown } &&
                decision.DecidedAt >= cooldown)
            {
                current = current with { State = CircuitState.HalfOpen, UpdatedAt = decision.DecidedAt };
            }
            else if (decision.Outcome == RouteOutcome.Succeeded)
            {
                current = new(key, CircuitState.Closed, 0, null, null, decision.DecidedAt);
            }
            else if (decision.Outcome == RouteOutcome.Failed)
            {
                current = Trip(
                    current ?? new(key, CircuitState.Closed, 0, null, null, decision.DecidedAt),
                    decision.DecidedAt);
            }
        }

        return current;
    }

    /// <summary>The latest decision for <paramref name="key"/> determines whether it is currently
    /// deferred: a <see cref="RouteOutcome.Deferred"/> decision sets the wait, and any later decision
    /// of any other outcome (a fresh <see cref="RouteOutcome.Routed"/> once the wait elapsed, or a
    /// terminal outcome recorded against it) clears it. The caller compares the result against "now"
    /// — a past timestamp here is simply an elapsed wait, not a cleared one.</summary>
    private static DateTimeOffset? BuildDeferredUntil(HealthKey key, IEnumerable<RouteDecision> decisions)
    {
        DateTimeOffset? deferredUntil = null;
        foreach (RouteDecision decision in decisions.Where(item => item.Key == key))
        {
            deferredUntil = decision.Outcome == RouteOutcome.Deferred ? decision.ResumeNotBefore : null;
        }

        return deferredUntil;
    }

    private static CircuitBreakerRecord Trip(CircuitBreakerRecord current, DateTimeOffset now)
    {
        int failures = current.ConsecutiveFailures + 1;
        return failures >= DefaultFailureThreshold || current.State == CircuitState.HalfOpen
            ? current with
            {
                State = CircuitState.Open,
                ConsecutiveFailures = failures,
                OpenedAt = now,
                CooldownUntil = now + DefaultCooldown,
                UpdatedAt = now,
            }
            : current with { State = CircuitState.Closed, ConsecutiveFailures = failures, UpdatedAt = now };
    }
}
