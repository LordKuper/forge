using System.Text.Json.Nodes;
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
    public async Task ATransitionMissingToStateFailsClosedWithoutAppendingFromStaleState()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        JsonNode corrupted = JsonNode.Parse(await File.ReadAllTextAsync(eventsPath, cancellationToken))!;
        Assert.True(corrupted["arguments"]!.AsObject().Remove("to_state"));
        await File.WriteAllTextAsync(eventsPath, corrupted.ToJsonString() + "\n", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => log.LoadAsync(root.Path, sprintId, cancellationToken));
        AppendOutcome append = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_ready", "ready", 1, Guid.NewGuid(), cancellationToken);

        Assert.False(append.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowLogCorrupted, append.DiagnosticCode);
        Assert.Single(await File.ReadAllLinesAsync(eventsPath, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnActivityHeartbeatAdvancesLastActivityButIsDroppedOnReplayOnceTheAttemptIsTerminal()
    {
        using TestRoot root = new();
        FakeClock clock = new();
        FileSprintEventLog log = new(clock);
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        string attemptKey = attemptId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_created", "created", 0, Guid.NewGuid(), cancellationToken);

        await log.AppendAttemptActivityAsync(root.Path, sprintId, attemptId, cancellationToken);
        DateTimeOffset firstHeartbeat = clock.UtcNow;
        SprintWorkflowState? afterFirstHeartbeat = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(firstHeartbeat, afterFirstHeartbeat!.Attempts[attemptKey].LastActivityAt);

        clock.UtcNow += TimeSpan.FromMinutes(1);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_cancelled", "cancelled", 1, Guid.NewGuid(), cancellationToken);
        // A heartbeat appended after the attempt already went terminal is durably written (this
        // store never gates the append itself) but must never surface on replay.
        await log.AppendAttemptActivityAsync(root.Path, sprintId, attemptId, cancellationToken);

        SprintWorkflowState? final = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(AttemptState.Cancelled, final!.Attempts[attemptKey].State);
        Assert.Equal(firstHeartbeat, final.Attempts[attemptKey].LastActivityAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnActivityEventCarryingAToStateArgumentIsRejectedAsCorrupt()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_created", "created", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendAttemptActivityAsync(root.Path, sprintId, attemptId, cancellationToken);
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        string[] lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken);
        JsonNode activityEvent = JsonNode.Parse(lines[^1])!;
        Assert.Equal("AttemptActivityRecorded", activityEvent["type"]!.GetValue<string>());
        activityEvent["arguments"]!["to_state"] = "running";
        lines[^1] = activityEvent.ToJsonString();
        await File.WriteAllLinesAsync(eventsPath, lines, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => log.LoadAsync(root.Path, sprintId, cancellationToken));
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
    public async Task AFinalLineMissingOnlyItsTerminatingNewlineIsDiscardedEvenThoughItParses()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        // Simulate a crash that flushed the second event's complete, valid JSON but not its own
        // trailing '\n' — the write was never confirmed, even though the bytes happen to parse.
        byte[] bytes = await File.ReadAllBytesAsync(eventsPath, cancellationToken);
        await File.WriteAllBytesAsync(eventsPath, bytes[..^1], cancellationToken);

        SprintWorkflowState? loaded = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        // Recovery must re-append "ready" at version 1 (the unconfirmed one was fully discarded,
        // not kept) — appending "running" straight from "draft" would be illegal.
        AppendOutcome readyAgain = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);

        Assert.Equal(SprintState.Draft, loaded!.Sprint.State);
        Assert.True(readyAgain.Succeeded);
        Assert.Equal(SprintState.Ready, readyAgain.State!.Sprint.State);
        string[] lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken);
        Assert.Equal(2, lines.Length);
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
