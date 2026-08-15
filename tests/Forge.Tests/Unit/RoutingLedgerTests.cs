using System.Text.Json.Nodes;
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
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());
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
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());
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
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
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

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(FailureClass.Auth)]
    [InlineData(FailureClass.Policy)]
    public async Task AnExcludedFailureNeverTripsTheBreakerOrConsumesTheSharedBudget(FailureClass failureClass)
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // More than the breaker's threshold: even a run of excluded failures long enough to trip it
        // must leave no breaker record at all.
        for (int i = 0; i < RoutingLedger.DefaultFailureThreshold + 2; i++)
        {
            RouteDecision decision = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, decision, false, failureClass, cancellationToken);
        }

        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Null(breaker);
        IReadOnlyList<RouteDecision> decisions =
            await ledger.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);
        Assert.All(decisions.Where(d => d.Outcome != RouteOutcome.Routed), d => Assert.Equal(RouteOutcome.Excluded, d.Outcome));
        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(RoutingLedger.DefaultRetryBudget, budget.Remaining);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExcludedFailureForADecisionThatWasNotRoutedDoesNotCreditAUnitItNeverSpent()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());
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
    public async Task ReportingAnOutcomeForADecisionThatWasNotRoutedNeverTouchesTheBreakerEitherDirection()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
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
        CircuitBreakerRecord? beforeReport =
            await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);

        clock.UtcNow += TimeSpan.FromSeconds(1);
        // A caller reporting an outcome for a decision the breaker itself already refused (nothing
        // was ever attempted) must not move the breaker at all in either direction: crediting it as
        // a success would close a breaker nothing actually tested, and crediting it as a failure
        // would refresh the cooldown on every such misreport forever — a livelock, never recovering.
        await ledger.RecordOutcomeAsync(root.Path, sprintId, blocked, false, FailureClass.Transient, cancellationToken);
        await ledger.RecordOutcomeAsync(root.Path, sprintId, blocked, true, null, cancellationToken);

        CircuitBreakerRecord? afterReport =
            await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Equal(beforeReport, afterReport);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFailingHalfOpenTrialReopensTheBreakerWithAFreshCooldown()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
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
        FileSprintEventLog store = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RouteDecision first = new(Guid.NewGuid(), sprintId, "a", attemptId, Key, RouteOutcome.Routed, null, DateTimeOffset.UnixEpoch);
        await store.AppendRouteDecisionAsync(root.Path, first, cancellationToken);

        string path = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
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
        FileSprintEventLog store = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string path = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Newline-terminated (not torn) but not valid JSON — real corruption this store never
        // produces itself.
        await File.WriteAllTextAsync(path, "{not json}\n", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RoutingPathsDoNotMutateOrMarkAJournalWithACorruptTransition(bool append)
    {
        using TestRoot root = new();
        FileSprintEventLog store = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        string sprintDirectory = FileSprintEventLog.SprintDirectory(root.Path, sprintId);
        string eventsPath = Path.Combine(sprintDirectory, "events.jsonl");
        JsonNode corrupted = JsonNode.Parse(await File.ReadAllTextAsync(eventsPath, cancellationToken))!;
        Assert.True(corrupted["arguments"]!.AsObject().Remove("to_state"));
        string original = corrupted.ToJsonString() + "\n";
        await File.WriteAllTextAsync(eventsPath, original, cancellationToken);
        string routingDirectory = Path.Combine(sprintDirectory, "routing");
        Directory.CreateDirectory(routingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(routingDirectory, "retry-budget.json"),
            "{\"total\":10,\"consumed\":0}",
            cancellationToken);

        Task action = append
            ? store.AppendRouteDecisionAsync(
                root.Path,
                new(
                    Guid.NewGuid(), sprintId, "a", AttemptId.New(), Key, RouteOutcome.Routed, null,
                    DateTimeOffset.UnixEpoch),
                cancellationToken)
            : store.GetRouteDecisionsAsync(root.Path, sprintId, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => action);
        Assert.Equal(original, await File.ReadAllTextAsync(eventsPath, cancellationToken));
        Assert.False(File.Exists(Path.Combine(routingDirectory, "migrated-to-sprint-journal")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheRetryBudgetIsSharedAcrossDifferentNodesAndHealthKeysInTheSameSprint()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());
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
    public async Task ADeferredKeyStaysUnroutableUntilItsResumeTimeElapsesThenRoutesAgain()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RouteDecision routed = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        DateTimeOffset resumeAt = clock.UtcNow + TimeSpan.FromMinutes(1);

        await ledger.RecordDeferralAsync(root.Path, sprintId, routed, resumeAt, cancellationToken);
        RouteDecision stillWaiting =
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Deferred, stillWaiting.Outcome);
        Assert.Equal(resumeAt, stillWaiting.ResumeNotBefore);

        clock.UtcNow = resumeAt;
        RouteDecision resumed = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Routed, resumed.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADeferralConsumesTheSharedBudgetLikeAnyOtherFailureAndNeverTripsTheBreaker()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RouteDecision routed = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);

        await ledger.RecordDeferralAsync(
            root.Path, sprintId, routed, clock.UtcNow + TimeSpan.FromMinutes(1), cancellationToken);

        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(RoutingLedger.DefaultRetryBudget - 1, budget.Remaining);
        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(root.Path, sprintId, Key, cancellationToken);
        Assert.Null(breaker);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ABreakerTripOnTheSameKeyNeverClearsAnUnrelatedPendingDeferral()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        FileSprintEventLog store = new(clock);
        RoutingLedger ledger = new(store, clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset start = clock.UtcNow;
        for (int i = 0; i < RoutingLedger.DefaultFailureThreshold; i++)
        {
            RouteDecision decision =
                await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
            await ledger.RecordOutcomeAsync(
                root.Path, sprintId, decision, false, FailureClass.Transient, cancellationToken);
        }

        RouteDecision open = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.CircuitOpen, open.Outcome);
        DateTimeOffset resumeAt = start + TimeSpan.FromMinutes(5);
        // An unrelated deferral recorded directly against the durable store for the same key —
        // e.g. a rate limit reported by a different attempt while this one's breaker was already
        // tripped for an unrelated reason.
        await store.AppendRouteDecisionAsync(
            root.Path,
            new(
                Guid.NewGuid(), sprintId, "a", attemptId, Key, RouteOutcome.Deferred, FailureClass.Transient,
                clock.UtcNow, resumeAt),
            cancellationToken);

        // The breaker is still open (its 2-minute cooldown is shorter than the 5-minute deferral),
        // so it keeps producing fresh CircuitOpen decisions for the same key in between.
        clock.UtcNow = start + TimeSpan.FromMinutes(1);
        RouteDecision stillOpen =
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.CircuitOpen, stillOpen.Outcome);

        // Once the breaker's cooldown elapses, the deferral — recorded before the breaker's own
        // CircuitOpen decisions above — must still be honored rather than silently discarded.
        clock.UtcNow = start + RoutingLedger.DefaultCooldown + TimeSpan.FromSeconds(1);
        RouteDecision stillDeferred =
            await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Deferred, stillDeferred.Outcome);
        Assert.Equal(resumeAt, stillDeferred.ResumeNotBefore);

        clock.UtcNow = resumeAt;
        RouteDecision resumed = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Routed, resumed.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetResumeNotBeforeReturnsTheSoonestPendingDeferralAcrossKeysAndNullOnceAllElapse()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
        SprintId sprintId = SprintId.New();
        HealthKey otherKey = new("codex", "gpt", "sprint");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RouteDecision first = await ledger.DecideAsync(
            root.Path, sprintId, "a", AttemptId.New(), Key, cancellationToken);
        RouteDecision second = await ledger.DecideAsync(
            root.Path, sprintId, "b", AttemptId.New(), otherKey, cancellationToken);
        DateTimeOffset soonest = clock.UtcNow + TimeSpan.FromMinutes(1);
        DateTimeOffset later = clock.UtcNow + TimeSpan.FromMinutes(5);
        await ledger.RecordDeferralAsync(root.Path, sprintId, first, soonest, cancellationToken);
        await ledger.RecordDeferralAsync(root.Path, sprintId, second, later, cancellationToken);

        Assert.Equal(soonest, await ledger.GetResumeNotBeforeAsync(root.Path, sprintId, cancellationToken));

        clock.UtcNow = later;
        Assert.Null(await ledger.GetResumeNotBeforeAsync(root.Path, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFreshRoutingLedgerInstanceRecoversAPendingDeferralIdempotentlyAfterARestart()
    {
        // Simulates a Host restart: no in-memory timer exists to lose, so a brand-new ledger over
        // the same durable store must derive the exact same wait purely from history plus the clock.
        using TestRoot root = new();
        FakeClock clock = new();
        FileSprintEventLog store = new(clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RouteDecision routed = await new RoutingLedger(store, clock)
            .DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        DateTimeOffset resumeAt = clock.UtcNow + TimeSpan.FromMinutes(1);
        await new RoutingLedger(store, clock)
            .RecordDeferralAsync(root.Path, sprintId, routed, resumeAt, cancellationToken);

        RoutingLedger recovered = new(store, clock);
        RouteDecision stillWaiting =
            await recovered.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Deferred, stillWaiting.Outcome);

        clock.UtcNow = resumeAt;
        RoutingLedger recoveredAgain = new(store, clock);
        RouteDecision resumed =
            await recoveredAgain.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.Routed, resumed.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingADeferralForADecisionThatWasNotRoutedIsANoOp()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        RoutingLedger ledger = new(new FileSprintEventLog(clock), clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (int i = 0; i < RoutingLedger.DefaultRetryBudget; i++)
        {
            await ledger.DecideAsync(root.Path, sprintId, "a", AttemptId.New(), Key, cancellationToken);
        }

        RouteDecision exhausted = await ledger.DecideAsync(root.Path, sprintId, "a", attemptId, Key, cancellationToken);
        Assert.Equal(RouteOutcome.BudgetExhausted, exhausted.Outcome);

        await ledger.RecordDeferralAsync(
            root.Path, sprintId, exhausted, clock.UtcNow + TimeSpan.FromMinutes(1), cancellationToken);

        Assert.Null(await ledger.GetResumeNotBeforeAsync(root.Path, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RouteDecisionsAreDurablyRecordedInOrder()
    {
        using TestRoot root = new();
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LegacyRoutingSidecarsMigrateOnceIntoTheSprintJournal()
    {
        using TestRoot root = new();
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        Guid decisionId = Guid.NewGuid();
        string routingDirectory = Path.Combine(
            FileSprintEventLog.SprintDirectory(root.Path, sprintId), "routing");
        string breakersDirectory = Path.Combine(routingDirectory, "breakers");
        Directory.CreateDirectory(breakersDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(routingDirectory, "decisions.jsonl"),
            $$"""
            {"decision_id":"{{decisionId:D}}","node_id":"a","attempt_id":"{{attemptId.Value:D}}","provider":"claude_code","model":"sonnet","surface":"sprint","outcome":"routed","failure_class":null,"decided_at":"1970-01-01T00:00:00+00:00"}
            """ + "\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(breakersDirectory, "legacy.json"),
            """
            {"provider":"claude_code","model":"sonnet","surface":"sprint","state":"open","consecutive_failures":3,"opened_at":"1970-01-01T00:00:00+00:00","cooldown_until":"1970-01-01T00:02:00+00:00","updated_at":"1970-01-01T00:00:00+00:00"}
            """,
            TestContext.Current.CancellationToken);
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());

        IReadOnlyList<RouteDecision> first = await ledger.GetRouteDecisionsAsync(
            root.Path, sprintId, TestContext.Current.CancellationToken);
        IReadOnlyList<RouteDecision> second = await ledger.GetRouteDecisionsAsync(
            root.Path, sprintId, TestContext.Current.CancellationToken);
        CircuitBreakerRecord? breaker = await ledger.GetCircuitBreakerAsync(
            root.Path, sprintId, Key, TestContext.Current.CancellationToken);

        Assert.Equal(4, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(decisionId, first[0].DecisionId);
        Assert.Equal(CircuitState.Open, breaker!.State);
        Assert.True(File.Exists(Path.Combine(routingDirectory, "migrated-to-sprint-journal")));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyRetryBudgetReconcilesEitherOldCrashOrdering(bool budgetWriteWasAhead)
    {
        using TestRoot root = new();
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        string routingDirectory = Path.Combine(
            FileSprintEventLog.SprintDirectory(root.Path, sprintId), "routing");
        Directory.CreateDirectory(routingDirectory);
        if (!budgetWriteWasAhead)
        {
            await File.WriteAllTextAsync(
                Path.Combine(routingDirectory, "decisions.jsonl"),
                $$"""
                {"decision_id":"{{Guid.NewGuid():D}}","node_id":"a","attempt_id":"{{attemptId.Value:D}}","provider":"claude_code","model":"sonnet","surface":"sprint","outcome":"routed","failure_class":null,"decided_at":"1970-01-01T00:00:00+00:00"}
                """ + "\n",
                TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(
            Path.Combine(routingDirectory, "retry-budget.json"),
            $$"""{"total":10,"consumed":{{(budgetWriteWasAhead ? 1 : 0)}}}""",
            TestContext.Current.CancellationToken);
        RoutingLedger ledger = new(new FileSprintEventLog(new FakeClock()), new FakeClock());

        RetryBudgetRecord budget = await ledger.GetRetryBudgetAsync(
            root.Path, sprintId, TestContext.Current.CancellationToken);
        IReadOnlyList<RouteDecision> decisions = await ledger.GetRouteDecisionsAsync(
            root.Path, sprintId, TestContext.Current.CancellationToken);

        RouteOutcome[] expected = budgetWriteWasAhead
            ? [RouteOutcome.Routed]
            : [RouteOutcome.Routed, RouteOutcome.Excluded];
        Assert.Equal(budgetWriteWasAhead ? 1 : 0, budget.Consumed);
        Assert.Equal(expected, decisions.Select(item => item.Outcome));
    }
}
