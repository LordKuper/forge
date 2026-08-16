using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host;
using Forge.Host.Client;
using Forge.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.IntegrationTests;

public sealed class ResumeSchedulerHostedServiceTests
{
    // A node whose dependency just settled outside the normal scheduler flow must not stay stuck
    // forever waiting for some other call to happen to notice — this proves the background timer
    // itself, not SprintScheduler.AdvanceGraphAsync's own correctness (already covered by
    // SprintSchedulerTests).
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheServicePromotesANodeLeftStuckAfterItsDependencySucceededOutsideTheScheduler()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();

        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken);
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

        // "a" has no dependency, so entering `running` already promoted it to `ready` via
        // RunSprintAsync's own AdvanceGraphAsync call. Settle it directly through the store —
        // bypassing SprintScheduler entirely — so nothing else ever re-derives readiness for "b".
        SprintWorkflowState running = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        NodeSnapshot nodeA = running.Nodes["a"];
        AppendOutcome nodeARunning = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Node, nodeA.Id.Value, "NodeChanged",
            "workflow.node_running", WorkflowStateNames.ToSnakeCase(NodeState.Running), nodeA.Version,
            Guid.NewGuid(), cancellationToken);
        Assert.True(nodeARunning.Succeeded);
        AppendOutcome nodeASucceeded = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Node, nodeA.Id.Value, "NodeChanged",
            "workflow.node_succeeded", WorkflowStateNames.ToSnakeCase(NodeState.Succeeded),
            nodeARunning.State!.Nodes["a"].Version, Guid.NewGuid(), cancellationToken);
        Assert.True(nodeASucceeded.Succeeded);
        Assert.Equal(NodeState.Pending, nodeASucceeded.State!.Nodes["b"].State);

        ResumeSchedulerOptions options = new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50));
        ResumeSchedulerHostedService service = new(
            options, store, scheduler, NullLogger<ResumeSchedulerHostedService>.Instance);
        await service.StartAsync(cancellationToken);
        try
        {
            NodeState observed = NodeState.Pending;
            for (int attempt = 0; attempt < 40 && observed == NodeState.Pending; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                SprintWorkflowState polled =
                    (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
                observed = polled.Nodes["b"].State;
            }

            Assert.Equal(NodeState.Ready, observed);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }
    }

    // Before this fix, WorkflowFold.Apply's int.Parse on a node's attempt_number argument could
    // throw OverflowException for an out-of-int32-range value -- an exception type distinct from
    // (and missed alongside) the JsonException/FormatException cases already fixed. That raw
    // OverflowException escaped LoadAsync and TickAsync's catch filter, permanently faulting the
    // whole BackgroundService's ExecuteTask.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASprintWithAnOutOfRangeAttemptNumberDoesNotStopOtherSprintsFromBeingReDerived()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initializedForOverflow = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initializedForOverflow.Succeeded);

        SprintOrchestrator overflowOrchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler overflowScheduler = environment.Resolve<SprintScheduler>();
        ISprintStore overflowStore = environment.Resolve<ISprintStore>();

        CreateSprintResult overflowSprint = await overflowOrchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken);
        Assert.True(overflowSprint.Succeeded);
        SprintWorkflowState overflowState = (await overflowStore.LoadAsync(
            environment.ProjectRoot, overflowSprint.SprintId!, cancellationToken))!;
        NodeSnapshot overflowNodeA = overflowState.Nodes["a"];
        AppendOutcome overflowAppend = await overflowStore.AppendTransitionAsync(
            environment.ProjectRoot, overflowSprint.SprintId!, AggregateKind.Node, overflowNodeA.Id.Value,
            "NodeChanged", "workflow.node_running", WorkflowStateNames.ToSnakeCase(NodeState.Running),
            overflowNodeA.Version, Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.AttemptNumberArgument] = "1" });
        Assert.True(overflowAppend.Succeeded);

        // AppendTransitionAsync re-folds and would itself reject an out-of-range attempt_number at
        // write time (WorkflowLogCorrupted) -- this must land as *durable* corruption discovered only
        // on a later read, matching a real journal damaged after the fact, so it is written directly.
        string overflowEventsPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, overflowSprint.SprintId!), "events.jsonl");
        string overflowJournalText = await File.ReadAllTextAsync(overflowEventsPath, cancellationToken);
        string corruptedJournalText = overflowJournalText.Replace(
            "\"attempt_number\":\"1\"", "\"attempt_number\":\"99999999999999999999\"", StringComparison.Ordinal);
        Assert.NotEqual(overflowJournalText, corruptedJournalText);
        await File.WriteAllTextAsync(overflowEventsPath, corruptedJournalText, cancellationToken);

        CreateSprintResult createdForOverflow = await overflowOrchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken);
        Assert.True(createdForOverflow.Succeeded);
        SprintId overflowSprintId = createdForOverflow.SprintId!;

        SprintWorkflowState overflowDraft =
            (await overflowStore.LoadAsync(environment.ProjectRoot, overflowSprintId, cancellationToken))!;
        SprintTransitionResult overflowToReady = await overflowOrchestrator.RunSprintAsync(
            new(environment.ProjectRoot, overflowSprintId, overflowDraft.Sprint.Version,
                SprintOrchestrator.RunSprintKey(overflowDraft.Sprint)), cancellationToken);
        SprintTransitionResult overflowToRunning = await overflowOrchestrator.RunSprintAsync(
            new(environment.ProjectRoot, overflowSprintId, overflowToReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(overflowToReady.Sprint)), cancellationToken);
        Assert.True(overflowToRunning.Succeeded);

        SprintWorkflowState overflowRunning =
            (await overflowStore.LoadAsync(environment.ProjectRoot, overflowSprintId, cancellationToken))!;
        NodeSnapshot overflowRunningNodeA = overflowRunning.Nodes["a"];
        AppendOutcome overflowNodeARunning = await overflowStore.AppendTransitionAsync(
            environment.ProjectRoot, overflowSprintId, AggregateKind.Node, overflowRunningNodeA.Id.Value,
            "NodeChanged", "workflow.node_running", WorkflowStateNames.ToSnakeCase(NodeState.Running),
            overflowRunningNodeA.Version, Guid.NewGuid(), cancellationToken);
        AppendOutcome overflowNodeASucceeded = await overflowStore.AppendTransitionAsync(
            environment.ProjectRoot, overflowSprintId, AggregateKind.Node, overflowRunningNodeA.Id.Value,
            "NodeChanged", "workflow.node_succeeded", WorkflowStateNames.ToSnakeCase(NodeState.Succeeded),
            overflowNodeARunning.State!.Nodes["a"].Version, Guid.NewGuid(), cancellationToken);
        Assert.Equal(NodeState.Pending, overflowNodeASucceeded.State!.Nodes["b"].State);

        ResumeSchedulerOptions overflowOptions = new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50));
        ResumeSchedulerHostedService overflowService = new(
            overflowOptions, overflowStore, overflowScheduler, NullLogger<ResumeSchedulerHostedService>.Instance);
        await overflowService.StartAsync(cancellationToken);
        try
        {
            NodeState observed = NodeState.Pending;
            for (int attempt = 0; attempt < 40 && observed == NodeState.Pending; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                SprintWorkflowState polled = (await overflowStore.LoadAsync(
                    environment.ProjectRoot, overflowSprintId, cancellationToken))!;
                observed = polled.Nodes["b"].State;
            }

            Assert.Equal(NodeState.Ready, observed);
            Assert.Null(overflowService.ExecuteTask!.Exception);
        }
        finally
        {
            await overflowService.StopAsync(cancellationToken);
        }
    }

    // Before this fix, FileSprintEventLog.LoadAsync (the path SprintScheduler.RequireStateAsync
    // uses every tick, for every sprint) let a corrupt events.jsonl line's raw JsonException escape
    // uncaught. That raw JsonException would escape TickAsync's catch filter entirely, permanently
    // faulting the whole BackgroundService's ExecuteTask -- the same bug class fixed for
    // definition.json, just on the journal instead.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASprintWithACorruptJournalDoesNotStopOtherSprintsFromBeingReDerived()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initializedForJournal = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initializedForJournal.Succeeded);

        SprintOrchestrator journalOrchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler journalScheduler = environment.Resolve<SprintScheduler>();
        ISprintStore journalStore = environment.Resolve<ISprintStore>();

        CreateSprintResult corruptedJournal = await journalOrchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken);
        Assert.True(corruptedJournal.Succeeded);
        string eventsPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, corruptedJournal.SprintId!),
            "events.jsonl");
        await File.AppendAllTextAsync(eventsPath, "{ not valid json\n", cancellationToken);

        CreateSprintResult createdForJournal = await journalOrchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken);
        Assert.True(createdForJournal.Succeeded);
        SprintId journalSprintId = createdForJournal.SprintId!;

        SprintWorkflowState journalDraft =
            (await journalStore.LoadAsync(environment.ProjectRoot, journalSprintId, cancellationToken))!;
        SprintTransitionResult journalToReady = await journalOrchestrator.RunSprintAsync(
            new(environment.ProjectRoot, journalSprintId, journalDraft.Sprint.Version,
                SprintOrchestrator.RunSprintKey(journalDraft.Sprint)), cancellationToken);
        SprintTransitionResult journalToRunning = await journalOrchestrator.RunSprintAsync(
            new(environment.ProjectRoot, journalSprintId, journalToReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(journalToReady.Sprint)), cancellationToken);
        Assert.True(journalToRunning.Succeeded);

        SprintWorkflowState journalRunning =
            (await journalStore.LoadAsync(environment.ProjectRoot, journalSprintId, cancellationToken))!;
        NodeSnapshot journalNodeA = journalRunning.Nodes["a"];
        AppendOutcome journalNodeARunning = await journalStore.AppendTransitionAsync(
            environment.ProjectRoot, journalSprintId, AggregateKind.Node, journalNodeA.Id.Value, "NodeChanged",
            "workflow.node_running", WorkflowStateNames.ToSnakeCase(NodeState.Running), journalNodeA.Version,
            Guid.NewGuid(), cancellationToken);
        AppendOutcome journalNodeASucceeded = await journalStore.AppendTransitionAsync(
            environment.ProjectRoot, journalSprintId, AggregateKind.Node, journalNodeA.Id.Value, "NodeChanged",
            "workflow.node_succeeded", WorkflowStateNames.ToSnakeCase(NodeState.Succeeded),
            journalNodeARunning.State!.Nodes["a"].Version, Guid.NewGuid(), cancellationToken);
        Assert.Equal(NodeState.Pending, journalNodeASucceeded.State!.Nodes["b"].State);

        ResumeSchedulerOptions journalOptions = new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50));
        ResumeSchedulerHostedService journalService = new(
            journalOptions, journalStore, journalScheduler, NullLogger<ResumeSchedulerHostedService>.Instance);
        await journalService.StartAsync(cancellationToken);
        try
        {
            NodeState observed = NodeState.Pending;
            for (int attempt = 0; attempt < 40 && observed == NodeState.Pending; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                SprintWorkflowState polled = (await journalStore.LoadAsync(
                    environment.ProjectRoot, journalSprintId, cancellationToken))!;
                observed = polled.Nodes["b"].State;
            }

            Assert.Equal(NodeState.Ready, observed);
            Assert.Null(journalService.ExecuteTask!.Exception);
        }
        finally
        {
            await journalService.StopAsync(cancellationToken);
        }
    }

    // Before this fix, FileSprintEventLog.LoadDefinitionAsync let a corrupt definition.json's raw
    // JsonException escape uncaught -- unlike its sibling journal-read path, which already wraps
    // JsonException into InvalidDataException. That raw JsonException escaped TickAsync's catch
    // filter entirely, permanently faulting the whole BackgroundService's ExecuteTask.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASprintWithACorruptDefinitionDoesNotStopOtherSprintsFromBeingReDerived()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();

        CreateSprintResult corrupted = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken);
        Assert.True(corrupted.Succeeded);
        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, corrupted.SprintId!), "definition.json");
        await File.WriteAllTextAsync(definitionPath, "{ not valid json", cancellationToken);

        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken);
        Assert.True(created.Succeeded);
        SprintId sprintId = created.SprintId!;

        SprintWorkflowState draft = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Sprint.Version,
                SprintOrchestrator.RunSprintKey(draft.Sprint)), cancellationToken);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)), cancellationToken);
        Assert.True(toRunning.Succeeded);

        SprintWorkflowState running = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        NodeSnapshot nodeA = running.Nodes["a"];
        AppendOutcome nodeARunning = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Node, nodeA.Id.Value, "NodeChanged",
            "workflow.node_running", WorkflowStateNames.ToSnakeCase(NodeState.Running), nodeA.Version,
            Guid.NewGuid(), cancellationToken);
        AppendOutcome nodeASucceeded = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Node, nodeA.Id.Value, "NodeChanged",
            "workflow.node_succeeded", WorkflowStateNames.ToSnakeCase(NodeState.Succeeded),
            nodeARunning.State!.Nodes["a"].Version, Guid.NewGuid(), cancellationToken);
        Assert.Equal(NodeState.Pending, nodeASucceeded.State!.Nodes["b"].State);

        ResumeSchedulerOptions options = new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50));
        ResumeSchedulerHostedService service = new(
            options, store, scheduler, NullLogger<ResumeSchedulerHostedService>.Instance);
        await service.StartAsync(cancellationToken);
        try
        {
            NodeState observed = NodeState.Pending;
            for (int attempt = 0; attempt < 40 && observed == NodeState.Pending; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                SprintWorkflowState polled =
                    (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
                observed = polled.Nodes["b"].State;
            }

            Assert.Equal(NodeState.Ready, observed);
            Assert.Null(service.ExecuteTask!.Exception);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }
    }

    // Before this fix, any single sprint whose state failed to load (SprintScheduler's
    // RequireStateAsync/RequireDefinitionAsync throw InvalidOperationException) escaped TickAsync's
    // catch filter entirely, faulting the whole BackgroundService's ExecuteTask permanently -- so
    // every OTHER sprint in the project would silently stop being re-derived too, forever.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASprintWithNoDurableDefinitionDoesNotStopOtherSprintsFromBeingReDerived()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();

        // A sprint that is durably "created" (visible via ListAsync) but was never given a frozen
        // definition -- the exact shape RequireDefinitionAsync's InvalidOperationException guards.
        SprintId brokenSprintId = new(Guid.NewGuid());
        AppendOutcome brokenCreated = await store.AppendTransitionAsync(
            environment.ProjectRoot, brokenSprintId, AggregateKind.Sprint, brokenSprintId.Value.ToString("D"),
            "SprintChanged", "workflow.sprint_created", WorkflowStateNames.ToSnakeCase(SprintState.Draft), 0,
            Guid.NewGuid(), cancellationToken);
        Assert.True(brokenCreated.Succeeded);
        await store.MarkSprintCreatedAsync(environment.ProjectRoot, brokenSprintId, cancellationToken);

        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken);
        Assert.True(created.Succeeded);
        SprintId sprintId = created.SprintId!;

        SprintWorkflowState draft = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Sprint.Version,
                SprintOrchestrator.RunSprintKey(draft.Sprint)), cancellationToken);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)), cancellationToken);
        Assert.True(toRunning.Succeeded);

        SprintWorkflowState running = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        NodeSnapshot nodeA = running.Nodes["a"];
        AppendOutcome nodeARunning = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Node, nodeA.Id.Value, "NodeChanged",
            "workflow.node_running", WorkflowStateNames.ToSnakeCase(NodeState.Running), nodeA.Version,
            Guid.NewGuid(), cancellationToken);
        AppendOutcome nodeASucceeded = await store.AppendTransitionAsync(
            environment.ProjectRoot, sprintId, AggregateKind.Node, nodeA.Id.Value, "NodeChanged",
            "workflow.node_succeeded", WorkflowStateNames.ToSnakeCase(NodeState.Succeeded),
            nodeARunning.State!.Nodes["a"].Version, Guid.NewGuid(), cancellationToken);
        Assert.Equal(NodeState.Pending, nodeASucceeded.State!.Nodes["b"].State);

        // Sanity check: the broken sprint is really in ListAsync's result, ahead of the good one is
        // not required, just present -- so a naive foreach really does reach it.
        Assert.Contains(brokenSprintId, await store.ListAsync(environment.ProjectRoot, cancellationToken));

        ResumeSchedulerOptions options = new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50));
        ResumeSchedulerHostedService service = new(
            options, store, scheduler, NullLogger<ResumeSchedulerHostedService>.Instance);
        await service.StartAsync(cancellationToken);
        try
        {
            NodeState observed = NodeState.Pending;
            for (int attempt = 0; attempt < 40 && observed == NodeState.Pending; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                SprintWorkflowState polled =
                    (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
                observed = polled.Nodes["b"].State;
            }

            Assert.Equal(NodeState.Ready, observed);
            Assert.Null(service.ExecuteTask!.Exception);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheServiceTicksOverAProjectWithNoSprintsWithoutFailing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);

        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        ResumeSchedulerOptions options = new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50));
        ResumeSchedulerHostedService service = new(
            options, store, scheduler, NullLogger<ResumeSchedulerHostedService>.Instance);

        await service.StartAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        await service.StopAsync(cancellationToken);
    }

    // ADR 0005: a Host that loses the project-lease race must never mutate durable state. Before
    // ControlPlaneHostedService started ResumeSchedulerHostedService itself only after winning the
    // lease, both Hosts registered it as their own independent IHostedService and the generic host
    // started it unconditionally — including on the loser, which would tick against state it did
    // not own before its own shutdown ever caught up.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheResumeSchedulerNeverStartsOnAHostThatLosesTheProjectLease()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string firstInstanceId = InstanceIdentity.CreateEphemeral();
        string secondInstanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost first = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, firstInstanceId, cancellationToken);

        // Confirm "first" is listening (and therefore already holds the lease) before "second"
        // starts, or the two Hosts could race for the mutex in either order.
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, firstInstanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        await using ControlPlaneHost second = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, secondInstanceId, cancellationToken);
        Assert.True(await second.WaitForStoppingAsync(TimeSpan.FromSeconds(10), cancellationToken));

        ResumeSchedulerHostedService winnerScheduler =
            first.Services.GetRequiredService<ResumeSchedulerHostedService>();
        ResumeSchedulerHostedService loserScheduler =
            second.Services.GetRequiredService<ResumeSchedulerHostedService>();
        Assert.NotNull(winnerScheduler.ExecuteTask);
        Assert.Null(loserScheduler.ExecuteTask);
    }
}
