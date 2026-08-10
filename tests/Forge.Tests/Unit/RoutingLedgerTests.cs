using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class RoutingLedgerTests
{
    private static readonly HealthKey Key = new("claude_code", "sonnet", "sprint");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheFirstDecisionForAFreshKeyRoutesAndConsumesOneUnitOfTheSharedBudget()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RouteDecision decision = await ledger.DecideAsync(
            root.Path, sprintId, "a", AttemptId.New(), Key, cancellationToken);

        Assert.Equal(RouteOutcome.Routed, decision.Outcome);
        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(RoutingLedger.DefaultRetryBudget - 1, budget.Remaining);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheBreakerOpensAfterTheConfiguredNumberOfConsecutiveTransientFailuresAndBlocksFurtherRouting()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        for (int i = 0; i < RoutingLedger.DefaultFailureThreshold; i++)
        {
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, "a", attemptId, Key, false, FailureClass.Transient, cancellationToken);
        }

        RouteDecision blocked = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);

        Assert.Equal(RouteOutcome.CircuitOpen, blocked.Outcome);
        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Equal(CircuitState.Open, breaker!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ABreakerStaysBlockedUntilItsCooldownElapsesThenAllowsOneTrialThatCloseItOnSuccess()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileRoutingStore(), clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (int i = 0; i < RoutingLedger.DefaultFailureThreshold; i++)
        {
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, "a", attemptId, Key, false, FailureClass.Transient, cancellationToken);
        }

        RouteDecision stillOpen = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.CircuitOpen, stillOpen.Outcome);

        clock.UtcNow += RoutingLedger.DefaultCooldown + TimeSpan.FromSeconds(1);
        RouteDecision trial = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Routed, trial.Outcome);
        await ledger.RecordOutcomeAsync(root.Path, sprintId, "a", attemptId, Key, true, null, cancellationToken);

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Equal(CircuitState.Closed, breaker!.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAuthFailureIsExcludedAndNeverTripsTheBreaker()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        for (int i = 0; i < RoutingLedger.DefaultFailureThreshold + 2; i++)
        {
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, "a", attemptId, Key, false, FailureClass.Auth, cancellationToken);
        }

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Null(breaker);
        IReadOnlyList<RouteDecision> decisions =
            await ledger.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);
        Assert.All(decisions.Where(d => d.Outcome != RouteOutcome.Routed), d => Assert.Equal(RouteOutcome.Excluded, d.Outcome));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APolicyFailureIsExcludedAndNeverTripsTheBreaker()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        await ledger.RecordOutcomeAsync(
            root.Path, sprintId, "a", attemptId, Key, false, FailureClass.Policy, cancellationToken);

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Null(breaker);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheRetryBudgetIsSharedAcrossDifferentNodesAndHealthKeysInTheSameSprint()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HealthKey otherKey = new("codex", "gpt", "sprint");

        for (int i = 0; i < RoutingLedger.DefaultRetryBudget; i++)
        {
            HealthKey key = i % 2 == 0 ? Key : otherKey;
            RouteDecision decision =
                await ledger.DecideAsync(root.Path, sprintId, $"node-{i}", AttemptId.New(), key, cancellationToken);
            Assert.Equal(RouteOutcome.Routed, decision.Outcome);
        }

        RouteDecision exhausted = await ledger.DecideAsync(
            root.Path, sprintId, "node-final", AttemptId.New(), Key, cancellationToken);

        Assert.Equal(RouteOutcome.BudgetExhausted, exhausted.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RouteDecisionsAreDurablyRecordedInOrder()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        await ledger.RecordOutcomeAsync(
            root.Path, sprintId, "a", attemptId, Key, false, FailureClass.Auth, cancellationToken);

        IReadOnlyList<RouteDecision> decisions =
            await ledger.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);

        Assert.Equal(2, decisions.Count);
        Assert.Equal(RouteOutcome.Routed, decisions[0].Outcome);
        Assert.Equal(RouteOutcome.Excluded, decisions[1].Outcome);
        Assert.Equal(FailureClass.Auth, decisions[1].FailureClass);
    }
}
