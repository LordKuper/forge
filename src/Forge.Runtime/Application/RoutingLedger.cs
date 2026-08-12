using System.Collections.Concurrent;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// Fallback routing policy: a circuit breaker per <see cref="HealthKey"/> (provider/model/surface),
/// one retry budget shared by every node and attempt in the sprint, and a durable, reproducible log
/// of every decision — so a fallback sequence can be explained after the fact from state alone. An
/// authentication or policy failure (<see cref="FailureClass.Auth"/>/<see cref="FailureClass.Policy"/>)
/// never trips a breaker and never consumes anything further: those are excluded outright, never
/// disguised as a transient, retryable failure (see the architecture overview's fallback section).
/// </summary>
/// <remarks>
/// ponytail: breakers and the retry budget are scoped per sprint, not shared project- or
/// user-wide, and the failure threshold/cooldown/budget are fixed constants rather than
/// configuration — matching <c>SprintScheduler.MaxAutomaticRetries</c>'s own fixed policy. A
/// provider outage affecting every concurrent sprint identically is a real gap this leaves open;
/// promote these to project scope if flapping across concurrent sprints on the same provider ever
/// becomes a real problem. <see cref="DecideAsync"/>/<see cref="RecordOutcomeAsync"/> serialize per
/// sprint (a single in-process lock, matching <c>SprintGitIsolation.Locks</c>), so a breaker's
/// half-open trial is exactly one call at a time within this process; a second Forge process
/// touching the same sprint concurrently is out of scope, same as every other file store here.
/// </remarks>
public sealed class RoutingLedger(IRoutingStore store, IClock clock)
{
    public const int DefaultRetryBudget = 10;
    public const int DefaultFailureThreshold = 3;
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(2);

    // Every budget/breaker update below is a read-modify-write against a shared per-sprint file;
    // without a lock, two `DecideAsync`/`RecordOutcomeAsync` calls for the same sprint racing (e.g.
    // two independent nodes routing concurrently) could both read the same starting value and one
    // write clobber the other's — a lost update, the same class of bug the event-sourced store's own
    // per-sprint lock (`FileSprintEventLog.Locks`) and `SprintGitIsolation.Locks` both exist to
    // prevent. Per-process only, matching every other lock in this codebase.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    /// <summary>Decides whether a call to <paramref name="key"/> may proceed right now, durably
    /// recording the decision either way before returning it.</summary>
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
            RetryBudgetRecord budget = await store.GetRetryBudgetAsync(projectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false) ?? new(sprintId, DefaultRetryBudget, 0);
            CircuitBreakerRecord? breaker = await store
                .GetCircuitBreakerAsync(projectRoot, sprintId, key, cancellationToken).ConfigureAwait(false);

            RouteOutcome outcome;
            if (budget.Remaining <= 0)
            {
                outcome = RouteOutcome.BudgetExhausted;
            }
            else if (breaker is { State: CircuitState.Open } open && open.CooldownUntil is { } cooldown &&
                now < cooldown)
            {
                outcome = RouteOutcome.CircuitOpen;
            }
            else
            {
                outcome = RouteOutcome.Routed;
                await store.SaveRetryBudgetAsync(
                    projectRoot, sprintId, budget with { Consumed = budget.Consumed + 1 }, cancellationToken)
                    .ConfigureAwait(false);
                if (breaker is { State: CircuitState.Open } stale)
                {
                    // The cooldown elapsed: the next outcome (RecordOutcomeAsync) decides whether this
                    // half-open trial closes the breaker or reopens it with a fresh cooldown.
                    await store.SaveCircuitBreakerAsync(
                        projectRoot, sprintId, stale with { State = CircuitState.HalfOpen, UpdatedAt = now },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            RouteDecision decision = new(Guid.NewGuid(), sprintId, nodeId, attemptId, key, outcome, null, now);
            await store.AppendRouteDecisionAsync(projectRoot, decision, cancellationToken).ConfigureAwait(false);
            return decision;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records the outcome of a call this ledger routed — <paramref name="decision"/> must be the
    /// exact <see cref="RouteDecision"/> <see cref="DecideAsync"/> returned for that call, not just
    /// its node/attempt/key. A decision whose own <see cref="RouteDecision.Outcome"/> is not
    /// <see cref="RouteOutcome.Routed"/> was never actually attempted — routing itself refused it —
    /// so this is a full no-op for one: no budget was consumed to refund, and applying an outcome to
    /// the breaker would be equally wrong in both directions (crediting a call that never happened
    /// closes a breaker nothing tested; reporting one as failed re-opens it with a fresh cooldown
    /// that never actually elapses, a livelock). A <see cref="FailureClass.Auth"/> or
    /// <see cref="FailureClass.Policy"/> failure is excluded outright — recorded as its own route
    /// decision, but never applied to the breaker, and its budget unit refunded — so it can never
    /// masquerade as a transient, retryable failure nor count against how many transient retries the
    /// rest of the sprint gets.
    /// </summary>
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
            DateTimeOffset now = clock.UtcNow;
            if (!succeeded && failureClass is FailureClass.Auth or FailureClass.Policy)
            {
                RetryBudgetRecord budget = await store
                    .GetRetryBudgetAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false) ??
                    new(sprintId, DefaultRetryBudget, 0);
                await store.SaveRetryBudgetAsync(
                    projectRoot, sprintId, budget with { Consumed = Math.Max(0, budget.Consumed - 1) },
                    cancellationToken).ConfigureAwait(false);

                await store.AppendRouteDecisionAsync(
                    projectRoot,
                    new(
                        Guid.NewGuid(), sprintId, decision.NodeId, decision.AttemptId, decision.Key,
                        RouteOutcome.Excluded, failureClass, now),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            CircuitBreakerRecord current = await store
                .GetCircuitBreakerAsync(projectRoot, sprintId, decision.Key, cancellationToken)
                .ConfigureAwait(false) ?? new(decision.Key, CircuitState.Closed, 0, null, null, now);
            CircuitBreakerRecord updated = succeeded
                ? current with
                {
                    State = CircuitState.Closed,
                    ConsecutiveFailures = 0,
                    OpenedAt = null,
                    CooldownUntil = null,
                    UpdatedAt = now,
                }
                : Trip(current, now);
            await store.SaveCircuitBreakerAsync(projectRoot, sprintId, updated, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<CircuitBreakerRecord?> GetCircuitBreakerAsync(
        string projectRoot,
        SprintId sprintId,
        HealthKey key,
        CancellationToken cancellationToken) =>
        store.GetCircuitBreakerAsync(projectRoot, sprintId, key, cancellationToken);

    public async Task<RetryBudgetRecord> GetRetryBudgetAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        await store.GetRetryBudgetAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false) ??
            new(sprintId, DefaultRetryBudget, 0);

    public Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        store.GetRouteDecisionsAsync(projectRoot, sprintId, cancellationToken);

    private static CircuitBreakerRecord Trip(CircuitBreakerRecord current, DateTimeOffset now)
    {
        int failures = current.ConsecutiveFailures + 1;
        return failures >= DefaultFailureThreshold
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
