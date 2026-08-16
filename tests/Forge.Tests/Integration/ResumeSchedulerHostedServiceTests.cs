using Forge.Application;
using Forge.Domain;
using Forge.Host;
using Forge.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.IntegrationTests;

public sealed class ResumeSchedulerHostedServiceTests
{
    // ADR 0006: a node whose dependency just settled must not stay stuck forever waiting for some
    // other call to happen to notice — this proves the background timer itself, not
    // SprintScheduler.AdvanceGraphAsync's own correctness (already covered by SprintSchedulerTests).
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
}
