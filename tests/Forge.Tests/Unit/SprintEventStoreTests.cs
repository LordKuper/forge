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

    // Round 9 review of PR #68: AppendTransitionAsync's idempotent-replay branch calls LoadAsync
    // while still holding this sprint directory's Locks entry. If a torn trailing line (crash
    // residue) is present, ReadEventsAsync's own final-truncate path re-acquires that same
    // non-reentrant semaphore -- a self-deadlock, reachable through ordinary crash residue plus a
    // replayed idempotency key, both designed-for paths. Bounded with a short cancellation deadline
    // so a regression fails the assertion instead of hanging the test run indefinitely.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayingAnIdempotencyKeyAfterATornTrailingLineDoesNotDeadlock()
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
        Assert.True(first.Succeeded);

        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        await File.AppendAllTextAsync(eventsPath, "{\"event_id\":\"partial", cancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        AppendOutcome replay = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, idempotencyKey, timeout.Token);

        Assert.True(replay.Succeeded);
        Assert.Equal(2, replay.State!.Sprint.Version);
    }

    // Round 9 review of PR #68: AppendLineAsync's own IOException retry (added to absorb transient
    // contention under CI-shaped load) re-opens the file and rewrites the whole line on every
    // attempt, which is only safe if no attempt already wrote any of those bytes to disk -- fixed by
    // truncating back to the pre-attempt length on every attempt. This test forces a real,
    // reproducible IOException on the write's own open, via the same exclusive-lock technique
    // LoadAsyncRecoversFromATransientSharingViolationOnTheJournal already uses for the read side, and
    // proves the retry recovers to exactly one well-formed new line rather than a duplicate. Honestly
    // scoped: a sharing violation on open never leaves partial bytes on disk, so this does not by
    // itself exercise the truncate-on-retry branch -- no production hook exists to interrupt a
    // FileStream between WriteAsync and disposal to force that specific case deterministically.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AppendTransitionAsyncRecoversFromATransientSharingViolationWithoutDuplicatingTheLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            // FileShare.None sharing-violation enforcement is reliably a hard failure only on
            // Windows; .NET's FileStream does not emulate it on Unix.
            return;
        }

        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        string sprintKey = sprintId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);

        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        await using FileStream exclusiveLock = new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Task<AppendOutcome> appendTask = log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);

        // Released well within the retry helper's wall-clock deadline, so both the read and the
        // write inside AppendTransitionAsync must recover rather than exhaust it.
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        await exclusiveLock.DisposeAsync();

        AppendOutcome outcome = await appendTask;
        Assert.True(outcome.Succeeded);
        Assert.Equal(SprintState.Ready, outcome.State!.Sprint.State);
        string[] lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken);
        Assert.Equal(2, lines.Length);
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
    public async Task AnActivityEventCarryingAToolUseKindFoldsIntoTheAttemptSnapshot()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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

        await log.AppendAttemptActivityAsync(
            root.Path, sprintId, attemptId, cancellationToken, AttemptActivityKind.ToolUse);

        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(AttemptActivityKind.ToolUse, state!.Attempts[attemptKey].LastActivityKind);
    }

    /// <summary>Regression test: an earlier version of the fold's attempt-transition branch
    /// rebuilt <c>AttemptSnapshot</c> carrying forward <c>NodeId</c>/<c>TargetOutcome</c>/
    /// <c>LastActivityAt</c> but silently omitted <c>LastActivityKind</c>, resetting it to
    /// <see langword="null"/> on every subsequent transition -- a wrong classification (plain
    /// heartbeat), not merely a missing one.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LastActivityKindSurvivesASubsequentAttemptTransition()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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
        await log.AppendAttemptActivityAsync(
            root.Path, sprintId, attemptId, cancellationToken, AttemptActivityKind.ToolUse);

        AppendOutcome transitioned = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_preparing", "preparing", 1, Guid.NewGuid(), cancellationToken);
        Assert.True(transitioned.Succeeded, "Created -> Preparing must be a valid attempt transition.");

        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(AttemptState.Preparing, state!.Attempts[attemptKey].State);
        Assert.Equal(AttemptActivityKind.ToolUse, state.Attempts[attemptKey].LastActivityKind);
    }

    /// <summary>An event recorded before this argument existed (pre-v0.37) never carried
    /// `activity_kind` at all -- replay must fold it to a `null` kind, not throw, so historical
    /// journals stay loadable.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnActivityEventWithNoActivityKindArgumentFoldsToANullKindForBackwardCompatibility()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        string[] lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken);
        JsonNode activityEvent = JsonNode.Parse(lines[^1])!;
        Assert.Equal("AttemptActivityRecorded", activityEvent["type"]!.GetValue<string>());
        ((JsonObject)activityEvent["arguments"]!).Remove("activity_kind");
        lines[^1] = activityEvent.ToJsonString();
        await File.WriteAllLinesAsync(eventsPath, lines, cancellationToken);

        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Null(state!.Attempts[attemptKey].LastActivityKind);
    }

    /// <summary>Plan section 12.3's sticky header provider/model gap: the attempt's own creation
    /// event carries the routed <c>provider</c>/<c>model</c> arguments
    /// (<see cref="SprintScheduler.StartAttemptAsync"/>'s own append), and a later transition that
    /// omits them (every subsequent attempt state change) must carry the already-recorded values
    /// forward rather than reset them to <see langword="null"/> -- the same carry-forward discipline
    /// <see cref="LastActivityKindSurvivesASubsequentAttemptTransition"/> already proves for
    /// <c>LastActivityKind</c>.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProviderAndModelPopulateFromTheAttemptCreatedEventAndSurviveASubsequentTransition()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        string attemptKey = attemptId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_created", "created", 0, Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?>
            {
                [WorkflowEvent.ProviderArgument] = "claude_code",
                [WorkflowEvent.ModelArgument] = "claude-sonnet-4-5",
            });

        SprintWorkflowState? created = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal("claude_code", created!.Attempts[attemptKey].Provider);
        Assert.Equal("claude-sonnet-4-5", created.Attempts[attemptKey].Model);

        AppendOutcome transitioned = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_preparing", "preparing", 1, Guid.NewGuid(), cancellationToken);
        Assert.True(transitioned.Succeeded, "Created -> Preparing must be a valid attempt transition.");

        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(AttemptState.Preparing, state!.Attempts[attemptKey].State);
        Assert.Equal("claude_code", state.Attempts[attemptKey].Provider);
        Assert.Equal("claude-sonnet-4-5", state.Attempts[attemptKey].Model);
    }

    /// <summary>An attempt recorded before <c>provider</c>/<c>model</c> existed on the creation
    /// event (every attempt in every journal written before this slice) never carries either
    /// argument at all -- replay must fold it to a <see langword="null"/> provider/model, not throw,
    /// so historical journals stay loadable and the sticky header falls back to its existing "not
    /// yet available" placeholder exactly as before this change.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ALegacyAttemptCreatedEventWithNoProviderOrModelFoldsToNullForBackwardCompatibility()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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

        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Null(state!.Attempts[attemptKey].Provider);
        Assert.Null(state.Attempts[attemptKey].Model);
    }

    /// <summary>Forge is the sole writer of its own event log, so an unrecognized `activity_kind`
    /// value means the journal was corrupted or written by an incompatible version -- this must
    /// fail loudly on replay, the same convention every other snake_case-encoded enum argument in
    /// the fold already follows (see <see cref="AnActivityEventCarryingAToStateArgumentIsRejectedAsCorrupt"/>).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnActivityEventWithAnUnknownActivityKindValueFailsLoudlyOnReplay()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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
        string eventsPath = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "events.jsonl");
        string[] lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken);
        JsonNode activityEvent = JsonNode.Parse(lines[^1])!;
        activityEvent["arguments"]!["activity_kind"] = "from_the_future";
        lines[^1] = activityEvent.ToJsonString();
        await File.WriteAllLinesAsync(eventsPath, lines, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => log.LoadAsync(root.Path, sprintId, cancellationToken));
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

    /// <summary>Plan section 7.2: <c>running -> paused</c> is a new legal sprint transition, and
    /// <c>FileSprintEventLog.IsLegalTransition</c> is the single store-level chokepoint that gates
    /// every append -- there is no generic public setter that could assign <c>Paused</c> directly,
    /// only this validated append path.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunningSprintCanBePausedAndPausedSprintCanResumeOrCancel()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        string sprintKey = sprintId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_running", "running", 2, Guid.NewGuid(), cancellationToken);

        AppendOutcome paused = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_paused", "paused", 3, Guid.NewGuid(), cancellationToken);
        Assert.True(paused.Succeeded, "Running -> Paused must be a valid sprint transition.");
        Assert.Equal(SprintState.Paused, paused.State!.Sprint.State);

        AppendOutcome resumed = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_ready", "ready", 4, Guid.NewGuid(), cancellationToken);
        Assert.True(resumed.Succeeded, "Paused -> Ready must be a valid sprint transition.");
        Assert.Equal(SprintState.Ready, resumed.State!.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PausedSprintCanBeCancelled()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        string sprintKey = sprintId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_advanced", "ready", 1, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_running", "running", 2, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_paused", "paused", 3, Guid.NewGuid(), cancellationToken);

        AppendOutcome cancelled = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_cancelled", "cancelled", 4, Guid.NewGuid(), cancellationToken);

        Assert.True(cancelled.Succeeded, "Paused -> Cancelled must be a valid sprint transition.");
        Assert.Equal(SprintState.Cancelled, cancelled.State!.Sprint.State);
    }

    /// <summary>A direct <c>draft -> paused</c> jump (skipping ready/running) is not in the frozen
    /// transition table and must be rejected by the store's own gate without appending anything --
    /// proof that <c>Paused</c> cannot be assigned outside the validated transitions this slice
    /// adds.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADirectDraftToPausedTransitionIsRejectedWithoutAppending()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        string sprintKey = sprintId.Value.ToString("D");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);

        AppendOutcome rejected = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged",
            "workflow.sprint_paused", "paused", 1, Guid.NewGuid(), cancellationToken);

        Assert.False(rejected.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowTransitionInvalid, rejected.DiagnosticCode);
        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(SprintState.Draft, state!.Sprint.State);
    }

    /// <summary>Plan section 7.2: <c>validating -> cancelled</c> must remain a valid attempt
    /// transition so a stop request stays valid until the operation has actually settled.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ValidatingAttemptCanBeCancelled()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_preparing", "preparing", 1, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_running", "running", 2, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_validating", "validating", 3, Guid.NewGuid(), cancellationToken);

        AppendOutcome cancelled = await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_cancelled", "cancelled", 4, Guid.NewGuid(), cancellationToken);

        Assert.True(cancelled.Succeeded, "Validating -> Cancelled must be a valid attempt transition.");
        Assert.Equal(AttemptState.Cancelled, cancelled.State!.Attempts[attemptKey].State);
    }

    /// <summary>ADR 0059: the whole point of the `payload` envelope is carrying a nested list the
    /// flat <see cref="WorkflowEvent.Arguments"/> map genuinely cannot. This proves the structured
    /// payload survives the full durable round trip (serialize -> schema-validate -> JSONL ->
    /// re-read -> schema-validate -> deserialize) with per-file rows intact, and that the flat
    /// summary the timeline template renders is derived from that same payload rather than supplied
    /// independently, so the two can never disagree.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAttemptDiffPayloadSurvivesTheJournalRoundTripWithItsPerFileRowsIntact()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
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
        DiffPayload diff = new(
            3,
            12,
            4,
            [
                new DiffFileStat("src/Forge.Runtime/A.cs", 10, 4, DiffChangeKinds.Modified),
                new DiffFileStat("docs/b.md", 2, 0, DiffChangeKinds.Added),
                new DiffFileStat("assets/logo.png", 0, 0, DiffChangeKinds.Binary),
            ],
            1);

        await log.AppendAttemptDiffRecordedAsync(root.Path, sprintId, attemptId, diff, cancellationToken);

        IReadOnlyList<WorkflowEvent> events = await log.GetEventsAsync(root.Path, sprintId, cancellationToken);
        WorkflowEvent recorded = Assert.Single(
            events, item => item.Type == WorkflowEvent.AttemptDiffRecordedType);
        // Compared field by field, not with record equality: a record's synthesized Equals compares
        // its IReadOnlyList member by reference, so `Assert.Equal(diff, ...)` would fail on an
        // otherwise perfect round trip and pass on nothing useful.
        DiffPayload actual = recorded.Payload!.Diff!;
        Assert.Equal(diff.FilesChanged, actual.FilesChanged);
        Assert.Equal(diff.Insertions, actual.Insertions);
        Assert.Equal(diff.Deletions, actual.Deletions);
        Assert.Equal(diff.ElidedFiles, actual.ElidedFiles);
        Assert.Equal(diff.Files, actual.Files);
        Assert.Equal(attemptKey, recorded.Aggregate.Id);
        Assert.Equal("3", recorded.Arguments[WorkflowEvent.DiffFilesChangedArgument]);
        Assert.Equal("12", recorded.Arguments[WorkflowEvent.DiffInsertionsArgument]);
        Assert.Equal("4", recorded.Arguments[WorkflowEvent.DiffDeletionsArgument]);

        // Recorded at most once per attempt: an attempt produces exactly one commit, so a second
        // call is always a replay of the same already-landed one.
        await log.AppendAttemptDiffRecordedAsync(root.Path, sprintId, attemptId, diff, cancellationToken);
        IReadOnlyList<WorkflowEvent> replayed = await log.GetEventsAsync(root.Path, sprintId, cancellationToken);
        Assert.Single(replayed, item => item.Type == WorkflowEvent.AttemptDiffRecordedType);

        // Never folded: a diff summary is durable audit content, not workflow state, so it must not
        // disturb the attempt's own state or version.
        SprintWorkflowState? state = await log.LoadAsync(root.Path, sprintId, cancellationToken);
        Assert.Equal(AttemptState.Created, state!.Attempts[attemptKey].State);
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
