using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Stage 11, P11.48-P11.55: durable rate-limit deferral (<see cref="SprintScheduler.DeferAttemptAsync"/>,
/// wired through <see cref="SprintScheduler.StartAttemptAsync"/>'s new <c>RoutingLedger</c>
/// consultation) and human-only attempt supersession
/// (<see cref="SprintScheduler.SupersedeAttemptAsync"/>).</summary>
public sealed class SprintSchedulerRoutingAndSupersessionTests
{
    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    private static readonly IReadOnlyList<NodeDefinition> ImplementationNodeGraph =
        [new("a", NodeKind.Work, [], NodeRole.Implementation)];

    private static readonly IReadOnlyList<NodeDefinition> GenericNodeGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeferAttemptAsyncRecordsARoutingDeferralThatBlocksTheNextStartAttempt()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ImplementationNodeGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded);

        CompleteAttemptResult deferred = await scheduler.DeferAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, SampleDigest, cancellationToken);
        Assert.True(deferred.Succeeded);
        // Within the automatic-retry budget: the node is re-armed exactly like any other failure.
        Assert.Equal(NodeState.Ready, deferred.Node!.State);

        StartAttemptResult retried = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", deferred.Node.Version, cancellationToken);

        Assert.False(retried.Succeeded);
        Assert.Equal(DiagnosticCodes.RoutingDeferred, retried.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeferAttemptAsyncFailsWhenNoRoutedDecisionExistsForTheAttempt()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // A Generic-role node has no execution profile to route by, so StartAttemptAsync never
        // consults RoutingLedger for it at all -- there is nothing for DeferAttemptAsync to find.
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GenericNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        CompleteAttemptResult result = await scheduler.DeferAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, SampleDigest, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, result.DiagnosticCode);
    }

    // ADR 0006: "Repeated deferral cannot spin or bypass the sprint retry budget."
    // DeferAttemptAsyncRecordsARoutingDeferralThatBlocksTheNextStartAttempt (above) already shows
    // one deferral consumes an automatic-retry unit exactly like an ordinary failure does (the
    // node auto-retries within budget, then the routing deferral additionally blocks the next
    // start). Budget *exhaustion* through repeated consumption is already covered for the shared
    // underlying mechanism by the ordinary-failure tests elsewhere in SprintSchedulerTests
    // (ExhaustRetriesAsync); re-deriving it here through repeated real deferrals would need to
    // advance real wall-clock time past each one's DefaultRateLimitBackoff between iterations,
    // which the clock this scheduler is wired to in these tests cannot do.

    /// <summary>Regression: an earlier version consulted <c>RoutingLedger</c> on every attempt start
    /// but never refunded a successful one, turning the shared 10-unit budget into an unrecoverable
    /// lifetime cap on ordinary progress (proven here with more model-bearing nodes than the budget
    /// has units, each of which starts and completes cleanly on its first attempt).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAttemptAsyncRefundsTheRoutingBudgetOnSuccessSoOrdinaryProgressNeverExhaustsIt()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int nodeCount = RoutingLedger.DefaultRetryBudget + 2;
        List<NodeDefinition> graph = [];
        for (int i = 0; i < nodeCount; i++)
        {
            graph.Add(new($"n{i}", NodeKind.Work, [], NodeRole.Implementation));
        }

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: graph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        for (int i = 0; i < nodeCount; i++)
        {
            string nodeId = $"n{i}";
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, nodeId, 2, cancellationToken);
            Assert.True(started.Succeeded, $"node {nodeId} failed to start: {started.DiagnosticCode}");
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, nodeId, started.AttemptId!, true, SampleDigest, [], [],
                cancellationToken);
            Assert.True(completed.Succeeded, $"node {nodeId} failed to complete: {completed.DiagnosticCode}");
        }
    }

    /// <summary>Regression: an earlier version consumed a routing-budget unit via <c>DecideAsync</c>
    /// before attempting the node transition that unit was meant to authorize, and never refunded it
    /// when that transition itself failed (a stale pre-check, or a genuine concurrent race) -- a run
    /// of such conflicts on the same node could permanently exhaust the shared budget for work that
    /// never actually happened.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartAttemptAsyncRefundsTheRoutingBudgetWhenTheNodeTransitionItselfFails()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ImplementationNodeGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        // The node transition StartAttemptAsync's own routing decision was meant to authorize --
        // forced to conflict right after the routing decision has already been recorded.
        store.FailAt[store.AppendCount + 1] = AppendOutcome.Conflict;

        StartAttemptResult result =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        Assert.False(result.Succeeded);
        RoutingLedger routingLedger = new(store, environment.Resolve<IClock>());
        RetryBudgetRecord budget =
            await routingLedger.GetRetryBudgetAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(RoutingLedger.DefaultRetryBudget, budget.Remaining);
    }

    /// <summary>Regression: an earlier version recorded the durable routing deferral before
    /// confirming the completion it rides on actually succeeded, so a completion that failed for an
    /// unrelated reason still left the provider/model key blocked.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeferAttemptAsyncDoesNotRecordADeferralWhenTheUnderlyingCompletionFails()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ImplementationNodeGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded);

        store.FailAt[store.AppendCount + 1] = AppendOutcome.Conflict;

        CompleteAttemptResult result = await scheduler.DeferAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, SampleDigest, cancellationToken);

        Assert.False(result.Succeeded);
        IReadOnlyList<RouteDecision> decisions =
            await store.GetRouteDecisionsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.DoesNotContain(decisions, decision => decision.Outcome == RouteOutcome.Deferred);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRequiresConfirmation()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore _, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);

        CompleteAttemptResult result = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), confirmed: false, "Try a different approach.",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfirmationRequired, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRejectsAStaleVersion()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore _, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);

        CompleteAttemptResult result = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version + 1,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), confirmed: true, "Try a different approach.",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRejectsAMismatchedIdempotencyKey()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore _, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);

        CompleteAttemptResult result = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, Guid.NewGuid(), confirmed: true,
            "Try a different approach.", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRejectsAnOverlongInstruction()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore _, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        string tooLong = new('x', SprintScheduler.MaxSupersessionInstructionLength + 1);

        CompleteAttemptResult result = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), confirmed: true, tooLong,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SupersessionInstructionTooLong, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRejectsATerminalAttempt()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", attempt.Id, true, SampleDigest, [], [], cancellationToken);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot settled = state.Attempts[attempt.Id.Value.ToString("D")];

        CompleteAttemptResult result = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, settled.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, settled), confirmed: true, "Try a different approach.",
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.AttemptTerminal, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncCancelsTheOldAttemptAndCreatesALinkedReplacement()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CompleteAttemptResult result = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), confirmed: true, "Try a different approach.",
            cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, result.Node!.State);

        SprintWorkflowState afterState = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot supersededAttempt = afterState.Attempts[attempt.Id.Value.ToString("D")];
        Assert.Equal(AttemptState.Cancelled, supersededAttempt.State);

        AttemptSnapshot freshAttempt =
            Assert.Single(afterState.Attempts.Values, candidate => candidate.Id != attempt.Id);
        Assert.Equal(AttemptState.Created, freshAttempt.State);
        Assert.Equal(attempt.Id, freshAttempt.SupersedesAttemptId);
        Assert.Equal("a", freshAttempt.NodeId);
    }

    /// <summary>A retried call with the same idempotency key after the cancel transition already
    /// landed must still finish whatever the interrupted call left undone (the fresh attempt and
    /// the node re-arm), not merely replay the already-settled cancellation and stop.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncIsIdempotentOnReplay()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid key = SprintScheduler.SupersedeAttemptKey(sprintId, attempt);

        CompleteAttemptResult first = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);
        Assert.True(first.Succeeded);

        CompleteAttemptResult second = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);

        Assert.True(second.Succeeded);
        SprintWorkflowState finalState = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // Still exactly one replacement attempt -- a replay never creates a second one.
        Assert.Single(finalState.Attempts.Values, candidate => candidate.Id != attempt.Id);
    }

    /// <summary>Regression: an earlier version only recognized a replay once the fresh replacement
    /// attempt already existed, so a retry landing between the cancel transition and replacement
    /// creation (the exact durable state a crash right there would leave) fell through to the
    /// stale-version pre-check instead — the attempt was stuck `cancelled` under a `running` node
    /// with no way to finish, short of aborting the sprint.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncResumesAfterACrashBetweenCancellationAndReplacementCreation()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid key = SprintScheduler.SupersedeAttemptKey(sprintId, attempt);

        // The exact append `SupersedeAttemptAsync` itself would make first -- simulating a crash
        // right after it durably landed, before anything else in that call ran.
        AppendOutcome cancelOutcome = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Attempt, attempt.Id.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_superseded", WorkflowStateNames.ToSnakeCase(AttemptState.Cancelled), attempt.Version,
            key, cancellationToken);
        Assert.True(cancelOutcome.Succeeded);

        CompleteAttemptResult resumed = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);

        Assert.True(resumed.Succeeded, $"diag={resumed.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, resumed.Node!.State);
        SprintWorkflowState finalState = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot fresh = Assert.Single(finalState.Attempts.Values, candidate => candidate.Id != attempt.Id);
        Assert.Equal(attempt.Id, fresh.SupersedesAttemptId);
    }

    /// <summary>Regression: a later version of the crash-window fix above skipped node re-arm
    /// entirely whenever a replacement attempt already existed, not only once it had actually
    /// started. That regressed this exact case: a crash landing after the replacement was created
    /// but before the node was re-armed left the node stuck `running` (from the cancelled attempt's
    /// own consequence) with a `created` replacement no verb could ever start.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncResumesAfterACrashBetweenReplacementCreationAndNodeReArm()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid key = SprintScheduler.SupersedeAttemptKey(sprintId, attempt);

        // The two durable steps SupersedeAttemptAsync itself would make first, simulating a crash
        // right after the replacement attempt was created but before the node was re-armed.
        AppendOutcome cancelOutcome = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Attempt, attempt.Id.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_superseded", WorkflowStateNames.ToSnakeCase(AttemptState.Cancelled), attempt.Version,
            key, cancellationToken);
        Assert.True(cancelOutcome.Succeeded);
        await store.AppendAttemptSupersededAsync(
            environment.ProjectRoot, sprintId, attempt.Id, "Try a different approach.", cancellationToken);
        AttemptId freshAttemptId = AttemptId.New();
        AppendOutcome creationOutcome = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Attempt, freshAttemptId.Value.ToString("D"),
            "AttemptChanged", "workflow.attempt_created", WorkflowStateNames.ToSnakeCase(WorkflowStateMachines.AttemptInitial),
            0, Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkflowEvent.NodeIdArgument] = "a",
                [WorkflowEvent.SupersedesAttemptIdArgument] = attempt.Id.Value.ToString("D"),
            });
        Assert.True(creationOutcome.Succeeded);

        CompleteAttemptResult resumed = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);

        Assert.True(resumed.Succeeded, $"diag={resumed.DiagnosticCode}");
        Assert.Equal(NodeState.Ready, resumed.Node!.State);
        SprintWorkflowState finalState = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot onlyReplacement =
            Assert.Single(finalState.Attempts.Values, candidate => candidate.Id != attempt.Id);
        Assert.Equal(freshAttemptId, onlyReplacement.Id);
    }

    /// <summary>Regression: an earlier version appended a new <c>AttemptSuperseded</c> event on every
    /// call, so a replay carrying different instruction text (a caller bug, but nothing prevented
    /// it) silently produced a second, contradictory record instead of being ignored like every
    /// other part of a replayed call.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReplayWithDifferentInstructionTextKeepsTheOriginallyRecordedInstruction()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid key = SprintScheduler.SupersedeAttemptKey(sprintId, attempt);

        CompleteAttemptResult first = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Original instruction.", cancellationToken);
        Assert.True(first.Succeeded);

        CompleteAttemptResult second = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Different instruction text.", cancellationToken);
        Assert.True(second.Succeeded);

        IReadOnlyList<WorkflowEvent> events =
            await store.GetEventsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        WorkflowEvent supersededEvent = Assert.Single(
            events,
            item => item.Type == WorkflowEvent.AttemptSupersededType &&
                item.Aggregate.Id == attempt.Id.Value.ToString("D"));
        Assert.Equal(
            "Original instruction.", supersededEvent.Arguments[WorkflowEvent.SupersessionInstructionArgument]);
    }

    /// <summary>Regression: an earlier version recomputed the replacement attempt's deterministic id
    /// from <c>node.AttemptCount</c> on every call instead of finding it by linkage. Once an ordinary
    /// <see cref="SprintScheduler.StartAttemptAsync"/> picks the replacement up, that count moves, so
    /// a late replay of the original supersede call recomputed a different, unrelated id -- creating
    /// a second, orphaned replacement and forcing the node's already in-flight run back through
    /// `failed` to `ready`, discarding it.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReplayAfterTheReplacementAlreadyStartedDoesNotCreateAnOrphanOrInterruptIt()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid key = SprintScheduler.SupersedeAttemptKey(sprintId, attempt);

        CompleteAttemptResult first = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);
        Assert.True(first.Succeeded);

        // The replacement is picked up by an ordinary StartAttemptAsync call, exactly as intended:
        // the node re-enters `running` under the replacement, not the superseded attempt.
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", first.Node!.Version, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");

        // A late replay of the original supersede call (e.g. a retried response) must not disturb
        // the replacement's own in-flight run.
        CompleteAttemptResult replay = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);

        Assert.True(replay.Succeeded, $"diag={replay.DiagnosticCode}");
        SprintWorkflowState finalState = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot onlyReplacement =
            Assert.Single(finalState.Attempts.Values, candidate => candidate.Id != attempt.Id);
        Assert.Equal(started.AttemptId, onlyReplacement.Id);
        Assert.Equal(NodeState.Running, finalState.Nodes["a"].State);
    }

    /// <summary>Regression: an earlier version of the "has the replacement started" check compared
    /// the node's *current* attempt number against the replacement's own for equality. That number
    /// only ever grows -- an ordinary auto-retry of the replacement itself (or a later second
    /// supersession) advances it further without moving it back -- so a replay of the original
    /// supersede call arriving after such further progress saw a *later* generation than the one the
    /// replacement started at and wrongly concluded "not picked up yet", re-arming the node out from
    /// under whatever later, genuinely in-flight generation was actually running.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReplayAfterTheReplacementFailedAndRetriedAgainDoesNotInterruptTheLatestRun()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintScheduler scheduler, ISprintStore store, SprintId sprintId, AttemptSnapshot attempt) =
            await StartImplementationAttemptAsync(environment);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid key = SprintScheduler.SupersedeAttemptKey(sprintId, attempt);

        CompleteAttemptResult superseded = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);
        Assert.True(superseded.Succeeded);

        // The replacement (generation 2) starts, then fails on its own and auto-retries -- ordinary
        // failure handling, nothing to do with supersession -- re-arming the node without creating a
        // new attempt.
        StartAttemptResult startedReplacement = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", superseded.Node!.Version, cancellationToken);
        Assert.True(startedReplacement.Succeeded, $"diag={startedReplacement.DiagnosticCode}");
        CompleteAttemptResult failedReplacement = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", startedReplacement.AttemptId!, false, SampleDigest, [], [],
            cancellationToken);
        Assert.True(failedReplacement.Succeeded);
        Assert.Equal(NodeState.Ready, failedReplacement.Node!.State);

        // A third generation starts -- a genuinely new, unrelated attempt actually running now.
        StartAttemptResult startedThirdGeneration = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", failedReplacement.Node.Version, cancellationToken);
        Assert.True(startedThirdGeneration.Succeeded, $"diag={startedThirdGeneration.DiagnosticCode}");

        // A late replay of the *original* supersede call must not disturb the third generation's own
        // in-flight run, even though it is one generation past the replacement this call itself
        // created.
        CompleteAttemptResult replay = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attempt.Id, attempt.Version, key, confirmed: true,
            "Try a different approach.", cancellationToken);

        Assert.True(replay.Succeeded, $"diag={replay.DiagnosticCode}");
        SprintWorkflowState finalState = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, finalState.Nodes["a"].State);
        Assert.Equal(AttemptState.Created, finalState.Attempts[startedThirdGeneration.AttemptId!.Value.ToString("D")].State);
    }

    private static async Task<(SprintScheduler Scheduler, ISprintStore Store, SprintId SprintId, AttemptSnapshot Attempt)>
        StartImplementationAttemptAsync(TestEnvironment environment)
    {
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ImplementationNodeGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot attempt = state.Attempts[started.AttemptId!.Value.ToString("D")];
        return (scheduler, store, sprintId, attempt);
    }

    private static async Task RunToRunningAsync(
        SprintOrchestrator orchestrator,
        string root,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(root, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(root, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(root, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
    }

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
