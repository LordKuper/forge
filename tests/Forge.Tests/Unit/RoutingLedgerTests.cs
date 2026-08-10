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
            RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, decision, false, FailureClass.Transient, cancellationToken);
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
            RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, decision, false, FailureClass.Transient, cancellationToken);
        }

        RouteDecision stillOpen = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.CircuitOpen, stillOpen.Outcome);

        clock.UtcNow += RoutingLedger.DefaultCooldown + TimeSpan.FromSeconds(1);
        RouteDecision trial = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Routed, trial.Outcome);
        await ledger.RecordOutcomeAsync(root.Path, sprintId, trial, true, null, cancellationToken);

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
            RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, decision, false, FailureClass.Auth, cancellationToken);
        }

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Null(breaker);
        IReadOnlyList<RouteDecision> decisions =
            await ledger.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);
        Assert.All(decisions.Where(d => d.Outcome != RouteOutcome.Routed), d => Assert.Equal(RouteOutcome.Excluded, d.Outcome));
        // An excluded failure must not count against the shared budget either — only the breaker
        // half of that claim was covered before.
        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(RoutingLedger.DefaultRetryBudget, budget.Remaining);
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

        RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        await ledger.RecordOutcomeAsync(
            root.Path, sprintId, decision, false, FailureClass.Policy, cancellationToken);

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Null(breaker);
        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(RoutingLedger.DefaultRetryBudget, budget.Remaining);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExcludedFailureForADecisionThatWasNotRoutedDoesNotCreditAUnitItNeverSpent()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileRoutingStore(), new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (int i = 0; i < RoutingLedger.DefaultRetryBudget; i++)
        {
            await ledger.DecideAsync(root.Path, sprintId, "a", AttemptId.New(), Key, cancellationToken);
        }

        RouteDecision exhausted = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.BudgetExhausted, exhausted.Outcome);

        // Reporting an auth failure against a decision that was never actually routed (the caller
        // should not do this, but nothing stops it) must not refund a unit that was never consumed.
        await ledger.RecordOutcomeAsync(root.Path, sprintId, exhausted, false, FailureClass.Auth, cancellationToken);

        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFailingHalfOpenTrialReopensTheBreakerWithAFreshCooldown()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileRoutingStore(), clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (int i = 0; i < RoutingLedger.DefaultFailureThreshold; i++)
        {
            RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, decision, false, FailureClass.Transient, cancellationToken);
        }

        clock.UtcNow += RoutingLedger.DefaultCooldown + TimeSpan.FromSeconds(1);
        RouteDecision trial = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Routed, trial.Outcome);
        DateTimeOffset trialTime = clock.UtcNow;
        await ledger.RecordOutcomeAsync(root.Path, sprintId, trial, false, FailureClass.Transient, cancellationToken);

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Equal(CircuitState.Open, breaker!.State);
        Assert.Equal(trialTime + RoutingLedger.DefaultCooldown, breaker.CooldownUntil);
        RouteDecision blockedAgain =
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.CircuitOpen, blockedAgain.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATornTrailingRouteDecisionLineIsDiscardedRatherThanCorruptingTheNextAppend()
    {
        using TestRoot root = new();
        FileRoutingStore store = new();
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RouteDecision first = new(Guid.NewGuid(), sprintId, "a", attemptId, Key, RouteOutcome.Routed, null, DateTimeOffset.UnixEpoch);
        await store.AppendRouteDecisionAsync(root.Path, first, cancellationToken);

        string path = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "routing", "decisions.jsonl");
        byte[] completeBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        // Simulates a crash mid-append: a second, real event's buffer was only partially flushed —
        // its bytes exist on disk but with no terminating newline.
        await File.AppendAllTextAsync(path, "{\"decision_id\":\"not-terminated", cancellationToken);

        IReadOnlyList<RouteDecision> beforeRepair =
            await store.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);
        Assert.Single(beforeRepair);
        Assert.Equal(first.DecisionId, beforeRepair[0].DecisionId);

        byte[] afterTruncate = await File.ReadAllBytesAsync(path, cancellationToken);
        Assert.Equal(completeBytes, afterTruncate);

        RouteDecision second = new(Guid.NewGuid(), sprintId, "a", attemptId, Key, RouteOutcome.Routed, null, DateTimeOffset.UnixEpoch);
        await store.AppendRouteDecisionAsync(root.Path, second, cancellationToken);
        IReadOnlyList<RouteDecision> afterRepair =
            await store.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);

        Assert.Equal(2, afterRepair.Count);
        Assert.Equal(first.DecisionId, afterRepair[0].DecisionId);
        Assert.Equal(second.DecisionId, afterRepair[1].DecisionId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AGenuinelyCorruptRouteDecisionLineSurfacesAsADiagnosableInvalidDataException()
    {
        using TestRoot root = new();
        FileRoutingStore store = new();
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string path = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "routing", "decisions.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Newline-terminated (not torn) but not valid JSON — real corruption this store never
        // produces itself.
        await File.WriteAllTextAsync(path, "{not json}\n", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken));
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

        RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        await ledger.RecordOutcomeAsync(
            root.Path, sprintId, decision, false, FailureClass.Auth, cancellationToken);

        IReadOnlyList<RouteDecision> decisions =
            await ledger.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);

        Assert.Equal(2, decisions.Count);
        Assert.Equal(RouteOutcome.Routed, decisions[0].Outcome);
        Assert.Equal(RouteOutcome.Excluded, decisions[1].Outcome);
        Assert.Equal(FailureClass.Auth, decisions[1].FailureClass);
    }
}
