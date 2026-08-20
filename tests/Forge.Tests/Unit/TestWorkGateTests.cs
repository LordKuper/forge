using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Stage 11: <see cref="SprintScheduler.RecordTestWorkAsync"/>, the `test_work` counterpart to
/// <see cref="SprintScheduler.ConfirmNodeAsync"/> (PR #75/ADR 0034). Built with that slice's
/// decision-flip protection and stale-artifact handling in place from the start, rather than
/// discovered across review rounds -- this file's own test set mirrors
/// <c>ConfirmationGateTests</c>' final, fully-fixed coverage directly.
/// </summary>
public sealed class TestWorkGateTests
{
    private static readonly IReadOnlyList<NodeDefinition> TestWorkGraph =
        [new("test_work", NodeKind.Work, [], NodeRole.TestWork)];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncWithTestsAddedOutcomeSucceedsTheNode()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        Guid key = SprintScheduler.RecordTestWorkKey(sprintId, node);

        RecordTestWorkResult recorded = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot,
            sprintId,
            "test_work",
            TestWorkOutcome.TestsAdded,
            "Added a regression test for the reported off-by-one.",
            node.Version,
            key,
            cancellationToken);

        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);
        Assert.Equal(TestWorkOutcome.TestsAdded, recorded.TestWork!.Outcome);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncWithNoNewTestsJustifiedOutcomeAlsoSucceedsTheNode()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        Guid key = SprintScheduler.RecordTestWorkKey(sprintId, node);

        RecordTestWorkResult recorded = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot,
            sprintId,
            "test_work",
            TestWorkOutcome.NoNewTestsJustified,
            "Pure documentation change; existing checks cover every material risk.",
            node.Version,
            key,
            cancellationToken);

        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);
        Assert.Equal(TestWorkOutcome.NoNewTestsJustified, recorded.TestWork!.Outcome);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // Unlike confirmation's NotConfirmed outcome, neither test-work outcome blocks the sprint --
        // no downstream eligibility gate reads this artifact's content. The single-node fixture graph
        // reaches ReadyToFinalize once its only node succeeds, proving the sprint was never blocked.
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
        Assert.Equal(SprintState.ReadyToFinalize, state.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncRejectsAStaleVersion()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        Guid key = SprintScheduler.RecordTestWorkKey(sprintId, node);

        RecordTestWorkResult result = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.TestsAdded, "n/a", node.Version + 1, key,
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncAgainstANonTestWorkNodeIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("other", NodeKind.Work, []), .. TestWorkGraph]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordTestWorkResult result = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "other", TestWorkOutcome.TestsAdded, "n/a", 1, Guid.NewGuid(),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeKindMismatch, result.DiagnosticCode);
    }

    // A stateless caller (the CLI) retrying after its own response was lost presents the same,
    // now-stale version/key the original fresh call required -- must resolve to what already
    // happened, not a spurious SuggestionStale conflict.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncResumedAfterAlreadyTerminalReturnsTheRecordedArtifactInsteadOfReacting()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        Guid key = SprintScheduler.RecordTestWorkKey(sprintId, node);
        RecordTestWorkResult recorded = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.TestsAdded, "Added a test.",
            node.Version, key, cancellationToken);
        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);

        RecordTestWorkResult replay = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.TestsAdded, "irrelevant on replay",
            node.Version, key, cancellationToken);

        Assert.True(replay.Succeeded, replay.DiagnosticCode);
        Assert.Equal(recorded.TestWork!.TestWorkId, replay.TestWork!.TestWorkId);
    }

    // A resumed call presenting a DIFFERENT outcome than what already durably landed must never
    // silently reinterpret the earlier decision.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncRefusesADecisionFlipAfterAlreadyTerminal()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        Guid key = SprintScheduler.RecordTestWorkKey(sprintId, node);
        RecordTestWorkResult recorded = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.TestsAdded, "Added a test.",
            node.Version, key, cancellationToken);
        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);

        RecordTestWorkResult flipped = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.NoNewTestsJustified, "Actually not.",
            node.Version, key, cancellationToken);

        Assert.False(flipped.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeTransitionInvalid, flipped.DiagnosticCode);
        Assert.Equal(TestWorkOutcome.TestsAdded, flipped.TestWork!.Outcome);
        Assert.Single(await scheduler.GetTestWorkAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // A crash landing after StartAttemptAsync but before the record call leaves the node `running`
    // with nothing recorded yet -- a resumed retry with the same outcome must still complete cleanly.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncResumesARunningAttemptWithNoArtifactRecordedYet()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", node.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);

        RecordTestWorkResult resumed = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.TestsAdded, "Added a test.",
            node.Version, SprintScheduler.RecordTestWorkKey(sprintId, node), cancellationToken);

        Assert.True(resumed.Succeeded, resumed.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
        Assert.Single(await scheduler.GetTestWorkAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // A crash landing after the record call durably lands but before CompleteAttemptAsync leaves the
    // node `running` with an artifact already on record (simulated here by writing directly through
    // the store, bypassing the scheduler -- there is no lower-level "record only" scheduler primitive
    // for test-work, unlike confirmation's RecordConfirmationAsync). A same-outcome resume must
    // complete without minting a duplicate; a different-outcome resume must be refused, both while
    // still `running`.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncResumesARunningAttemptWithAMatchingArtifactAlreadyRecorded()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", node.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        TestWorkArtifact preCrash = new(
            Guid.NewGuid(), sprintId, new("test_work"), TestWorkOutcome.TestsAdded, "Added a test.",
            DateTimeOffset.UnixEpoch);
        await store.SaveTestWorkAsync(environment.ProjectRoot, preCrash, cancellationToken);
        Guid key = SprintScheduler.RecordTestWorkKey(sprintId, node);

        RecordTestWorkResult resumed = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.TestsAdded, "irrelevant on resume",
            node.Version, key, cancellationToken);

        Assert.True(resumed.Succeeded, resumed.DiagnosticCode);
        Assert.Equal(preCrash.TestWorkId, resumed.TestWork!.TestWorkId);
        Assert.Single(await scheduler.GetTestWorkAsync(environment.ProjectRoot, sprintId, cancellationToken));
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
    }

    // The same crash point as above (an artifact already durably recorded, node still `running`),
    // but the resumed call now presents a DIFFERENT outcome -- must be refused rather than silently
    // overriding the already-durable verdict while the node has not even reached a terminal state.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncRefusesADecisionFlipWhileStillRunning()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", node.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        TestWorkArtifact preCrash = new(
            Guid.NewGuid(), sprintId, new("test_work"), TestWorkOutcome.TestsAdded, "Added a test.",
            DateTimeOffset.UnixEpoch);
        await store.SaveTestWorkAsync(environment.ProjectRoot, preCrash, cancellationToken);

        RecordTestWorkResult flipped = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot, sprintId, "test_work", TestWorkOutcome.NoNewTestsJustified, "Actually not.",
            node.Version, SprintScheduler.RecordTestWorkKey(sprintId, node), cancellationToken);

        Assert.False(flipped.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeTransitionInvalid, flipped.DiagnosticCode);
        Assert.Single(await scheduler.GetTestWorkAsync(environment.ProjectRoot, sprintId, cancellationToken));
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, state.Nodes["test_work"].State);
    }

    // A fresh attempt (this node re-armed to `ready` by a supersession of an earlier, unrelated
    // attempt) must never reuse a stale artifact left over from that earlier attempt.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordTestWorkAsyncNeverReusesAStaleArtifactFromASupersededAttemptOnAFreshCall()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot originalNode = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", originalNode.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        TestWorkArtifact stale = new(
            Guid.NewGuid(), sprintId, new("test_work"), TestWorkOutcome.TestsAdded, "Stale.",
            DateTimeOffset.UnixEpoch);
        await store.SaveTestWorkAsync(environment.ProjectRoot, stale, cancellationToken);
        AttemptSnapshot attempt = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Attempts[started.AttemptId!.Value.ToString("D")];
        CompleteAttemptResult superseded = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, attempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), true, "Re-run test-work.", cancellationToken);
        Assert.True(superseded.Succeeded, superseded.DiagnosticCode);
        NodeSnapshot readyAgain = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["test_work"];
        Assert.Equal(NodeState.Ready, readyAgain.State);

        RecordTestWorkResult fresh = await scheduler.RecordTestWorkAsync(
            environment.ProjectRoot,
            sprintId,
            "test_work",
            TestWorkOutcome.NoNewTestsJustified,
            "Actually nothing new was needed.",
            readyAgain.Version,
            SprintScheduler.RecordTestWorkKey(sprintId, readyAgain),
            cancellationToken);

        Assert.True(fresh.Succeeded, fresh.DiagnosticCode);
        Assert.Equal(TestWorkOutcome.NoNewTestsJustified, fresh.TestWork!.Outcome);
        Assert.NotEqual(stale.TestWorkId, fresh.TestWork.TestWorkId);
        Assert.Equal(
            2, (await scheduler.GetTestWorkAsync(environment.ProjectRoot, sprintId, cancellationToken)).Count);
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
