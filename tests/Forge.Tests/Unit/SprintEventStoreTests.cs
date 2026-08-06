using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class SprintEventStoreTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AppendedTransitionsFoldIntoCurrentState()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        string sprintKey = sprintId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        AppendOutcome created = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        AppendOutcome ready = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);

        Assert.True(created.Succeeded);
        Assert.True(ready.Succeeded);
        Assert.Equal(SprintState.Ready, ready.State!.Sprint.State);
        Assert.Equal(2, ready.State.Sprint.Version);

        SprintWorkflowState? reloaded = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(SprintState.Ready, reloaded!.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StaleExpectedVersionIsRejectedWithoutAppending()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);

        AppendOutcome conflict = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_advanced", "ready", 0, Guid.NewGuid(), cancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, conflict.DiagnosticCode);
        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(SprintState.Draft, state!.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayingTheSameIdempotencyKeyDoesNotAppendTwice()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        string sprintKey = sprintId.Value.ToString("D");
        Guid idempotencyKey = Guid.NewGuid();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);

        AppendOutcome first = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, idempotencyKey, cancellationToken);
        AppendOutcome replay = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, idempotencyKey, cancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.Equal(2, replay.State!.Sprint.Version);
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        Assert.Equal(2, (await File.ReadAllLinesAsync(eventsPath, cancellationToken)).Length);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReopeningTheStoreAfterAWriteResumesFromDurableEvents()
    {
        using TestRoot root = new();
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await new FileSprintEventLog(new FakeClock()).AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);

        // A brand new instance simulates a process restart: nothing but the durable events.jsonl
        // is shared with the writer above.
        FileSprintEventLog reopened = new(new FakeClock());
        SprintWorkflowState? state = await reopened.LoadAsync(root.Path, sprintId, cancellationToken);

        Assert.NotNull(state);
        Assert.Equal(SprintState.Draft, state.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATornTrailingLineFromACrashIsIgnoredWithoutLosingEarlierEvents()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        await File.AppendAllTextAsync(eventsPath, "{\"schema_version\":\"1.0.0\",\"event_id\":\"trunc", cancellationToken);

        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);

        Assert.NotNull(state);
        Assert.Equal(SprintState.Draft, state.Sprint.State);
        Assert.Equal(0, state.LastSequence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATornLineIsTruncatedSoTheNextAppendDoesNotConcatenateOntoItOrLoseData()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        await File.AppendAllTextAsync(eventsPath, "{\"schema_version\":\"1.0.0\",\"event_id\":\"trunc", cancellationToken);

        // This append is the one that must both see the file cleaned up AND actually land — not
        // silently report success while its event is lost, and not throw on a now-unparseable file.
        AppendOutcome ready = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);
        AppendOutcome running = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_advanced", "running", 2, Guid.NewGuid(), cancellationToken);

        Assert.True(ready.Succeeded);
        Assert.Equal(SprintState.Ready, ready.State!.Sprint.State);
        Assert.True(running.Succeeded);
        Assert.Equal(SprintState.Running, running.State!.Sprint.State);
        SprintWorkflowState? reloaded = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(SprintState.Running, reloaded!.Sprint.State);
        Assert.Equal(3, reloaded.Sprint.Version);
        string[] lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadingAnUnknownSprintReturnsNull()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());

        SprintWorkflowState? state =
            await log.LoadAsync(root.Path, SprintId.New(), TestContext.Current.CancellationToken);

        Assert.Null(state);
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
}

internal sealed class TestRoot : IDisposable
{
    public TestRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"forge-sprint-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // Temporary directories are reclaimed by the operating system.
        }
    }
}
