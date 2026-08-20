using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
/// <see cref="PlanningExecutionHostedService"/> is Stage 11's second node executor and the first
/// production caller of <see cref="ILlmProvider.RunAsync"/>. These tests decouple the executor's
/// own orchestration from real `git.exe` (<see cref="FakeWorktreeManager"/> — the real thing is
/// already exercised by <c>GitIsolationTests</c>) and from a real provider process
/// (<see cref="FakeRunnableLlmProvider"/>), the same "prove this service's own logic, not its
/// already-tested dependencies" boundary <c>IntakeExecutionHostedServiceTests</c> draws.
/// </summary>
public sealed class PlanningExecutionHostedServiceTests
{
    private const string IntakeNodeId = ImplementationCriticalGraphBuilder.IntakeNodeId;
    private const string PlanningNodeId = ImplementationCriticalGraphBuilder.PlanningNodeId;
    private const string ImplementationNodeId = ImplementationCriticalGraphBuilder.ImplementationNodeId;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AReadyPlanningNodeSucceedsAndRecordsAHandoffFromTheProvidersTerminalSummary()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string summary = "Plan: touch module X, then wire it into Y.";
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(
                ProviderRunResult.Success([], new ProviderTerminalResult(summary))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);
        SprintDefinition definition = (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;

        PlanningExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
            // The node's own state flips to `Succeeded` (via CompleteAttemptAsync) strictly before
            // ExecutePlanningAsync goes on to call RecordHandoffAsync -- both real, in production,
            // not just here -- so a poll loop that stops at node state alone can observe a real,
            // if narrow, window with no handoff yet. Waited out explicitly rather than assumed away.
            await WaitForHandoffAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(await PlanningResultsAsync(store, environment, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Equal([Digest(summary)], result.Outputs);
        ForgeDocumentSet documents = await new ForgeDocumentCompiler().ParseAsync(environment.ProjectRoot, cancellationToken);
        ContextManifest expectedManifest = ContextManifestCompiler.Compile(
            sprintId.Value, definition.BaseCommit, definition.Workflow, definition.WorkflowVersion, documents,
            TokenBudgetResolver.DefaultTokenBudget);
        Assert.Equal(expectedManifest.ManifestDigest, result.InputDigest);

        Handoff handoff = Assert.Single(
            await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(PlanningNodeId, handoff.NodeId.Value);
        Assert.Equal(definition.BaseCommit, handoff.BaseSha);
        Assert.Equal(summary, handoff.Summary);
        Assert.Empty(handoff.Decisions);
        Assert.Empty(handoff.OpenRisks);
        Assert.Equal([ImplementationNodeId], handoff.NextNodeIds);

        // The provider ran inside an isolated attempt worktree that no longer exists once the
        // node settled -- planning makes no commit for anything to integrate, so the attempt
        // worktree is discarded rather than left around. Two worktrees are created in total (the
        // shared per-sprint integration worktree, plus this one attempt worktree); only the latter
        // is ever removed by this executor.
        Assert.Single(provider.Calls);
        Assert.Equal(2, worktrees.CreatedPaths.Count);
        string attemptPath = Assert.Single(worktrees.RemovedPaths);
        Assert.Contains(attemptPath, provider.Calls[0].WorkingDirectory);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task APromptInstructsThePlannerToMakeNoFileChangesAndCarriesAdmittedRuleContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(
                ProviderRunResult.Success([], new ProviderTerminalResult("Plan text."))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        WriteDocument(environment, "rules", "testing.md", Frontmatter("testing", "Testing") + "Write tests first.");

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        PlanningExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        string prompt = Assert.Single(provider.Calls).Prompt;
        Assert.Contains("Do not edit, create, or delete any file", prompt, StringComparison.Ordinal);
        Assert.Contains("Write tests first.", prompt, StringComparison.Ordinal);
        Assert.Contains("rules/testing.md", prompt, StringComparison.Ordinal);
    }

    // ADR 0006's durable outcome distinction: an ordinary provider failure is recorded and
    // automatically retried (SprintScheduler.MaxAutomaticRetries), not silently swallowed or
    // conflated with a timeout/cancellation.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AProviderFailureIsRecordedWithTheMappedDiagnosticAndAutomaticallyRetried()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(
                ProviderRunResult.Failed(ProviderFailureKind.Transient, "boom")));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        PlanningExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForTerminalFailureAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        IReadOnlyList<NodeResult> results = await PlanningResultsAsync(store, environment, sprintId, cancellationToken);
        // MaxAutomaticRetries = 2 -> three total attempts before the node stays terminally failed.
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(NodeOutcome.Failed, result.State));
        NodeDiagnostic diagnostic = Assert.Single(results[0].Diagnostics);
        Assert.Equal(ProviderDiagnosticCodes.RunTransientFailure, diagnostic.Code);
        Assert.Equal("provider", diagnostic.Category);
        Assert.Equal("boom", Assert.Contains("detail", diagnostic.Arguments));
        Assert.Empty(await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        // One shared integration worktree (created once, then idempotently reused every retry) plus
        // one fresh attempt worktree per attempt -- every one of the three failed attempts got its
        // own isolated worktree, and every one was discarded (clean replay, ADR 0004), never a
        // failed attempt's leftovers reused by its retry. Only the three attempt worktrees are ever
        // removed -- the shared integration worktree is never discarded by this executor.
        Assert.Equal(4, worktrees.CreatedPaths.Count);
        Assert.Equal(4, worktrees.CreatedPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, worktrees.RemovedPaths.Count);
    }

    // The provider reporting a schema-valid success with no usable text (ADR 0016: neither vendor
    // guarantees non-empty terminal-result text) must never produce a Handoff with an empty summary
    // -- handoff.schema.json requires minLength: 1 -- so this is a recorded failure, distinct from
    // every other ProviderFailureKind because the provider itself never reported failing at all.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnEmptyTerminalSummaryIsRecordedAsAFailureWithoutRecordingAHandoff()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new();
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => Task.FromResult(
                ProviderRunResult.Success([], new ProviderTerminalResult("   "))));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        PlanningExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForTerminalFailureAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        IReadOnlyList<NodeResult> results = await PlanningResultsAsync(store, environment, sprintId, cancellationToken);
        NodeDiagnostic diagnostic = Assert.Single(results[0].Diagnostics);
        Assert.Equal(ProviderDiagnosticCodes.EmptyTerminalSummary, diagnostic.Code);
        Assert.Empty(await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // A worktree could not even be created (a real orchestration failure this executor -- unlike
    // intake -- can actually reach). The provider must never be invoked against a working
    // directory that does not exist, and the attempt must still settle to a terminal recording
    // instead of leaving the node stuck `running` with nothing to complete it.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AWorktreeCreationFailureFailsTheAttemptWithoutInvokingTheProvider()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeWorktreeManager worktrees = new() { FailNextCreate = true };
        FakeRunnableLlmProvider provider = new(
            new ProviderId("fake"),
            (_, _, _, _) => throw new InvalidOperationException("The provider must not run."));
        using TestEnvironment environment = new(llmProviders: [provider], worktrees: worktrees);
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateSprintReadyForPlanningAsync(environment, orchestrator, scheduler, store, cancellationToken);

        PlanningExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForTerminalFailureAsync(store, environment, sprintId, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Empty(provider.Calls);
        IReadOnlyList<NodeResult> results = await PlanningResultsAsync(store, environment, sprintId, cancellationToken);
        // A persistently broken worktree manager fails every automatic retry too (MaxAutomaticRetries).
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(NodeOutcome.Failed, result.State));
        NodeDiagnostic diagnostic = Assert.Single(results[0].Diagnostics);
        Assert.Equal(DiagnosticCodes.WorktreeUnavailable, diagnostic.Code);
        Assert.Equal("git", diagnostic.Category);
    }

    private static async Task<IReadOnlyList<NodeResult>> PlanningResultsAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken) =>
        [.. (await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken))
            .Where(result => result.NodeId.Value == PlanningNodeId)];

    private static PlanningExecutionHostedService NewService(
        TestEnvironment environment, ISprintStore store, SprintScheduler scheduler, TimeSpan? interval = null) =>
        new(
            new PlanningExecutionOptions(environment.ProjectRoot, interval ?? TimeSpan.FromMilliseconds(50)),
            store,
            scheduler,
            environment.Resolve<SprintGitIsolation>(),
            environment.Resolve<ProviderCatalog>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment,
            environment.Application,
            NullLogger<PlanningExecutionHostedService>.Instance);

    private static async Task<SprintId> CreateSprintReadyForPlanningAsync(
        TestEnvironment environment,
        SprintOrchestrator orchestrator,
        SprintScheduler scheduler,
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

        // Drives intake to Succeeded directly through the scheduler rather than running a real
        // IntakeExecutionHostedService: intake's own executor behavior is IntakeExecutionHostedServiceTests'
        // job, and this file's tests are about the planning node specifically, which the built-in
        // graph makes depend on intake.
        SprintWorkflowState running = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult intakeStarted = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, IntakeNodeId, running.Nodes[IntakeNodeId].Version, cancellationToken);
        Assert.True(intakeStarted.Succeeded);
        CompleteAttemptResult intakeCompleted = await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, IntakeNodeId, intakeStarted.AttemptId!, true,
            "sha256:" + new string('0', 64), [], [], cancellationToken);
        Assert.True(intakeCompleted.Succeeded);
        return sprintId;
    }

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
            observed = state.Nodes[PlanningNodeId].State;
            if (observed == expected)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"The planning node of sprint {sprintId.Value:D} stayed '{observed}' instead of '{expected}'.");
    }

    /// <summary>`node_failed` and the bounded auto-retry's own `node_retrying` (SprintScheduler.
    /// CompleteAttemptAsync) are two separate durable appends, not one atomic compound event -- a
    /// concurrent reader can genuinely observe the node sitting at `Failed` in the narrow window
    /// between them, before the retry-to-`Ready` transition lands. Plain <see cref="WaitForNodeStateAsync"/>
    /// polling for `Failed` is therefore ambiguous between that transient window and the real
    /// terminal failure (attempt budget exhausted); this additionally requires the attempt count
    /// SprintScheduler.MaxAutomaticRetries + 1 proves no further retry is coming.</summary>
    private static async Task WaitForTerminalFailureAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        NodeSnapshot? observed = null;
        while (stopwatch.Elapsed < PollTimeout)
        {
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            observed = state.Nodes[PlanningNodeId];
            if (observed.State == NodeState.Failed &&
                observed.AttemptCount >= SprintScheduler.MaxAutomaticRetries + 1)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail(
            $"The planning node of sprint {sprintId.Value:D} never reached terminal failure " +
            $"(last observed state={observed?.State}, attemptCount={observed?.AttemptCount}).");
    }

    private static async Task WaitForHandoffAsync(
        ISprintStore store, TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if ((await store.GetHandoffsAsync(environment.ProjectRoot, sprintId, cancellationToken)).Count > 0)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"Sprint {sprintId.Value:D} never recorded a planning handoff.");
    }

    private static void WriteDocument(
        TestEnvironment environment, string directoryName, string fileName, string content)
    {
        string directory = Path.Combine(
            ProjectRootResolver.ForgeDirectory(environment.ProjectRoot), directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    private static string Frontmatter(string id, string title)
    {
        StringBuilder builder = new();
        builder.Append("---\nschema_version: \"1.0.0\"\nid: ").Append(id)
            .Append("\ntitle: ").Append(title)
            .Append("\nscope: project\n---\n");
        return builder.ToString();
    }

    private static string Digest(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";
}
