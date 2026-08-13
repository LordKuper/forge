using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ControlEventsReaderTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AProjectWithNoSprintsReadsAnEmptyPageWithAUsableCursor()
    {
        using TestEnvironment environment = await InitializedAsync();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();

        ControlEventsPage page = await reader.ReadAsync(
            environment.ProjectRoot, null, TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
        Assert.Equal(DiagnosticCodes.None, page.DiagnosticCode);
        Assert.True(ControlEventsCursorCodec.TryDecode(page.Cursor, out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreatingASprintProducesAnUnseenEventThatTheReturnedCursorConsumes()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        ControlEventsPage first = await reader.ReadAsync(environment.ProjectRoot, null, cancellationToken);
        Assert.NotEmpty(first.Events);
        Assert.All(first.Events, record => Assert.Equal(sprintId.Value, record.SprintId));

        ControlEventsPage second = await reader.ReadAsync(environment.ProjectRoot, first.Cursor, cancellationToken);
        Assert.Empty(second.Events);
        Assert.Equal(DiagnosticCodes.None, second.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AdvancingASprintAfterTheFirstReadOnlyReturnsTheNewEvents()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        ControlEventsPage baseline = await reader.ReadAsync(environment.ProjectRoot, null, cancellationToken);

        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);

        ControlEventsPage page = await reader.ReadAsync(environment.ProjectRoot, baseline.Cursor, cancellationToken);

        ControlEventRecord record = Assert.Single(page.Events);
        Assert.Equal(sprintId.Value, record.SprintId);
        Assert.Equal("ready", record.Event.Arguments[WorkflowEvent.ToStateArgument]);
        Assert.Equal(SprintState.Ready, toReady.Sprint!.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANewlyCreatedSprintIsDiscoveredByAnOlderCursor()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ControlEventsPage baseline = await reader.ReadAsync(environment.ProjectRoot, null, cancellationToken);

        SprintId secondSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        ControlEventsPage page = await reader.ReadAsync(environment.ProjectRoot, baseline.Cursor, cancellationToken);

        Assert.NotEmpty(page.Events);
        Assert.All(page.Events, record => Assert.Equal(secondSprintId.Value, record.SprintId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AMalformedCursorFailsLoudlyWithAFreshAnchorInsteadOfSilentlyRebaselining()
    {
        using TestEnvironment environment = await InitializedAsync();
        ControlEventsReader reader = environment.Resolve<ControlEventsReader>();

        ControlEventsPage page = await reader.ReadAsync(
            environment.ProjectRoot, "not-a-real-cursor", TestContext.Current.CancellationToken);

        Assert.Empty(page.Events);
        Assert.Equal(DiagnosticCodes.ControlCursorStale, page.DiagnosticCode);
        Assert.True(ControlEventsCursorCodec.TryDecode(page.Cursor, out ControlEventsCursor anchor));
        Assert.Empty(anchor.Watermarks);
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
