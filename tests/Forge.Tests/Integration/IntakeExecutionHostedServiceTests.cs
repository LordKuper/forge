using System.Diagnostics;
using System.Text;
using Forge.Application;
using Forge.Compiler;
using Forge.Domain;
using Forge.Host;
using Forge.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.IntegrationTests;

/// <summary>
/// <see cref="IntakeExecutionHostedService"/> is the first code in this repository that mutates
/// durable workflow state with no human command behind it (ADR 0028), so these tests exercise the
/// real <see cref="ISprintStore"/> and the real <see cref="SprintScheduler"/> end to end rather
/// than a stubbed executor — the same shape <see cref="ResumeSchedulerHostedServiceTests"/> uses.
/// </summary>
public sealed class IntakeExecutionHostedServiceTests
{
    private const string IntakeNodeId = ImplementationCriticalGraphBuilder.IntakeNodeId;

    // The whole point of the slice: a sprint's intake node must reach `succeeded` on its own, with a
    // durable NodeResult recording the REAL context-manifest digest for that sprint (ADR 0012), not
    // a placeholder. The expectation is recomputed from the sprint's own frozen identity and the
    // project's own `.forge/` documents, so a digest that stopped depending on either would fail.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AReadyIntakeNodeIsExecutedToSucceededWithTheSprintsRealContextManifestDigest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        WriteDocument(environment, "rules", "testing.md", Frontmatter("testing", "Testing") + "Implement first.");
        WriteDocument(
            environment, "knowledge", "adr.md", Frontmatter("adr", "An ADR", "accepted") + "Accepted knowledge.");

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        SprintWorkflowState before =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, before.Nodes[IntakeNodeId].State);

        IntakeExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        Assert.Equal(IntakeNodeId, result.NodeId.Value);
        Assert.Empty(result.Diagnostics);

        SprintDefinition definition =
            (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        ForgeDocumentSet documents =
            await new ForgeDocumentCompiler().ParseAsync(environment.ProjectRoot, cancellationToken);
        ContextManifest expected = ContextManifestCompiler.Compile(
            sprintId.Value, definition.BaseCommit, definition.Workflow, definition.WorkflowVersion, documents,
            IntakeExecutionHostedService.DefaultTokenBudget);
        Assert.Equal(expected.ManifestDigest, result.InputDigest);
        Assert.Equal(
            [.. expected.Layers.Rules.Select(item => item.Digest), .. expected.Layers.Knowledge.Select(item => item.Digest)],
            result.Outputs);
        // Both documents really were admitted -- otherwise "the digest matches" would also hold for
        // an executor that silently parsed nothing at all.
        Assert.Equal(2, result.Outputs.Count);
    }

    // ADR 0028: a malformed `.forge/` document degrades intake's admitted context, it never fails the
    // node. Failing would burn all three automatic attempts and block the sprint on something no
    // retry can ever fix, and would contradict IntegrationGenerationService's own precedent for the
    // identical ForgeDocumentSet.Errors input.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AMalformedForgeDocumentIsRecordedAsADiagnosticWithoutFailingIntake()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        WriteDocument(environment, "rules", "broken.md", "No frontmatter at all.");
        WriteDocument(environment, "rules", "ok.md", Frontmatter("ok-rule", "OK rule") + "Fine.");

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);

        IntakeExecutionHostedService service = NewService(environment, store, scheduler);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        NodeResult result = Assert.Single(
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(NodeOutcome.Succeeded, result.State);
        NodeDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ForgeDocumentDiagnosticCodes.FrontmatterInvalid, diagnostic.Code);
        Assert.Equal("context", diagnostic.Category);
        Assert.Equal($"diagnostic.{ForgeDocumentDiagnosticCodes.FrontmatterInvalid}", diagnostic.MessageKey);
        Assert.Equal("rules/broken.md", Assert.Contains("relative_path", diagnostic.Arguments));
        // The one document that did parse is still admitted: the malformed sibling degrades the
        // manifest, it does not empty it.
        Assert.Single(result.Outputs);
    }

    // The crash-resumability story. A tick that dies between StartAttemptAsync and
    // CompleteAttemptAsync leaves the node `running` with a durable CurrentAttemptId and no result;
    // nothing else in this codebase can move a `running` node onward, so a restarted Host must pick
    // up THAT attempt rather than mint a second one (or strand the node forever).
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnIntakeAttemptInterruptedBeforeCompletionIsResumedWithoutASecondAttempt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        WriteDocument(environment, "rules", "testing.md", Frontmatter("testing", "Testing") + "Implement first.");

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);

        // Exactly the durable state a tick killed after its start call leaves behind.
        SprintWorkflowState ready = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult interrupted = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, IntakeNodeId, ready.Nodes[IntakeNodeId].Version, cancellationToken);
        Assert.True(interrupted.Succeeded);
        AttemptId interruptedAttemptId = interrupted.AttemptId!;
        SprintWorkflowState crashed = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Running, crashed.Nodes[IntakeNodeId].State);
        Assert.Empty(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));

        IntakeExecutionHostedService resuming = NewService(environment, store, scheduler);
        await resuming.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, sprintId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await resuming.StopAsync(cancellationToken);
        }

        NodeResult resumed = Assert.Single(
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(interruptedAttemptId, resumed.AttemptId);
        SprintWorkflowState settled = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Single(settled.Attempts);

        // A restarted Host must not re-execute an already-settled node. A second sprint whose own
        // intake completes under the restarted service is a deterministic proof that a full tick
        // swept BOTH sprints -- no fixed sleep, which under a loaded runner proves nothing.
        SprintId second = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        IntakeExecutionHostedService restarted = NewService(environment, store, scheduler);
        await restarted.StartAsync(cancellationToken);
        try
        {
            await WaitForNodeStateAsync(store, environment, second, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await restarted.StopAsync(cancellationToken);
        }

        Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        SprintWorkflowState afterRestart =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Single(afterRestart.Attempts);
        Assert.Equal(NodeState.Succeeded, afterRestart.Nodes[IntakeNodeId].State);
        Assert.Null(restarted.ExecuteTask!.Exception);
    }

    // Matches ResumeSchedulerHostedService's own per-sprint isolation precedent: one sprint whose
    // durable state cannot be read must never stop every other sprint's intake from running, and
    // must never fault this BackgroundService's ExecuteTask (nothing else would ever observe it).
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASprintWithACorruptDefinitionDoesNotStopAnotherSprintsIntakeFromCompleting()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();

        SprintId corruptId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, corruptId), "definition.json");
        await File.WriteAllTextAsync(definitionPath, "{ not valid json", cancellationToken);
        Assert.Contains(corruptId, await store.ListAsync(environment.ProjectRoot, cancellationToken));

        // The corrupt sprint is deliberately the ONLY one present when the service starts, and the
        // healthy sprint is created only after the corrupt one is proven to have been swept and
        // isolated. Seeding both up front would make this test depend on ListAsync's (GUID-ordered,
        // effectively random) iteration order: a healthy sprint that happens to sort first completes
        // even when the corrupt one goes on to fault the loop permanently.
        RecordingLogger logger = new();
        IntakeExecutionHostedService service = new(
            new IntakeExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store, scheduler, logger);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForLogAsync(logger, "IntakeExecutionSprintFailed", cancellationToken);
            SprintId healthyId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
            await WaitForNodeStateAsync(store, environment, healthyId, NodeState.Succeeded, cancellationToken);
            Assert.Null(service.ExecuteTask!.Exception);
            Assert.Single(await store.GetNodeResultsAsync(environment.ProjectRoot, healthyId, cancellationToken));
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }
    }

    // AdvanceGraphAsync promotes a dependency-free intake node to `ready` regardless of the sprint's
    // own state, so a draft sprint has a `ready` intake node the moment it is created. Executing it
    // would drive an attempt on a sprint the operator never ran -- and the service must skip it
    // SILENTLY: StartAttemptAsync would refuse it anyway with `sprint_not_running`, but letting the
    // call get that far would log a rejection every interval, for every draft sprint, forever.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADraftSprintsReadyIntakeNodeIsNeitherExecutedNorRepeatedlyReportedAsRejected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();

        CreateSprintResult draft = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        Assert.True(draft.Succeeded);
        SprintId draftId = draft.SprintId!;
        SprintWorkflowState draftState =
            (await store.LoadAsync(environment.ProjectRoot, draftId, cancellationToken))!;
        Assert.Equal(SprintState.Draft, draftState.Sprint.State);
        Assert.Equal(NodeState.Ready, draftState.Nodes[IntakeNodeId].State);

        // The draft sprint is the only one present when the service starts, so the very first tick
        // is guaranteed to reach it — nothing has completed yet, so nothing can cut that tick short.
        // Seeding a running sprint first instead would leave this test at the mercy of ListAsync's
        // (GUID-ordered, effectively random) iteration order: with the running sprint swept first,
        // StopAsync can cancel the tick before the draft one is ever visited, so the assertion below
        // would pass without the guard it exists to protect.
        RecordingLogger logger = new();
        IntakeExecutionHostedService service = new(
            new IntakeExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store, scheduler, logger);
        await service.StartAsync(cancellationToken);
        try
        {
            // A running sprint added afterwards proves ticks really are firing, so "logged nothing"
            // means the draft sprint was skipped silently, not that the loop never ran.
            SprintId runningId = await CreateRunningSprintAsync(environment, orchestrator, store, cancellationToken);
            await WaitForNodeStateAsync(store, environment, runningId, NodeState.Succeeded, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        SprintWorkflowState untouched =
            (await store.LoadAsync(environment.ProjectRoot, draftId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, untouched.Nodes[IntakeNodeId].State);
        Assert.Empty(untouched.Attempts);
        Assert.Empty(await store.GetNodeResultsAsync(environment.ProjectRoot, draftId, cancellationToken));
        // Every message this service can emit reports something wrong; a healthy project with a
        // draft sprint must produce none of them, however many times it is swept.
        Assert.Equal(string.Empty, string.Join(" || ", logger.Snapshot()));
    }

    private static IntakeExecutionHostedService NewService(
        TestEnvironment environment, ISprintStore store, SprintScheduler scheduler) =>
        new(
            new IntakeExecutionOptions(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            store,
            scheduler,
            NullLogger<IntakeExecutionHostedService>.Instance);

    private static async Task<SprintId> CreateRunningSprintAsync(
        TestEnvironment environment,
        SprintOrchestrator orchestrator,
        ISprintStore store,
        CancellationToken cancellationToken)
    {
        // No explicit Graph: the built-in `implementation-critical` graph is the only one that has an
        // intake-role node at all, and it is what every managed project's sprint really uses.
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

    /// <summary>A generous wall-clock deadline rather than an attempt count, matching the lesson
    /// <see cref="NotificationDeliveryHostedServiceTests"/>'s own polling helpers record: a fixed
    /// attempt budget is load-sensitive, and on a starved runner a timeout becomes indistinguishable
    /// from a genuine "it never happened" defect. Costs nothing on the happy path — this returns as
    /// soon as the node reaches the expected state.</summary>
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
            observed = state.Nodes[IntakeNodeId].State;
            if (observed == expected)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"The intake node of sprint {sprintId.Value:D} stayed '{observed}' instead of '{expected}'.");
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

    private static void WriteDocument(
        TestEnvironment environment, string directoryName, string fileName, string content)
    {
        string directory = Path.Combine(
            ProjectRootResolver.ForgeDirectory(environment.ProjectRoot), directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    /// <summary>Captures what the service logged. Every message
    /// <see cref="IntakeExecutionHostedService"/> defines reports a rejection or a failure, so
    /// "logged nothing" is a meaningful assertion about a healthy sweep.</summary>
    private sealed class RecordingLogger : ILogger<IntakeExecutionHostedService>
    {
        private readonly List<string> entries = [];

        /// <summary>Written from the service's own background loop and read from the test thread, so
        /// every access is taken under the same lock.</summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (entries)
            {
                return [.. entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
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

    private static string Frontmatter(string id, string title, string? status = null)
    {
        StringBuilder builder = new();
        builder.Append("---\nschema_version: \"1.0.0\"\nid: ").Append(id)
            .Append("\ntitle: ").Append(title)
            .Append("\nscope: project\n");
        if (status is not null)
        {
            builder.Append("status: ").Append(status).Append('\n');
        }

        builder.Append("---\n");
        return builder.ToString();
    }
}
