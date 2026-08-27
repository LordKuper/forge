using System.ComponentModel;
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

    // Round 1 review of PR #73: a real bug, found and fixed. The commit subject was extracted from
    // the summary's own FIRST line only -- a summary whose first line happens to be blank (but a
    // later line has real content) collapsed to an empty `git commit -m ""` argument, which git
    // itself rejects outright, discarding a real, already-verified-dirty edit as a failure purely
    // because of the summary's own line breaks.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASummaryWhoseFirstLineIsBlankStillProducesAUsableCommitSubjectFromALaterLine()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "\n\nAdded the feature module.";
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(ProviderRunResult.Success([], new ProviderTerminalResult(summary)));
            });
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
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        (string Path, string Message) commit = Assert.Single(worktrees.Commits);
        Assert.Equal("Added the feature module.", commit.Message);
    }

    // Round 1 review of PR #73: a second real bug in the same method, found and fixed. Truncating
    // the commit subject at a fixed UTF-16 code-unit count with a bare span slice can land between
    // a surrogate pair's high and low halves, producing a malformed string with an unpaired high
    // surrogate at its end.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ACommitSubjectIsNeverTruncatedInsideASurrogatePair()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // U+1F600 ("😀") is a surrogate pair in UTF-16. Placed so the 200-character truncation
        // boundary falls exactly between its high and low halves.
        string summary = new string('a', 199) + "\U0001F600" + new string('b', 50);
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(ProviderRunResult.Success([], new ProviderTerminalResult(summary)));
            });
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
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        (string Path, string Message) commit = Assert.Single(worktrees.Commits);
        // The pair's high surrogate lands exactly at the 200-char cut point (index 199, after 199
        // 'a's) -- the fix must drop the whole pair rather than keep the lone high surrogate, so
        // the kept subject is 199 'a's plus the ellipsis, never a malformed 200th character.
        Assert.Equal(new string('a', 199) + "…", commit.Message);
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
            environment.Application, environment.Resolve<ActiveOperationRegistry>(),
            environment.Resolve<StopOperationCoordinator>(), logger);
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

    // Post-release audit (PR #101): confirmed correctness gap in the merged Slice 2 (ADR 0047) design -- the
    // durable stop intent's per-tick check (top of ExecuteImplementationAsync) only runs once,
    // before the provider starts, and was never re-checked between the provider returning success
    // and this executor's own commit/integrate. A stop that lands durably in that exact window
    // (recorded, but too late for ActiveOperationRegistry.TryCancel to matter since the provider is
    // already on its way out with a success result) previously still reached
    // SprintGitIsolation.CommitAttemptAsync/IntegrateAsync, letting the change reach the integration
    // branch despite the durably recorded stop -- violating plan section 7.1's "no partial change
    // reaches the integration branch". The provider delegate below simulates exactly this race
    // deterministically (no real concurrency needed): it records the durable stop intent directly
    // (bypassing StopOperationCoordinator.RequestStopAsync, whose own best-effort
    // ActiveOperationRegistry.TryCancel would have nothing left to cancel by then anyway) right
    // before returning success, so the provider's own cancellation token is never observed and
    // RunAsync returns success normally -- exactly the "the provider already returned success right
    // around that time" scenario the audit describes.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStopRequestedRightAsTheProviderSucceedsIsHonoredInsteadOfReachingIntegration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Added the feature module.";
        FakeWorktreeManager worktrees = new();
        ISprintStore? store = null;
        TestEnvironment? environmentRef = null;
        SprintId sprintId = default!;
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            async (_, workingDirectory, token, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                SprintWorkflowState state =
                    (await store!.LoadAsync(environmentRef!.ProjectRoot, sprintId, token))!;
                string attemptIdText = state.Nodes[ImplementationNodeId].CurrentAttemptId!;
                AttemptSnapshot attempt = state.Attempts[attemptIdText];
                await store.AppendAttemptStopRequestedAsync(
                    environmentRef.ProjectRoot, sprintId, new AttemptId(Guid.Parse(attemptIdText)),
                    attempt.Version, token);
                return ProviderRunResult.Success([], new ProviderTerminalResult(summary));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        environmentRef = environment;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        store = environment.Resolve<ISprintStore>();
        sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            // Polls for the saga's own last, unconditional step (StopConvergedAt) rather than the
            // node/sprint states it appends on the way there: those two land as separate, earlier
            // appends inside the same FinishStopAsync call, so polling for either individually is
            // racy against the remaining steps this test also wants to have already landed.
            await WaitForAttemptStopConvergedAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        // No partial change reached the integration branch: no commit, no handoff, no recorded node
        // result for the stopped attempt.
        Assert.Empty(worktrees.Commits);
        Assert.DoesNotContain(
            await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken),
            item => item.NodeId.Value == ImplementationNodeId);
        Assert.Empty(await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.NotEmpty(worktrees.RemovedPaths);

        SprintWorkflowState final = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, final.Nodes[ImplementationNodeId].State);
        Assert.Equal(SprintState.Paused, final.Sprint.State);
        AttemptSnapshot stoppedAttempt = Assert.Single(
            final.Attempts.Values, attempt => attempt.NodeId == ImplementationNodeId);
        Assert.Equal(AttemptState.Cancelled, stoppedAttempt.State);
        Assert.NotNull(stoppedAttempt.StopConvergedAt);
        // The stop path never consumed automatic-retry budget (ADR 0047): still exactly the one
        // attempt this test started.
        Assert.Equal(1, final.Nodes[ImplementationNodeId].AttemptCount);
    }

    // PR #101 review finding 1 (critical): the point-of-no-return re-check the test above exercises
    // originally copied the top-of-tick gate's own `StopConvergedAt is null` clause. That clause is
    // correct at the top-of-tick gate (it stops FinishStopAsync from re-firing forever once a stop
    // has already fully converged) but wrong here: a genuinely concurrent second converger --
    // StageTransitionCoordinator.StopAndFailRunningNodeAsync, a rewind's own step 1, running from a
    // different call path than this executor's tick -- can append BOTH StopRequestedAt AND
    // StopConvergedAt for this exact attempt (and drive the node itself to `Failed`) before this
    // executor's own re-check runs. Under the old clause that combination reads as "no stop detected"
    // (StopConvergedAt is not null), so the attempt would still commit and integrate into the sprint's
    // integration branch even though a rewind is actively tearing it down -- exactly the bug class the
    // original fix was supposed to close, one layer deeper. The provider delegate below simulates a
    // fully converged concurrent stop (every append `StopAndFailRunningNodeAsync` itself makes, not
    // just the request) landing before RunImplementationAttemptAsync's own re-check, deterministically.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStopThatHasAlreadyFullyConvergedConcurrentlyStillPreventsCommitAndIntegrate()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Added the feature module.";
        FakeWorktreeManager worktrees = new();
        ISprintStore? store = null;
        TestEnvironment? environmentRef = null;
        SprintId sprintId = default!;
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            async (_, workingDirectory, token, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                SprintWorkflowState state =
                    (await store!.LoadAsync(environmentRef!.ProjectRoot, sprintId, token))!;
                string attemptIdText = state.Nodes[ImplementationNodeId].CurrentAttemptId!;
                AttemptId attemptId = new(Guid.Parse(attemptIdText));
                AttemptSnapshot attempt = state.Attempts[attemptIdText];

                // Step 1 of StopAndFailRunningNodeAsync: request the stop.
                await store.AppendAttemptStopRequestedAsync(
                    environmentRef.ProjectRoot, sprintId, attemptId, attempt.Version, token);

                // Step 2: land the attempt on `cancelled` -- the same transition
                // StopAndFailRunningNodeAsync appends once the stop request itself lands.
                state = (await store.LoadAsync(environmentRef.ProjectRoot, sprintId, token))!;
                AttemptSnapshot stopping = state.Attempts[attemptIdText];
                await store.AppendTransitionAsync(
                    environmentRef.ProjectRoot, sprintId, AggregateKind.Attempt, attemptIdText, "AttemptChanged",
                    "workflow.attempt_stopped", WorkflowStateNames.ToSnakeCase(AttemptState.Cancelled),
                    stopping.Version, Guid.NewGuid(), token);

                // Step 3: land the node on `failed` -- the same transition
                // StopAndFailRunningNodeAsync appends for every downstream node it stops.
                state = (await store.LoadAsync(environmentRef.ProjectRoot, sprintId, token))!;
                NodeSnapshot runningNode = state.Nodes[ImplementationNodeId];
                await store.AppendTransitionAsync(
                    environmentRef.ProjectRoot, sprintId, AggregateKind.Node, ImplementationNodeId, "NodeChanged",
                    "workflow.node_rewind_interrupted", WorkflowStateNames.ToSnakeCase(NodeState.Failed),
                    runningNode.Version, Guid.NewGuid(), token);

                // Step 4: converge the stop -- the exact append the old `StopConvergedAt is null`
                // clause would treat as "this stop is already handled, nothing to see here".
                await store.AppendAttemptStopConvergedAsync(environmentRef.ProjectRoot, sprintId, attemptId, token);

                return ProviderRunResult.Success([], new ProviderTerminalResult(summary));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        environmentRef = environment;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        store = environment.Resolve<ISprintStore>();
        sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForAttemptStopConvergedAsync(store, environment, sprintId, cancellationToken);
            // The node was driven straight to `failed` by the simulated concurrent converger (never
            // rearmed to `ready` by this executor's own FinishStopAsync, since StopConvergedAt was
            // already set before this executor's own top-of-tick gate ever saw the attempt) -- poll
            // for that terminal state too so this assertion never races the background tick.
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Failed, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        // The bug: under the old `StopConvergedAt is null` clause, StopHasBeenRequestedAsync would
        // have reported "no stop" here (StopConvergedAt was already set by the simulated concurrent
        // converger above), so the executor would have committed and integrated the provider's change
        // into the sprint's integration branch despite the concurrent stop -- worktrees.Commits would
        // be non-empty and a discarded worktree path would come from a post-hoc integration failure,
        // not from this executor recognizing the stop up front. The fix (StopRequestedAt alone) must
        // discard instead, exactly like the request-only race the test above already covers.
        Assert.Empty(worktrees.Commits);
        Assert.DoesNotContain(
            await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken),
            item => item.NodeId.Value == ImplementationNodeId);
        Assert.Empty(await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
    }

    // PR #101 review finding 2: the point-of-no-return re-check above only guarded the upcoming
    // CommitAttemptAsync call -- CommitAttemptAsync writes only to the attempt's own worktree/branch
    // (discarded wholesale on a stop), but IntegrateAsync afterward is the actual publish to the
    // sprint's shared integration branch, and nothing re-checked the stop intent in between. This test
    // simulates a stop converging in exactly that window -- right after the real commit lands,
    // immediately before RunImplementationAttemptAsync's own second re-check -- proving that check
    // (added alongside this finding) catches it too, discarding the attempt instead of integrating it.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStopThatConvergesBetweenCommitAndIntegrateIsHonoredInsteadOfReachingIntegration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Added the feature module.";
        FakeWorktreeManager worktrees = new();
        ISprintStore? store = null;
        TestEnvironment? environmentRef = null;
        SprintId sprintId = default!;
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(ProviderRunResult.Success([], new ProviderTerminalResult(summary)));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        environmentRef = environment;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        store = environment.Resolve<ISprintStore>();
        sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

        worktrees.AfterCommitAll = async token =>
        {
            // Fires exactly once, after the real commit already landed in worktrees.Commits (proving
            // the commit itself is not what this finding guards) but before this method's own
            // production code re-checks the stop intent -- the precise window PR #101 review finding 2
            // found unguarded.
            worktrees.AfterCommitAll = null;
            SprintWorkflowState state = (await store!.LoadAsync(environmentRef!.ProjectRoot, sprintId, token))!;
            string attemptIdText = state.Nodes[ImplementationNodeId].CurrentAttemptId!;
            AttemptId attemptId = new(Guid.Parse(attemptIdText));
            AttemptSnapshot attempt = state.Attempts[attemptIdText];
            await store.AppendAttemptStopRequestedAsync(
                environmentRef.ProjectRoot, sprintId, attemptId, attempt.Version, token);
        };

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            // Not WaitForNodeStateAsync(..., Ready, ...): the node already starts at `Ready` before
            // the executor's first tick even runs, so polling for that state alone could trivially
            // match before the attempt (and this test's own race) ever happened. StopConvergedAt is
            // the saga's own unambiguous last step, exactly like the request-only race test above.
            await WaitForAttemptStopConvergedAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        // The commit itself did land (proves the race window really was past CommitAttemptAsync), but
        // the second re-check must stop the actual publish: no handoff, no recorded result for the
        // stopped attempt, and the attempt worktree/branch is discarded rather than integrated.
        Assert.Single(worktrees.Commits);
        Assert.DoesNotContain(
            await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken),
            item => item.NodeId.Value == ImplementationNodeId);
        Assert.Empty(await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.NotEmpty(worktrees.RemovedPaths);
    }

    // ADR 0059's ordering property, end to end through the real executor: the diff is READ before
    // IntegrateAsync (which discards the very worktree the read resolves against) and RECORDED only
    // after that integrate succeeded. Asserted through the durable journal rather than a spy, so it
    // also proves the flat summary the timeline renders is derived from the same payload.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASuccessfullyIntegratedAttemptRecordsExactlyOneDiffSummaryMatchingTheReadTakenBeforeIntegration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new()
        {
            DiffStat = new(
                3,
                12,
                4,
                [
                    new DiffFileStat("src/Forge.Runtime/A.cs", 10, 4, DiffChangeKinds.Modified),
                    new DiffFileStat("docs/b.md", 2, 0, DiffChangeKinds.Added),
                ],
                1),
        };
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(
                    ProviderRunResult.Success([], new ProviderTerminalResult("Added the feature module.")));
            });
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
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
            await WaitForAttemptDiffRecordedAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        WorkflowEvent recorded = Assert.Single(
            await AttemptDiffEventsAsync(store, environment, sprintId, cancellationToken));
        // Recorded against the attempt that actually integrated, not some other aggregate.
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot attempt = Assert.Single(
            state.Attempts.Values, item => item.NodeId == ImplementationNodeId);
        Assert.Equal(attempt.Id.Value.ToString("D"), recorded.Aggregate.Id);
        Assert.Equal("3", recorded.Arguments[WorkflowEvent.DiffFilesChangedArgument]);
        Assert.Equal("12", recorded.Arguments[WorkflowEvent.DiffInsertionsArgument]);
        Assert.Equal("4", recorded.Arguments[WorkflowEvent.DiffDeletionsArgument]);
        DiffPayload payload = recorded.Payload!.Diff!;
        Assert.Equal(worktrees.DiffStat.Files, payload.Files);
        Assert.Equal(1, payload.ElidedFiles);
    }

    // The other half of the same ordering property: a diff summary for work that never reached the
    // integration branch would be a durable claim about a change the sprint does not have. A stop
    // landing right as the provider succeeds discards the attempt before IntegrateAsync, so nothing
    // may be recorded -- reusing the exact race the stop-path test above already sets up.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStoppedAttemptRecordsNoDiffSummaryBecauseItsWorkNeverReachedTheIntegrationBranch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        ISprintStore? store = null;
        TestEnvironment? environmentRef = null;
        SprintId sprintId = default!;
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            async (_, workingDirectory, token, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                SprintWorkflowState state =
                    (await store!.LoadAsync(environmentRef!.ProjectRoot, sprintId, token))!;
                string attemptIdText = state.Nodes[ImplementationNodeId].CurrentAttemptId!;
                AttemptSnapshot attempt = state.Attempts[attemptIdText];
                await store.AppendAttemptStopRequestedAsync(
                    environmentRef.ProjectRoot, sprintId, new AttemptId(Guid.Parse(attemptIdText)),
                    attempt.Version, token);
                return ProviderRunResult.Success([], new ProviderTerminalResult("Added the feature module."));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        environmentRef = environment;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        store = environment.Resolve<ISprintStore>();
        sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

        ImplementationExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForAttemptStopConvergedAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Empty(worktrees.Commits);
        Assert.Empty(await AttemptDiffEventsAsync(store, environment, sprintId, cancellationToken));
    }

    // Same rule for the failure path: the executor discards the attempt worktree and never reaches
    // IntegrateAsync at all, so no diff summary may be recorded for any of its retries either.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AFailedAttemptRecordsNoDiffSummaryForAnyOfItsRetries()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            // Never marks the worktree dirty -- the provider ran but left nothing to commit, so the
            // attempt fails with ImplementationNoChanges long before any integrate.
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

        Assert.Empty(await AttemptDiffEventsAsync(store, environment, sprintId, cancellationToken));
    }

    // PR #116 review finding 1 (regression proof): the diff-stat read is audit-only and runs BEFORE
    // IntegrateAsync, so whichever way it fails -- a returned failure result or a raw throw from the
    // `git` process itself (Win32Exception, or an inner deadline's OperationCanceledException) -- the
    // attempt's already-made commit must still reach the sprint's integration branch and complete
    // normally. Before the fix the `throws` case propagated out of RunImplementationAttemptAsync,
    // was swallowed by this service's own per-sprint boundary, and left the node stuck `running`
    // with its commit never integrated at all.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task ADiffReadThatFailsNeverPreventsTheAttemptFromIntegratingAndSucceeding(bool throws)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Added the feature module.";
        FakeWorktreeManager worktrees = new();
        if (throws)
        {
            worktrees.DiffStatException = new Win32Exception("git could not be started.");
        }
        else
        {
            worktrees.FailNextDiff = true;
        }

        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(ProviderRunResult.Success([], new ProviderTerminalResult(summary)));
            });
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
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
            await WaitForHandoffAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(
            await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Single(worktrees.Commits);
        // Only the audit record is lost -- never the work itself.
        Assert.Empty(await AttemptDiffEventsAsync(store, environment, sprintId, cancellationToken));
    }

    // PR #116 review finding 2 (regression proof): the diff record is appended AFTER a successful
    // integrate but BEFORE CompleteAttemptAsync, so a throw escaping it strands an attempt whose
    // change is already on the integration branch in `running` forever. OperationCanceledException
    // is the realistic shape (FileSprintEventLog's own per-sprint gate raises it) and the one the
    // original catch filter -- IOException/UnauthorizedAccessException/InvalidDataException -- did
    // not cover.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADiffRecordThatThrowsNeverStrandsAnAlreadyIntegratedAttemptInRunning()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Added the feature module.";
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, workingDirectory, _, _) =>
            {
                worktrees.Dirty.Add(workingDirectory);
                return Task.FromResult(ProviderRunResult.Success([], new ProviderTerminalResult(summary)));
            });
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        (SprintOrchestrator orchestrator, SprintScheduler scheduler, FlakySprintStore store) =
            environment.ResolveWithFlakyStore();
        store.DiffRecordFailure = new OperationCanceledException("The journal gate was cancelled.");
        SprintId sprintId = await CreateSprintReadyForImplementationAsync(
            environment, orchestrator, scheduler, store, "The plan.", cancellationToken);

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

        NodeResult result = Assert.Single(
            await ImplementationResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Single(worktrees.Commits);
        Assert.Empty(await AttemptDiffEventsAsync(store, environment, sprintId, cancellationToken));
    }

    private static async Task<IReadOnlyList<WorkflowEvent>> AttemptDiffEventsAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken) =>
        [.. (await store.GetEventsAsync(environment.ProjectRoot, sprintId, cancellationToken))
            .Where(item => item.Type == WorkflowEvent.AttemptDiffRecordedType)];

    /// <summary>The diff record is appended before the node reaches `succeeded`, but the append is
    /// fail-open: if it fails, the node still reaches `succeeded` with no diff event at all. Node
    /// state therefore never guarantees the diff event's presence in either direction, so tests that
    /// expect one must poll for it directly rather than inferring it from node state.</summary>
    private static async Task WaitForAttemptDiffRecordedAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if ((await AttemptDiffEventsAsync(store, environment, sprintId, cancellationToken)).Count > 0)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D}'s implementation attempt never recorded a diff summary.");
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
            environment.Resolve<ActiveOperationRegistry>(),
            environment.Resolve<StopOperationCoordinator>(),
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

    private static async Task WaitForAttemptStopConvergedAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            if (state.Attempts.Values.Any(
                attempt => attempt.NodeId == ImplementationNodeId && attempt.StopConvergedAt is not null))
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D}'s implementation attempt never converged its stop.");
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
