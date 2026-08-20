using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Stage 11: a test-work node must never become eligible on dependency completion alone — only a
/// recorded, `Confirmed` <see cref="ConfirmationArtifact"/> from its confirmation-role dependency
/// makes it so. See <c>SprintScheduler.IsTestWorkEligibleAsync</c>.
/// </summary>
public sealed class ConfirmationGateTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TestWorkStaysPendingAfterConfirmationNodeSucceedsWithNoRecordedArtifact()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        Assert.Equal(NodeState.Pending, state.Nodes["test_work"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartingTestWorkBeforeConfirmationIsRejectedAsWorkflowBlocked()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", 1, cancellationToken);

        Assert.False(started.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowBlocked, started.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmedArtifactPromotesTestWorkToReady()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        RecordConfirmationResult recorded = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.Confirmed,
            "Feature X matches its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the full test suite locally; all green.")],
            cancellationToken);

        Assert.True(recorded.Succeeded);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["test_work"].State);

        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", state.Nodes["test_work"].Version, cancellationToken);
        Assert.True(started.Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NotConfirmedOutcomeBlocksARunningSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);

        await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.NotConfirmed,
            "Feature X does not yet match its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Inspection, "Acceptance criterion 2 is not met.")],
            cancellationToken);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, state.Sprint.State);
        Assert.Equal("confirmation", state.Sprint.BlockedReason);
        Assert.Equal(NodeState.Pending, state.Nodes["test_work"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ALaterNotConfirmedVerdictRevokesEligibilityGrantedByAnEarlierConfirmedOne()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await SucceedAsync(scheduler, environment.ProjectRoot, sprintId, "confirm", cancellationToken);
        await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], cancellationToken);

        // The confirmation node is re-attempted (e.g. after a human gate rejection upstream) and
        // this time is not confirmed -- the earlier `Confirmed` artifact must not keep eligibility
        // latched open.
        await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.NotConfirmed, "No longer meets the DoD.",
            [new(ConfirmationEvidenceKind.Inspection, "Regression found on re-review.")], cancellationToken);

        // `RecordConfirmationAsync` already moved the sprint to `Blocked` -- but the real exploit
        // this closes is an operator resuming past that block (e.g. because a *different* stuck
        // node was the reason they believed they fixed) without the test-work node itself having
        // been re-validated: `ResumeSprintAsync` does not distinguish *why* a sprint was blocked.
        SprintSnapshot blocked = (await orchestrator.GetSprintAsync(
            environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult resumed = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, blocked.Version, SprintOrchestrator.ResumeSprintKey(blocked)),
            cancellationToken);
        Assert.True(resumed.Succeeded);
        Assert.Equal(SprintState.Ready, resumed.Sprint!.State);
        SprintTransitionResult running = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, resumed.Sprint.Version, SprintOrchestrator.RunSprintKey(resumed.Sprint)),
            cancellationToken);
        Assert.True(running.Succeeded);
        Assert.Equal(SprintState.Running, running.Sprint!.State);

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", state.Nodes["test_work"].Version, cancellationToken);
        Assert.False(started.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowBlocked, started.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATieBetweenConfirmedAndNotConfirmedAtTheSameRecordedAtFailsClosed()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        // Written directly through the store (bypassing `RecordConfirmationAsync`'s own clock) so
        // both artifacts share the exact same `RecordedAt` regardless of the real clock's
        // resolution -- proving the tie-break itself, not just that ties are rare in practice.
        DateTimeOffset tie = DateTimeOffset.UnixEpoch;
        await store.SaveConfirmationAsync(
            environment.ProjectRoot,
            new(
                Guid.NewGuid(), sprintId, new("confirm"), ConfirmationOutcome.Confirmed, "Met the DoD.",
                [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], tie),
            cancellationToken);
        await store.SaveConfirmationAsync(
            environment.ProjectRoot,
            new(
                Guid.NewGuid(), sprintId, new("confirm"), ConfirmationOutcome.NotConfirmed, "Actually does not.",
                [new(ConfirmationEvidenceKind.Inspection, "Found a regression on closer review.")], tie),
            cancellationToken);

        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "test_work", 1, cancellationToken);

        Assert.False(started.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowBlocked, started.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAConfirmationAgainstAnUnknownNodeIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordConfirmationResult result = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "does_not_exist", ConfirmationOutcome.Confirmed, "n/a",
            [new(ConfirmationEvidenceKind.Inspection, "n/a")], cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeNotFound, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAConfirmationAgainstANonConfirmationNodeIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordConfirmationResult result = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "test_work", ConfirmationOutcome.Confirmed, "n/a",
            [new(ConfirmationEvidenceKind.Inspection, "n/a")], cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeKindMismatch, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncWithConfirmedOutcomeSucceedsTheNodeAndPromotesTestWorkToReady()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        Guid key = SprintScheduler.ConfirmNodeKey(sprintId, node);

        RecordConfirmationResult recorded = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.Confirmed,
            "Feature X matches its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the full test suite locally; all green.")],
            node.Version,
            key,
            cancellationToken);

        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);
        Assert.Equal(ConfirmationOutcome.Confirmed, recorded.Confirmation!.Outcome);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        Assert.Equal(NodeState.Ready, state.Nodes["test_work"].State);
    }

    // The node's own job -- rendering an honest judgment -- succeeded even though the judgment
    // itself was negative: a NotConfirmed verdict still completes the confirmation node's attempt,
    // it does not fail it. Blocking the sprint (RecordConfirmationAsync's own side effect) is the
    // actual stopping point for a human, not the node's own attempt outcome.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncWithNotConfirmedOutcomeStillSucceedsTheNodeButBlocksTheSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        Guid key = SprintScheduler.ConfirmNodeKey(sprintId, node);

        RecordConfirmationResult recorded = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.NotConfirmed,
            "Feature X does not yet match its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Inspection, "Acceptance criterion 2 is not met.")],
            node.Version,
            key,
            cancellationToken);

        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        Assert.Equal(SprintState.Blocked, state.Sprint.State);
        Assert.Equal("confirmation", state.Sprint.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncRejectsAStaleVersion()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        Guid key = SprintScheduler.ConfirmNodeKey(sprintId, node);

        RecordConfirmationResult result = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "n/a",
            [new(ConfirmationEvidenceKind.Inspection, "n/a")], node.Version + 1, key, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncAgainstANonConfirmationNodeIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordConfirmationResult result = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "test_work", ConfirmationOutcome.Confirmed, "n/a",
            [new(ConfirmationEvidenceKind.Inspection, "n/a")], 1, Guid.NewGuid(), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeKindMismatch, result.DiagnosticCode);
    }

    // A stateless caller (the CLI) retrying after its own response was lost presents the same,
    // now-stale version/key the original fresh call required -- must resolve to what already
    // happened, not a spurious SuggestionStale conflict.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncResumedAfterAlreadyTerminalReturnsTheRecordedArtifactInsteadOfReacting()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        Guid key = SprintScheduler.ConfirmNodeKey(sprintId, node);
        RecordConfirmationResult recorded = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], node.Version, key, cancellationToken);
        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);

        RecordConfirmationResult replay = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "irrelevant on replay",
            [new(ConfirmationEvidenceKind.Inspection, "irrelevant on replay")], node.Version, key, cancellationToken);

        Assert.True(replay.Succeeded, replay.DiagnosticCode);
        Assert.Equal(recorded.Confirmation!.ConfirmationId, replay.Confirmation!.ConfirmationId);
    }

    // A resumed call presenting a DIFFERENT outcome than what already durably landed must never
    // silently reinterpret the earlier verdict -- the same decision-flip protection
    // ResolveHumanGateAsync's own review history (ADR 0019) established for gate decisions,
    // required here too (ADR 0034's review).
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncRefusesADecisionFlipAfterAlreadyTerminal()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        Guid key = SprintScheduler.ConfirmNodeKey(sprintId, node);
        RecordConfirmationResult recorded = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], node.Version, key, cancellationToken);
        Assert.True(recorded.Succeeded, recorded.DiagnosticCode);

        RecordConfirmationResult flipped = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.NotConfirmed, "Actually does not.",
            [new(ConfirmationEvidenceKind.Inspection, "Found a regression on closer review.")], node.Version, key,
            cancellationToken);

        Assert.False(flipped.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeTransitionInvalid, flipped.DiagnosticCode);
        Assert.Equal(ConfirmationOutcome.Confirmed, flipped.Confirmation!.Outcome);
        Assert.Equal(
            recorded.Confirmation!.ConfirmationId,
            Assert.Single(await scheduler.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken))
                .ConfirmationId);
    }

    // A crash landing after StartAttemptAsync but before ConfirmNodeAsync's own RecordConfirmationAsync
    // call leaves the node `running` with nothing recorded yet -- a resumed retry with the same
    // outcome must still complete cleanly.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncResumesARunningAttemptWithNoArtifactRecordedYet()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        // Simulates the crash point ConfirmNodeAsync's own StartAttemptAsync call would have reached:
        // the node is `running`, but RecordConfirmationAsync never ran.
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "confirm", node.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);

        RecordConfirmationResult resumed = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], node.Version,
            SprintScheduler.ConfirmNodeKey(sprintId, node), cancellationToken);

        Assert.True(resumed.Succeeded, resumed.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        Assert.Single(await scheduler.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // A crash landing after RecordConfirmationAsync durably lands but before CompleteAttemptAsync
    // leaves the node `running` with an artifact already on record. A same-outcome resume must
    // complete the node without minting a second, duplicate artifact.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncResumesARunningAttemptWithAMatchingArtifactAlreadyRecorded()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "confirm", node.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        RecordConfirmationResult preCrash = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], cancellationToken);
        Assert.True(preCrash.Succeeded, preCrash.DiagnosticCode);

        RecordConfirmationResult resumed = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "irrelevant on replay",
            [new(ConfirmationEvidenceKind.Inspection, "irrelevant on replay")], node.Version,
            SprintScheduler.ConfirmNodeKey(sprintId, node), cancellationToken);

        Assert.True(resumed.Succeeded, resumed.DiagnosticCode);
        Assert.Equal(preCrash.Confirmation!.ConfirmationId, resumed.Confirmation!.ConfirmationId);
        Assert.Single(await scheduler.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
    }

    // The same crash point as above (an artifact already durably recorded, node still `running`),
    // but the resumed call now presents a DIFFERENT outcome -- must be refused rather than silently
    // overriding the already-durable verdict while the node has not even reached a terminal state
    // yet (ADR 0034's review: the terminal-branch protection alone is not enough).
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncRefusesADecisionFlipWhileStillRunning()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot node = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "confirm", node.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        RecordConfirmationResult preCrash = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], cancellationToken);
        Assert.True(preCrash.Succeeded, preCrash.DiagnosticCode);

        RecordConfirmationResult flipped = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.NotConfirmed, "Actually does not.",
            [new(ConfirmationEvidenceKind.Inspection, "Found a regression on closer review.")], node.Version,
            SprintScheduler.ConfirmNodeKey(sprintId, node), cancellationToken);

        Assert.False(flipped.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeTransitionInvalid, flipped.DiagnosticCode);
        Assert.Single(await scheduler.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, state.Nodes["confirm"].State);
    }

    // A fresh attempt (this node re-armed to `ready` by a supersession of an earlier, unrelated
    // attempt) must never reuse a stale artifact left over from that earlier attempt -- only a
    // *resumed* call for the SAME still-running attempt may do that (and only once its outcome is
    // checked to match, per the sibling `running`-branch tests above). Regression for round 2's own
    // finding on PR #75: the reuse-instead-of-record check originally applied to every non-resuming
    // path too, letting a stale, unrelated verdict silently win over what a fresh call requested.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmNodeAsyncNeverReusesAStaleArtifactFromASupersededAttemptOnAFreshCall()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmThenTestWorkGraph), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot originalNode = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "confirm", originalNode.Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        RecordConfirmationResult stale = await scheduler.RecordConfirmationAsync(
            environment.ProjectRoot, sprintId, "confirm", ConfirmationOutcome.Confirmed, "Met the DoD (stale).",
            [new(ConfirmationEvidenceKind.Execution, "Ran the suite.")], cancellationToken);
        Assert.True(stale.Succeeded, stale.DiagnosticCode);

        // Re-arms the node from `running` back to `ready` on a fresh attempt id -- the same
        // Running -> Failed -> Ready sequence ADR 0018 documents, leaving the stale artifact above on
        // record but unlinked to anything the node itself still points at.
        AttemptSnapshot attempt = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Attempts[started.AttemptId!.Value.ToString("D")];
        CompleteAttemptResult superseded = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, attempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, attempt), true, "Re-run confirmation.", cancellationToken);
        Assert.True(superseded.Succeeded, superseded.DiagnosticCode);
        NodeSnapshot readyAgain = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["confirm"];
        Assert.Equal(NodeState.Ready, readyAgain.State);

        RecordConfirmationResult fresh = await scheduler.ConfirmNodeAsync(
            environment.ProjectRoot,
            sprintId,
            "confirm",
            ConfirmationOutcome.NotConfirmed,
            "Actually does not meet the DoD.",
            [new(ConfirmationEvidenceKind.Inspection, "Found a regression on re-review.")],
            readyAgain.Version,
            SprintScheduler.ConfirmNodeKey(sprintId, readyAgain),
            cancellationToken);

        Assert.True(fresh.Succeeded, fresh.DiagnosticCode);
        Assert.Equal(ConfirmationOutcome.NotConfirmed, fresh.Confirmation!.Outcome);
        Assert.NotEqual(stale.Confirmation!.ConfirmationId, fresh.Confirmation.ConfirmationId);
        Assert.Equal(
            2, (await scheduler.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken)).Count);
        SprintWorkflowState finalState =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, finalState.Sprint.State);
        Assert.Equal("confirmation", finalState.Sprint.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ImplementationCriticalGraphBuilderProducesAValidGraphWithIsolatedRoles()
    {
        IReadOnlyList<NodeDefinition> graph = Forge.Compiler.ImplementationCriticalGraphBuilder.Build();

        Assert.True(SprintGraphValidator.IsValid(graph));
        Assert.Equal(
            [
                NodeRole.Intake, NodeRole.Planning, NodeRole.Implementation, NodeRole.Confirmation,
                NodeRole.TestWork, NodeRole.Review, NodeRole.HumanApproval, NodeRole.Finalization,
            ],
            graph.Select(node => node.Role));
        NodeDefinition testWork = graph.Single(node => node.Role == NodeRole.TestWork);
        Assert.Contains(Forge.Compiler.ImplementationCriticalGraphBuilder.ConfirmationNodeId, testWork.DependsOn);
        NodeDefinition humanApproval = graph.Single(node => node.Role == NodeRole.HumanApproval);
        Assert.Equal(NodeKind.HumanGate, humanApproval.Kind);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintDefaultsToTheImplementationCriticalGraphWhenNoneIsSupplied()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(
            Forge.Compiler.ImplementationCriticalGraphBuilder.Build().Select(node => node.Id),
            definition.Graph.Select(node => node.Id));
    }

    private static readonly IReadOnlyList<NodeDefinition> ConfirmThenTestWorkGraph =
    [
        new("confirm", NodeKind.Work, [], NodeRole.Confirmation),
        new("test_work", NodeKind.Work, ["confirm"], NodeRole.TestWork),
    ];

    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    private static async Task SucceedAsync(
        SprintScheduler scheduler,
        string root,
        SprintId sprintId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        StartAttemptResult started = await scheduler.StartAttemptAsync(root, sprintId, nodeId, 2, cancellationToken);
        Assert.True(started.Succeeded);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            root, sprintId, nodeId, started.AttemptId!, true, SampleDigest, [], [], cancellationToken);
        Assert.True(completed.Succeeded);
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
