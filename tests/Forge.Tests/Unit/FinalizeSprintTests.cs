using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Stage 11, ADR 0036: the human-only `workflow.finalize` capability
/// (<see cref="ForgeApplication.FinalizeSprintAsync"/>), and the
/// <see cref="SprintDefinition.DefaultBranch"/> freezing it depends on
/// (<see cref="SprintOrchestrator.CreateSprintAsync"/>). Unlike `confirm`/`test-work`, the real
/// action here is genuine git I/O (<see cref="IRepository.MergeSprintIntoDefaultBranchAsync"/>), so
/// these tests drive it through a <see cref="FakeRepository"/> rather than asserting purely on
/// <see cref="SprintScheduler"/> state.
/// </summary>
public sealed class FinalizeSprintTests
{
    private static readonly IReadOnlyList<NodeDefinition> FinalizationGraph =
        [new("finalization", NodeKind.Work, [], NodeRole.Finalization)];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncMergesAndCompletesTheSprintOnSuccess()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FinalizeSprintResult result = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(NodeState.Succeeded, result.Node!.State);
        Assert.Equal(SprintState.Completed, result.Sprint!.State);
        (string DefaultBranch, string SourceBranch) call = Assert.Single(repository.MergeCalls);
        Assert.Equal("main", call.DefaultBranch);
        Assert.Equal(WorktreeLayout.IntegrationBranch(sprintId), call.SourceBranch);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Completed, state.Sprint.State);
        Assert.Equal(NodeState.Succeeded, state.Nodes["finalization"].State);
    }

    // A failed merge (dirty tree, wrong branch, diverged) goes through the same generic
    // per-node retry budget every other Work node already has: CompleteAttemptAsync's own
    // "workflow.node_retrying" step resets the node back to `ready` (not stuck `failed`) as long as
    // fewer than MaxAutomaticRetries + 1 attempts have landed, so a human who fixes the underlying
    // issue can just run `forge finalize` again.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncAllowsACleanRetryAfterAFailedMerge()
    {
        FakeRepository repository = new(defaultBranch: "main")
        {
            MergeResult = GitOperationResult.Fail(DiagnosticCodes.RepositoryDirty),
        };
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FinalizeSprintResult failed = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.False(failed.Succeeded);
        Assert.Equal(DiagnosticCodes.RepositoryDirty, failed.DiagnosticCode);
        SprintWorkflowState afterFailure =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // Automatically reset to `ready`, not stuck at `failed` -- the first of MaxAutomaticRetries.
        Assert.Equal(NodeState.Ready, afterFailure.Nodes["finalization"].State);
        Assert.Equal(SprintState.Running, afterFailure.Sprint.State);

        repository.MergeResult = GitOperationResult.Ok(new string('c', 40));
        FinalizeSprintResult retried = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.True(retried.Succeeded, retried.DiagnosticCode);
        Assert.Equal(SprintState.Completed, retried.Sprint!.State);
        Assert.Equal(2, repository.MergeCalls.Count);
    }

    // A permanently-failed node (retry budget exhausted) reports Node alone, matching
    // FinalizeSprintResult's own doc comment ("a failed merge... reports Node alone") -- the sprint
    // never moved in this case, so nothing about it should be reported back.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncReportsOnlyTheNodeOnceRetriesAreExhausted()
    {
        FakeRepository repository = new(defaultBranch: "main")
        {
            MergeResult = GitOperationResult.Fail(DiagnosticCodes.RepositoryDirty),
        };
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FinalizeSprintResult result = new(false, null, null, DiagnosticCodes.None);
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            result = await environment.Application.FinalizeSprintAsync(
                environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);
        }

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.RepositoryDirty, result.DiagnosticCode);
        Assert.Equal(NodeState.Failed, result.Node!.State);
        Assert.Null(result.Sprint);

        FinalizeSprintResult replay = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.False(replay.Succeeded);
        Assert.Equal(NodeState.Failed, replay.Node!.State);
        Assert.Null(replay.Sprint);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncRequiresConfirmation()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FinalizeSprintResult result = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", false, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfirmationRequired, result.DiagnosticCode);
        Assert.Empty(repository.MergeCalls);
    }

    // A sprint frozen before DefaultBranch existed (or with a detached HEAD, back when that was
    // still possible) has nothing to merge into -- checked before the finalization attempt even
    // starts, rather than starting one that could never succeed.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncFailsClosedWhenDefaultBranchIsUnavailable()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        // Simulates a sprint frozen before this field existed -- written directly through the store,
        // bypassing CreateSprintAsync's own (now-mandatory) DefaultBranch freeze.
        SprintDefinition original =
            (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        await store.SaveDefinitionAsync(environment.ProjectRoot, original with { DefaultBranch = null }, cancellationToken);

        FinalizeSprintResult result = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDefaultBranchUnavailable, result.DiagnosticCode);
        Assert.Empty(repository.MergeCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["finalization"].State);
    }

    // A stateless caller (the CLI) retrying after its own response was lost must resolve to what
    // already happened rather than attempting a second, redundant merge.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncResumedAfterAlreadySucceededDoesNotReMerge()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        FinalizeSprintResult first = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);
        Assert.True(first.Succeeded, first.DiagnosticCode);

        FinalizeSprintResult replay = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.True(replay.Succeeded, replay.DiagnosticCode);
        Assert.Equal(SprintState.Completed, replay.Sprint!.State);
        Assert.Single(repository.MergeCalls);
    }

    // A crash or dropped call between CompleteAttemptAsync (marks the node Succeeded) and
    // CompleteSprintAsync (the only path that ever appends ready_to_finalize -> completed) would
    // otherwise leave the sprint wedged forever: the node is durably Succeeded, but nothing ever
    // drives the sprint the rest of the way. Simulated here by performing exactly the first half of
    // CompleteFinalizationAsync's two writes directly through SprintScheduler, then resuming through
    // the public FinalizeSprintAsync entry point the way a retried CLI call would.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FinalizeSprintAsyncResumedAfterANodeCompletedWithoutSprintCompletionFinishesTheSprint()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        SprintWorkflowState before = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "finalization", before.Nodes["finalization"].Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        string digest = $"sha256:{new string('a', 64)}";
        CompleteAttemptResult completedAttempt = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "finalization", started.AttemptId!, true, digest,
            outputs: [digest], diagnostics: [], cancellationToken);
        Assert.True(completedAttempt.Succeeded, completedAttempt.DiagnosticCode);
        SprintWorkflowState wedged = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, wedged.Nodes["finalization"].State);
        Assert.Equal(SprintState.ReadyToFinalize, wedged.Sprint.State);

        FinalizeSprintResult resumed = await environment.Application.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", true, cancellationToken);

        Assert.True(resumed.Succeeded, resumed.DiagnosticCode);
        Assert.Equal(SprintState.Completed, resumed.Sprint!.State);
        Assert.Empty(repository.MergeCalls);
        SprintWorkflowState after = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Completed, after.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintAsyncFreezesTheProjectsCurrentBranchAsDefaultBranch()
    {
        FakeRepository repository = new(defaultBranch: "feature/whatever");
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        SprintDefinition definition =
            (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal("feature/whatever", definition.DefaultBranch);
    }

    // A detached HEAD has no branch name to freeze -- finalization would otherwise have nothing to
    // merge into, no matter how far the sprint gets. Refusing once, at creation, is simpler than
    // letting every sprint carry a null DefaultBranch finalization has to fail closed on later.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintAsyncRefusesADetachedHead()
    {
        FakeRepository repository = new(defaultBranch: null);
        using TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.RepositoryDetachedHead, result.DiagnosticCode);
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
}
