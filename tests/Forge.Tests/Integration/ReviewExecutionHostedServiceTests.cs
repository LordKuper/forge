using System.Diagnostics;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host;
using Forge.Providers;
using Forge.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.IntegrationTests;

/// <summary>
/// <see cref="ReviewExecutionHostedService"/> is Stage 11's fourth node executor and the first
/// production caller of <see cref="SprintScheduler.RecordReviewIterationAsync"/>. These tests
/// decouple the executor's own orchestration from real `git.exe` (<see cref="FakeWorktreeManager"/>)
/// and a real provider process (<see cref="FakeRunnableLlmProvider"/>), the same boundary every
/// prior node-executor test file already draws.
/// </summary>
public sealed class ReviewExecutionHostedServiceTests
{
    private const string IntakeNodeId = ImplementationCriticalGraphBuilder.IntakeNodeId;
    private const string PlanningNodeId = ImplementationCriticalGraphBuilder.PlanningNodeId;
    private const string ImplementationNodeId = ImplementationCriticalGraphBuilder.ImplementationNodeId;
    private const string ConfirmationNodeId = ImplementationCriticalGraphBuilder.ConfirmationNodeId;
    private const string TestWorkNodeId = ImplementationCriticalGraphBuilder.TestWorkNodeId;
    private const string ReviewNodeId = ImplementationCriticalGraphBuilder.ReviewNodeId;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnApprovedVerdictSucceedsOnTheFirstIteration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new()
        {
            Diff = "diff --git a/x.txt b/x.txt\n+hello",
            DiffTruncated = true,
        };
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (prompt, _, _, _) =>
            {
                // The diff must actually reach the prompt for a review to mean anything, and a
                // budget-truncated diff must say so rather than silently passing partial content off
                // as the whole change.
                Assert.Contains("diff --git a/x.txt b/x.txt", prompt, StringComparison.Ordinal);
                Assert.Contains("(truncated)", prompt, StringComparison.Ordinal);
                return Task.FromResult(ProviderRunResult.Success(
                    [], new ProviderTerminalResult("Looks correct.\nAPPROVED")));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForReviewAsync(environment, orchestrator, scheduler, store, cancellationToken);

        ReviewExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(await ReviewResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Outputs);

        ReviewIterationRecord iteration = Assert.Single(
            await scheduler.GetReviewIterationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(1, iteration.Iteration);
        Assert.Equal(ReviewOutcome.Approved, iteration.Outcome);
        Assert.Equal(ReviewDimension.Implementation, iteration.Dimension);
        Assert.Equal(ReviewerKind.External, iteration.ReviewerKind);
        Assert.Empty(iteration.ExternalFindings);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AChangesRequestedVerdictLeavesTheNodeRunningAndAccumulatesFurtherIterations()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(ProviderRunResult.Success(
                [], new ProviderTerminalResult("Needs work.\nCHANGES_REQUESTED"))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForReviewAsync(environment, orchestrator, scheduler, store, cancellationToken);

        ReviewExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            // A repeated identical (empty) finding set converges after its second occurrence
            // (ReviewConvergencePolicy.HasRepeatedExternalFindingSet), so wait for exactly the
            // second iteration to prove the node stays open for at least one full extra cycle
            // before that gate would fire, distinguishing "genuinely not converged yet" from
            // "already blocked."
            await WaitForIterationCountAsync(scheduler, environment, sprintId, 2, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        // Two iterations in and the node has neither succeeded nor failed -- MaxAutomaticRetries (2)
        // would have already exhausted an ordinary Work node's attempt budget by now if this
        // executor mistakenly routed ChangesRequested through the generic failure path.
        Assert.Equal(NodeState.Running, state.Nodes[ReviewNodeId].State);
        Assert.Empty(await ReviewResultsAsync(store, environment, sprintId, cancellationToken));
    }

    // ADR 0006's repeated-finding-set convergence gate: two consecutive ChangesRequested verdicts
    // with the identical (here: empty) normalized finding set block the sprint and complete the
    // review node -- a stopping point handed to a human, not a silent stall.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARepeatedIdenticalFindingSetConvergesAndBlocksTheSprint()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(ProviderRunResult.Success(
                [], new ProviderTerminalResult("Still needs work.\nCHANGES_REQUESTED"))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForReviewAsync(environment, orchestrator, scheduler, store, cancellationToken);

        ReviewExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        IReadOnlyList<ReviewIterationRecord> iterations =
            await scheduler.GetReviewIterationsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(2, iterations.Count);
        Assert.All(iterations, iteration => Assert.Equal(ReviewOutcome.ChangesRequested, iteration.Outcome));

        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, state.Sprint.State);
        Assert.Equal("review_convergence", state.Sprint.BlockedReason);

        NodeResult result = Assert.Single(await ReviewResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnUnparseableVerdictIsRecordedAsAFailureWithoutRecordingAReviewIteration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(ProviderRunResult.Success(
                [], new ProviderTerminalResult("I think this is fine, probably."))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForReviewAsync(environment, orchestrator, scheduler, store, cancellationToken);

        ReviewExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForTerminalFailureAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        IReadOnlyList<NodeResult> results = await ReviewResultsAsync(store, environment, sprintId, cancellationToken);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(NodeOutcome.Failed, result.State));
        Assert.Equal(
            ProviderDiagnosticCodes.ReviewVerdictUnparseable, Assert.Single(results[0].Diagnostics).Code);
        Assert.Empty(await scheduler.GetReviewIterationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARateLimitedProviderFailureDefersRoutingInsteadOfBeingTreatedAsAnOrdinaryFailure()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(
                ProviderRunResult.Failed(ProviderFailureKind.RateLimited, "slow down")));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForReviewAsync(environment, orchestrator, scheduler, store, cancellationToken);

        ReviewExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        RouteDecision deferred;
        try
        {
            deferred = await WaitForDeferredRouteDecisionAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(await ReviewResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Failed, result.State);
        Assert.Equal(ProviderDiagnosticCodes.RateLimited, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(result.AttemptId, deferred.AttemptId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AReviewNodeWithNoImplementationHandoffIsNeitherStartedNorRepeatedlyReportedAsRejected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => throw new InvalidOperationException("The provider must not run."));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, IntakeNodeId, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, PlanningNodeId, cancellationToken);
        // implementation's own attempt completes, but deliberately never records its handoff.
        await CompleteNodeDirectlyAsync(
            scheduler, environment.ProjectRoot, sprintId, ImplementationNodeId, cancellationToken);
        await CompleteConfirmationAndTestWorkAsync(scheduler, environment.ProjectRoot, sprintId, cancellationToken);

        RecordingLogger logger = new();
        ReviewExecutionHostedService service = new(
            new ReviewExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store, scheduler, environment.Resolve<SprintGitIsolation>(), environment.Resolve<ProviderCatalog>(),
            environment.Resolve<IConfigurationRegistry>(), environment, environment.Application, logger);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForLogAsync(logger, "ReviewExecutionDefinitionUnusable", cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Empty(provider.Calls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes[ReviewNodeId].State);
        Assert.Empty(await ReviewResultsAsync(store, environment, sprintId, cancellationToken));
    }

    private static ReviewExecutionHostedService NewService(
        TestEnvironment environment, ISprintStore store, SprintScheduler scheduler) =>
        new(
            new ReviewExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store,
            scheduler,
            environment.Resolve<SprintGitIsolation>(),
            environment.Resolve<ProviderCatalog>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment,
            environment.Application,
            NullLogger<ReviewExecutionHostedService>.Instance);

    private static async Task<SprintId> CreateSprintReadyForReviewAsync(
        TestEnvironment environment,
        SprintOrchestrator orchestrator,
        SprintScheduler scheduler,
        ISprintStore store,
        CancellationToken cancellationToken)
    {
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, IntakeNodeId, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, PlanningNodeId, cancellationToken);
        await CompleteNodeDirectlyAsync(
            scheduler, environment.ProjectRoot, sprintId, ImplementationNodeId, cancellationToken);
        SprintDefinition definition = (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        RecordHandoffResult handoff = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, ImplementationNodeId, definition.BaseCommit,
            "Implemented the feature module.", decisions: [], openRisks: [], nextNodeIds: [ReviewNodeId],
            cancellationToken);
        Assert.True(handoff.Succeeded, handoff.DiagnosticCode);
        await CompleteConfirmationAndTestWorkAsync(scheduler, environment.ProjectRoot, sprintId, cancellationToken);
        return sprintId;
    }

    /// <summary>The review node depends on `test_work`, which the scheduler only ever promotes past
    /// `pending` once a `Confirmed` artifact is on record for its own `confirmation` dependency
    /// (<c>SprintScheduler.IsTestWorkEligibleAsync</c>) -- neither node has an executor yet, so every
    /// review test must drive both directly, exactly like <see cref="CompleteNodeDirectlyAsync"/> does
    /// for the nodes that already have one.</summary>
    private static async Task CompleteConfirmationAndTestWorkAsync(
        SprintScheduler scheduler, string projectRoot, SprintId sprintId, CancellationToken cancellationToken)
    {
        await CompleteNodeDirectlyAsync(scheduler, projectRoot, sprintId, ConfirmationNodeId, cancellationToken);
        RecordConfirmationResult confirmed = await scheduler.RecordConfirmationAsync(
            projectRoot,
            sprintId,
            ConfirmationNodeId,
            ConfirmationOutcome.Confirmed,
            "Met the agreed definition of done.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the full test suite locally; all green.")],
            cancellationToken);
        Assert.True(confirmed.Succeeded, confirmed.DiagnosticCode);
        await CompleteNodeDirectlyAsync(scheduler, projectRoot, sprintId, TestWorkNodeId, cancellationToken);
    }

    private static async Task<SprintId> CreateRunningSprintAsync(
        TestEnvironment environment,
        SprintOrchestrator orchestrator,
        ISprintStore store,
        CancellationToken cancellationToken)
    {
        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        Assert.True(created.Succeeded);
        SprintId sprintId = created.SprintId!;

        SprintWorkflowState draft = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Sprint.Version,
                SprintOrchestrator.RunSprintKey(draft.Sprint)), cancellationToken);
        Assert.True(toReady.Succeeded);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)), cancellationToken);
        Assert.True(toRunning.Succeeded);
        return sprintId;
    }

    /// <summary>Drives one node straight to `succeeded` through the scheduler, bypassing whichever
    /// executor would normally do it -- that executor's own behavior is a different test file's job;
    /// this file's own tests are about the review node specifically.</summary>
    private static async Task CompleteNodeDirectlyAsync(
        SprintScheduler scheduler, string projectRoot, SprintId sprintId, string nodeId, CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await scheduler.AdvanceGraphAsync(projectRoot, sprintId, cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            projectRoot, sprintId, nodeId, state.Nodes[nodeId].Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            projectRoot, sprintId, nodeId, started.AttemptId!, true,
            "sha256:" + new string('0', 64), [], [], cancellationToken);
        Assert.True(completed.Succeeded, completed.DiagnosticCode);
    }

    private static async Task<IReadOnlyList<NodeResult>> ReviewResultsAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken) =>
        [.. (await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken))
            .Where(result => result.NodeId.Value == ReviewNodeId)];

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private static async Task WaitForNodeStateAsync(
        ISprintStore store,
        TestEnvironment environment,
        SprintId sprintId,
        NodeState expected,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        NodeState observed = NodeState.Pending;
        while (stopwatch.Elapsed < PollTimeout)
        {
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            observed = state.Nodes[ReviewNodeId].State;
            if (observed == expected)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"The review node of sprint {sprintId.Value:D} stayed '{observed}' instead of '{expected}'.");
    }

    private static async Task WaitForTerminalFailureAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        NodeSnapshot? observed = null;
        while (stopwatch.Elapsed < PollTimeout)
        {
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            observed = state.Nodes[ReviewNodeId];
            if (observed.State == NodeState.Failed &&
                observed.AttemptCount >= SprintScheduler.MaxAutomaticRetries + 1)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail(
            $"The review node of sprint {sprintId.Value:D} never reached terminal failure " +
            $"(last observed state={observed?.State}, attemptCount={observed?.AttemptCount}).");
    }

    private static async Task WaitForIterationCountAsync(
        SprintScheduler scheduler,
        TestEnvironment environment,
        SprintId sprintId,
        int expected,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int observed = 0;
        while (stopwatch.Elapsed < PollTimeout)
        {
            observed = (await scheduler.GetReviewIterationsAsync(environment.ProjectRoot, sprintId, cancellationToken)).Count;
            if (observed >= expected)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D} only recorded {observed} review iterations, expected {expected}.");
    }

    private static async Task<RouteDecision> WaitForDeferredRouteDecisionAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            IReadOnlyList<RouteDecision> decisions =
                await store.GetRouteDecisionsAsync(environment.ProjectRoot, sprintId, cancellationToken);
            RouteDecision? deferred = decisions.FirstOrDefault(decision => decision.Outcome == RouteOutcome.Deferred);
            if (deferred is not null)
            {
                return deferred;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D} never recorded a deferred routing decision.");
        return null!;
    }

    private static async Task WaitForLogAsync(
        RecordingLogger logger, string eventName, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if (logger.Snapshot().Any(entry => entry.StartsWith(eventName, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"The service never logged '{eventName}'.");
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<ReviewExecutionHostedService>
    {
        private readonly List<string> entries = [];

        public IReadOnlyList<string> Snapshot()
        {
            lock (entries)
            {
                return [.. entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (entries)
            {
                entries.Add($"{eventId.Name}: {formatter(state, exception)}");
            }
        }
    }
}
