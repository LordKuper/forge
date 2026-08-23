using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 6.3's versioned, cursor-paged timeline projection over the existing
/// append-only workflow journal. Confirms real incremental-loading and redaction behavior before the
/// smallest risk-based tests were added.</summary>
public sealed class SprintTimelineTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheFirstPageReportsTheSprintsOwnCreationAsASystemItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        Assert.Equal(DiagnosticCodes.None, page.DiagnosticCode);
        Assert.Equal(sprintId.Value, page.SprintId);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, item => Assert.Equal(TimelineActor.System, item.Actor));
        Assert.Contains(page.Items, item => item.TargetKind == "sprint");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnknownSprintIdReportsSprintNotFoundWithAnEmptyPage()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, Guid.NewGuid(), null, cancellationToken);

        Assert.Equal(DiagnosticCodes.SprintNotFound, page.DiagnosticCode);
        Assert.Empty(page.Items);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RepeatingTheSameCursorNeverRedeliversAnAlreadySeenItemAndANewEventArrivesExactlyOnce()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        SprintTimelinePage firstPage = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.NotEmpty(firstPage.Items);

        SprintTimelinePage caughtUp = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, firstPage.Cursor, cancellationToken);
        Assert.Empty(caughtUp.Items);

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        SprintTimelinePage nextPage = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, firstPage.Cursor, cancellationToken);

        Assert.NotEmpty(nextPage.Items);
        Assert.Empty(nextPage.Items.Select(item => item.Id).Intersect(firstPage.Items.Select(item => item.Id)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARawCredentialInAnOperatorInstructionNeverAppearsInAProjectedTimelineItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        const string secret = "password=Sup3rSecretValue!!";
        await store.AppendAttemptSupersededAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, $"Retry, credential was {secret}",
            cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        SprintTimelineItem supersession =
            Assert.Single(page.Items, item => item.Type == WorkflowEvent.AttemptSupersededType);
        Assert.Equal(TimelineActor.Operator, supersession.Actor);
        Assert.All(
            supersession.Arguments.Values,
            value => Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.Ordinal));
        string serialized = StatusJson.Serialize(page);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
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
