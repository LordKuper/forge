using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Targeted crash/conflict-injection coverage for the Stage 6 durability review: failed
/// appends must be propagated (never silently swallowed), and the compound scheduler operations
/// must resume correctly from wherever an interrupted prior call left off.</summary>
public sealed class SprintResilienceTests
{
    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartAttemptFailsCleanlyWhenTheNodeAppendConflicts()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        store.FailAt[store.AppendCount + 1] = AppendOutcome.Conflict;
        StartAttemptResult result =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, result.DiagnosticCode);
        Assert.Null(result.AttemptId);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["a"].State);
        Assert.Empty(state.Attempts);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartAttemptFailsCleanlyWhenOnlyTheAttemptAppendConflicts()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        // The node append (1st) succeeds; only the attempt-creation append (2nd) fails.
        store.FailAt[store.AppendCount + 2] = AppendOutcome.Conflict;
        StartAttemptResult result =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        // Never a false success: the caller must see this failed even though the node already
        // durably moved to `running` with no attempt to show for it.
        Assert.False(result.Succeeded);
        Assert.Null(result.AttemptId);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, state.Nodes["a"].State);
        Assert.Empty(state.Attempts);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAttemptResumesAnAttemptWalkInterruptedBeforeThisProcessStarted()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        // Simulate a crash that landed the attempt's first walk step (created -> preparing) but
        // nothing past it — as if an earlier `CompleteAttemptAsync` call died mid-sequence.
        await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Attempt, started.AttemptId!.Value.ToString("D"),
            "AttemptChanged", "workflow.attempt_transitioned", "preparing", 1, Guid.NewGuid(), cancellationToken);

        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        Assert.True(completed.Succeeded);
        Assert.Equal(NodeState.Succeeded, completed.Node!.State);
        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAttemptResumesAfterTheTerminalNodeAppendIsInterrupted()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        // Attempt walk is 4 appends (created->preparing->running->validating->succeeded); the 5th
        // append is the terminal node transition — fail exactly that one.
        int baseline = store.AppendCount;
        store.FailAt[baseline + 5] = AppendOutcome.Conflict;
        CompleteAttemptResult first = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        Assert.False(first.Succeeded);
        // The result is durable even though the node never reached its terminal transition — the
        // interrupted call is fully recoverable, not wedged.
        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        SprintWorkflowState midState =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, midState.Nodes["a"].State);

        CompleteAttemptResult second = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(NodeState.Succeeded, second.Node!.State);
        // Exactly one result — resuming must never duplicate the already-durable record.
        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveHumanGateResumesAfterTheSecondNodeAppendIsInterrupted()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];

        // Attempt creation + walk is 5 appends (created, then 4 more to succeeded); the 6th append
        // moves the node awaiting_human -> running, the 7th running -> succeeded — fail the 7th.
        int baseline = store.AppendCount;
        store.FailAt[baseline + 7] = AppendOutcome.Conflict;
        NodeActionResult first = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        Assert.False(first.Succeeded);
        SprintWorkflowState midState =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, midState.Nodes["gate"].State);
        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));

        // The retry re-submits the exact same (node, expectedNodeVersion, key) triple even though
        // the node's *current* version has since moved — that is the whole point: this is still the
        // same logical decision, not a new one.
        NodeActionResult second = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(NodeState.Succeeded, second.Node!.State);
        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReservedWorkflowArgumentsCannotBeOverriddenByExtraArguments()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        AppendOutcome created = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?> { ["to_state"] = "completed" });

        // The canonical `to_state` always wins over whatever an "extra" argument claims, whether it
        // names another legal state or garbage — a caller cannot smuggle an illegal transition in.
        Assert.True(created.Succeeded);
        Assert.Equal(SprintState.Draft, created.State!.Sprint.State);

        AppendOutcome advanced = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?> { ["to_state"] = "not-a-real-state" });

        Assert.True(advanced.Succeeded);
        Assert.Equal(SprintState.Ready, advanced.State!.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreatingAndRetryingASprintDoesNotMutateTheProjectManifest()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        Guid idempotencyKey = Guid.NewGuid();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string manifestPath = ProjectRootResolver.ManifestPath(environment.ProjectRoot);
        string before = await File.ReadAllTextAsync(manifestPath, cancellationToken);

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey), cancellationToken)).SprintId!;

        CreateSprintResult retried = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey), cancellationToken);

        Assert.True(retried.Succeeded);
        Assert.Equal(sprintId, retried.SprintId);
        Assert.Equal(before, await File.ReadAllTextAsync(manifestPath, cancellationToken));
        Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetryingCreateSprintAfterTheIdempotencyKeyIsLostStillConverges()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        Guid idempotencyKey = Guid.NewGuid();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey, Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;

        // Simulate a crash exactly between the durable event append and the idempotency-key write
        // (the store's own documented ordering), before creation was ever marked complete: the first
        // event is durable, but nothing else is. With a deterministic id, a retry recomputes the
        // *exact same* id, so — unlike the old random-id design — it must resume, not just orphan
        // and start over.
        string sprintDirectory = FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId);
        File.Delete(Path.Combine(sprintDirectory, "idempotency.json"));
        File.Delete(Path.Combine(sprintDirectory, "created.marker"));

        CreateSprintResult retried = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey, Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken);
        // A second retry must converge too, not just the first.
        CreateSprintResult retriedAgain = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey, Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken);

        Assert.True(retried.Succeeded);
        Assert.Equal(sprintId, retried.SprintId);
        Assert.True(retriedAgain.Succeeded);
        Assert.Equal(sprintId, retriedAgain.SprintId);
        Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetryingCreateSprintReusesTheAlreadyFrozenDefinitionInsteadOfReFreezingIt()
    {
        MutableRepository repository = new();
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        Guid idempotencyKey = Guid.NewGuid();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey, Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        string originalHead = repository.Head;

        // Simulate a crash exactly before the marker: the first event, the idempotency key, and the
        // definition are all already durable — a retry replays the first append through the store's
        // own idempotency check rather than hitting the conflict path, and it is precisely because
        // `definition.json` already exists that the reuse path below gets exercised at all.
        string sprintDirectory = FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId);
        File.Delete(Path.Combine(sprintDirectory, "created.marker"));
        // HEAD moves and the retry's caller supplies a different graph — neither may retroactively
        // change what this sprint already froze.
        repository.Head = new string('b', 40);

        CreateSprintResult retried = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey, Graph: [new("z", NodeKind.Work, [])]),
            cancellationToken);

        Assert.True(retried.Succeeded);
        Assert.Equal(sprintId, retried.SprintId);
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(originalHead, definition!.BaseCommit);
        Assert.Equal("a", Assert.Single(definition.Graph).Id);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(["a"], state.Nodes.Keys);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsyncNeverObservesASprintBeforeItIsMarkedCreated()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // A crash-simulated partial sprint: a durable first event, but creation was never marked
        // complete (the process died before that last step, exactly as a real crash would).
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);

        Assert.Empty(await log.ListAsync(root.Path, cancellationToken));
        // The directory-addressed methods still see it — it is fully recoverable, just not listed.
        Assert.NotNull(await log.LoadAsync(root.Path, sprintId, cancellationToken));

        await log.MarkSprintCreatedAsync(root.Path, sprintId, cancellationToken);

        Assert.Equal([sprintId], await log.ListAsync(root.Path, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetryingStartAttemptAfterAWedgedNodeConverges()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        // First call: the node append lands, the attempt append is interrupted — the node is left
        // `running` with no attempt to show for it.
        store.FailAt[store.AppendCount + 2] = AppendOutcome.Conflict;
        StartAttemptResult first =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.False(first.Succeeded);

        // A caller retries with the *same* arguments it originally used — it has no way to know the
        // node's version already moved — and must still converge instead of being told the node is
        // no longer `ready`.
        StartAttemptResult second =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        Assert.True(second.Succeeded);
        Assert.NotNull(second.AttemptId);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, state.Nodes["a"].State);
        Assert.Contains(second.AttemptId!.Value.ToString("D"), state.Attempts.Keys);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAttemptRejectsARetryThatFlipsTheOutcome()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        CompleteAttemptResult failed = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, SampleDigest, [], [],
            cancellationToken);
        Assert.True(failed.Succeeded);

        // A caller retries the *same* attempt id but now claims success — this must never be
        // silently accepted and flip the already-durable "failed" outcome to "succeeded".
        CompleteAttemptResult flipped = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        Assert.False(flipped.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, flipped.DiagnosticCode);
        NodeResult result = (await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken))
            .Single(item => item.AttemptId == started.AttemptId!);
        Assert.Equal(NodeOutcome.Failed, result.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveHumanGateRejectsARetryThatFlipsTheDecision()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];

        // Interrupt the approve walk after it has already moved the node to `running`.
        int baseline = store.AppendCount;
        store.FailAt[baseline + 7] = AppendOutcome.Conflict;
        NodeActionResult approve = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", true, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        Assert.False(approve.Succeeded);
        Assert.Equal(NodeState.Running, (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"].State);

        // A caller retries with the *opposite* decision for the same (node, version) — this must be
        // a stable failure, never a success that quietly overrides the in-flight approval.
        NodeActionResult reject = await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        Assert.False(reject.Succeeded);
        Assert.Equal(NodeState.Running, (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveNodeResultRejectsADifferentResultForTheSameAttemptId()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        NodeResult first = new(
            sprintId, new("a"), attemptId, NodeOutcome.Succeeded, now, now, SampleDigest, [], []);
        NodeResult conflicting = first with { State = NodeOutcome.Failed };

        await log.SaveNodeResultAsync(root.Path, first, cancellationToken);
        // An identical replay of the exact same result is always a safe no-op.
        await log.SaveNodeResultAsync(root.Path, first, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => log.SaveNodeResultAsync(root.Path, conflicting, cancellationToken));
        NodeResult stored = Assert.Single(await log.GetNodeResultsAsync(root.Path, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, stored.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAnOpenFindingAfterReadyToFinalizeBlocksTheSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        Assert.Equal(
            SprintState.ReadyToFinalize,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["src/Foo.cs:1"], null, cancellationToken);

        SprintSnapshot? sprint = await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(SprintState.Blocked, sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GatePromotionResumesAfterTheSecondAppendIsInterrupted()
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;

        // Draft -> ready needs no gate work; fail the *second* of the gate-promotion's two appends
        // (running -> awaiting_human) exactly when ready -> running fires the auto-promotion.
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        int baseline = store.AppendCount;
        store.FailAt[baseline + 3] = AppendOutcome.Conflict;
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);

        SprintWorkflowState stuck = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, stuck.Nodes["gate"].State);

        // A later, unrelated graph advance must still finish the interrupted promotion.
        SprintWorkflowState resumed =
            await scheduler.AdvanceGraphAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.Equal(NodeState.AwaitingHuman, resumed.Nodes["gate"].State);
        Assert.Equal(SprintState.AwaitingHuman, resumed.Sprint.State);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(1, SprintState.Blocked)]
    [InlineData(2, SprintState.Ready)]
    [InlineData(3, SprintState.Running)]
    public async Task FindingRecoveryResumesAfterEveryInterruptedAppend(
        int failedAppend,
        SprintState interruptedState)
    {
        using TestEnvironment environment = await InitializedAsync();
        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", started.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.High, "finding.example",
            new Dictionary<string, string?>(), ["src/Foo.cs:1"], null, cancellationToken);
        int baseline = store.AppendCount;
        store.FailAt[baseline + failedAppend] = AppendOutcome.Conflict;

        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding!.FindingId, FindingStatus.Resolved,
            cancellationToken);
        Assert.Equal(
            interruptedState,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);

        await scheduler.ResolveFindingAsync(
            environment.ProjectRoot, sprintId, recorded.Finding.FindingId, FindingStatus.Resolved,
            cancellationToken);

        Assert.Equal(
            SprintState.ReadyToFinalize,
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SprintIdentityIsStableAcrossDifferentPathCasingForTheSameProject()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux/macOS filesystems are case-sensitive; a differently cased path is a different path there,
            // not the same project under a different string, so this test's premise only holds on Windows.
            return;
        }

        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        Guid idempotencyKey = Guid.NewGuid();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult first = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, idempotencyKey), cancellationToken);
        // Same physical directory on a case-insensitive filesystem, different string — this must
        // resolve to the exact same sprint id, not create a second, divergent sprint.
        CreateSprintResult second = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot.ToUpperInvariant(), 1, idempotencyKey), cancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.SprintId, second.SprintId);
        Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
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
