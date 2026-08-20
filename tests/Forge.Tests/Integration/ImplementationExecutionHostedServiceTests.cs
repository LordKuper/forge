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
/// <see cref="ImplementationExecutionHostedService"/> is Stage 11's third node executor and the
/// first whose provider run is meant to edit files. These tests decouple the executor's own
/// orchestration from real `git.exe` (<see cref="FakeWorktreeManager"/> — the real commit/integrate
/// sequence is already exercised by `GitIsolationTests`) and from a real provider process
/// (<see cref="FakeRunnableLlmProvider"/>), the same boundary
/// <c>PlanningExecutionHostedServiceTests</c> already draws.
/// </summary>
public sealed class ImplementationExecutionHostedServiceTests
{
    private const string IntakeNodeId = ImplementationCriticalGraphBuilder.IntakeNodeId;
    private const string PlanningNodeId = ImplementationCriticalGraphBuilder.PlanningNodeId;
    private const string ImplementationNodeId = ImplementationCriticalGraphBuilder.ImplementationNodeId;
    private const string ConfirmationNodeId = ImplementationCriticalGraphBuilder.ConfirmationNodeId;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AReadyImplementationNodeSucceedsCommitsIntegratesAndRecordsAHandoff()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Added the feature module and its tests.";
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                // Simulates the provider editing files: the executor's own dirty check afterward
                // must observe this, matching the real IWorktreeManager.IsDirtyAsync contract.
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(ProviderRunResult.Success([], new ProviderTerminalResult(summary)));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan: add a feature module.", cancellationToken);
        SprintDefinition definition = (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
            await WaitForHandoffAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Outputs);

        // The prompt invites edits (unlike planning's) and carries planning's own real handoff.
        string prompt = Assert.Single(provider.Calls).Prompt;
        Assert.Contains("Edit, create, or delete", prompt, StringComparison.Ordinal);
        Assert.Contains("The plan: add a feature module.", prompt, StringComparison.Ordinal);

        // Staged and committed exactly once, at the attempt worktree the provider actually wrote
        // into.
        (string Path, string Message) commit = Assert.Single(worktrees.Commits);
        Assert.Equal(provider.Calls[0].WorkingDirectory, commit.Path);
        Assert.Contains(summary, commit.Message, StringComparison.Ordinal);

        Handoff handoff = Assert.Single(
            await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken),
            item => item.NodeId.Value == ImplementationNodeId);
        Assert.Equal(definition.BaseCommit, handoff.BaseSha);
        Assert.Equal(summary, handoff.Summary);
        Assert.Equal([ConfirmationNodeId], handoff.NextNodeIds);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AProviderThatMakesNoEditsFailsWithoutCommittingOrRecordingAHandoff()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            // Never marks the worktree dirty -- the provider ran but left nothing to commit.
            (_, _, _, _) => Task.FromResult(
                ProviderRunResult.Success([], new ProviderTerminalResult("Nothing needed changing."))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForTerminalFailureAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        IReadOnlyList<NodeResult> results =
            await ImplementationResultsAsync(store, environment, sprintId, cancellationToken);
        Assert.All(results, result => Assert.Equal(NodeOutcome.Failed, result.State));
        NodeDiagnostic diagnostic = Assert.Single(results[0].Diagnostics);
        Assert.Equal(DiagnosticCodes.ImplementationNoChanges, diagnostic.Code);
        Assert.Equal("git", diagnostic.Category);
        Assert.Empty(worktrees.Commits);
        Assert.DoesNotContain(
            await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken),
            item => item.NodeId.Value == ImplementationNodeId);
    }

    // ADR 0006/0018's durable rate-limit wait, mirroring PlanningExecutionHostedServiceTests' own
    // coverage of the identical routing for the implementation phase.
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
        SprintId sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
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

        NodeResult result = Assert.Single(
            await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Failed, result.State);
        Assert.Equal(ProviderDiagnosticCodes.RateLimited, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(result.AttemptId, deferred.AttemptId);
    }

    // Nothing to implement without a real plan -- the node must stay untouched, never started.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnImplementationNodeWithNoPlanningHandoffIsNeitherStartedNorRepeatedlyReportedAsRejected()
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
        // Completes intake and planning's own attempt but deliberately never records planning's
        // handoff -- the shape a planning node stuck failing every retry would leave behind.
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, IntakeNodeId, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, PlanningNodeId, cancellationToken);

        RecordingLogger logger = new();
        ImplementationExecutionHostedService service = new(
            new ImplementationExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store, scheduler, environment.Resolve<SprintGitIsolation>(), worktrees,
            environment.Resolve<ProviderCatalog>(), environment.Resolve<IConfigurationRegistry>(), environment,
            environment.Application, logger);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForLogAsync(logger, "ImplementationExecutionDefinitionUnusable", cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Empty(provider.Calls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes[ImplementationNodeId].State);
        Assert.Empty(await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
    }

    private static ImplementationExecutionHostedService NewService(
        TestEnvironment environment, ISprintStore store, SprintScheduler scheduler) =>
        new(
            new ImplementationExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store,
            scheduler,
            environment.Resolve<SprintGitIsolation>(),
            environment.Resolve<IWorktreeManager>(),
            environment.Resolve<ProviderCatalog>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment,
            environment.Application,
            NullLogger<ImplementationExecutionHostedService>.Instance);

    private static async Task<SprintId> CreateSprintReadyForImplementationAsync(
        TestEnvironment environment,
        SprintOrchestrator orchestrator,
        SprintScheduler scheduler,
        ISprintStore store,
        string planSummary,
        CancellationToken cancellationToken)
    {
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, IntakeNodeId, cancellationToken);
        await CompleteNodeDirectlyAsync(scheduler, environment.ProjectRoot, sprintId, PlanningNodeId, cancellationToken);
        SprintDefinition definition = (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        RecordHandoffResult handoff = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, PlanningNodeId, definition.BaseCommit, planSummary,
            decisions: [], openRisks: [], nextNodeIds: [ImplementationNodeId], cancellationToken);
        Assert.True(handoff.Succeeded, handoff.DiagnosticCode);
        return sprintId;
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
    /// executor would normally do it -- that executor's own behavior is a different test file's job
    /// (`IntakeExecutionHostedServiceTests`/`PlanningExecutionHostedServiceTests`); this file's own
    /// tests are about the implementation node specifically, which the built-in graph makes depend
    /// on both.</summary>
    private static async Task CompleteNodeDirectlyAsync(
        SprintScheduler scheduler, string projectRoot, SprintId sprintId, string nodeId, CancellationToken cancellationToken)
    {
        SprintWorkflowState state = (await scheduler.AdvanceGraphAsync(projectRoot, sprintId, cancellationToken));
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            projectRoot, sprintId, nodeId, state.Nodes[nodeId].Version, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            projectRoot, sprintId, nodeId, started.AttemptId!, true,
            "sha256:" + new string('0', 64), [], [], cancellationToken);
        Assert.True(completed.Succeeded, completed.DiagnosticCode);
    }

    private static async Task<IReadOnlyList<NodeResult>> ImplementationResultsAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken) =>
        [.. (await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken))
            .Where(result => result.NodeId.Value == ImplementationNodeId)];

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
            observed = state.Nodes[ImplementationNodeId].State;
            if (observed == expected)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail(
            $"The implementation node of sprint {sprintId.Value:D} stayed '{observed}' instead of '{expected}'.");
    }

    /// <summary>Same rationale as `PlanningExecutionHostedServiceTests`' own version: `node_failed`
    /// and the bounded auto-retry's own `node_retrying` are two separate durable appends, so a poll
    /// for `Failed` alone is ambiguous with the transient pre-retry window.</summary>
    private static async Task WaitForTerminalFailureAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        NodeSnapshot? observed = null;
        while (stopwatch.Elapsed < PollTimeout)
        {
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            observed = state.Nodes[ImplementationNodeId];
            if (observed.State == NodeState.Failed &&
                observed.AttemptCount >= SprintScheduler.MaxAutomaticRetries + 1)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail(
            $"The implementation node of sprint {sprintId.Value:D} never reached terminal failure " +
            $"(last observed state={observed?.State}, attemptCount={observed?.AttemptCount}).");
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

    private static async Task WaitForHandoffAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if ((await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken))
                .Any(item => item.NodeId.Value == ImplementationNodeId))
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D} never recorded an implementation handoff.");
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

    /// <summary>Captures what the service logged, matching `IntakeExecutionHostedServiceTests`' own
    /// helper shape.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<ImplementationExecutionHostedService>
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
