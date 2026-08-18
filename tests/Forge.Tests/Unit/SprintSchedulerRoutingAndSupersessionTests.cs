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
