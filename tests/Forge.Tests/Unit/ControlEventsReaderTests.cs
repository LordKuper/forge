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

    [Fact]
    [Trait("Category", "Unit")]
    public void AdvanceWatermarksNeverSkipsAGapEvenWhenAHigherSequenceArrivesFirst()
    {
        Guid sprintId = Guid.NewGuid();

        // A page that returned sequence 1 but not sequence 0 for the same sprint — exactly what a
        // merge-and-cut can produce when a non-monotonic clock sorts a later append earlier.
        Dictionary<string, long> afterGap = ControlEventsReader.AdvanceWatermarks(
            new Dictionary<string, long>(),
            [(sprintId, FakeEvent(sprintId, sequence: 1))]);

        Assert.Equal(-1, afterGap[sprintId.ToString("D")]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AdvanceWatermarksCatchesUpOnceTheMissingSequenceArrives()
    {
        Guid sprintId = Guid.NewGuid();
        Dictionary<string, long> afterGap = ControlEventsReader.AdvanceWatermarks(
            new Dictionary<string, long>(),
            [(sprintId, FakeEvent(sprintId, sequence: 1))]);

        Dictionary<string, long> afterCatchUp = ControlEventsReader.AdvanceWatermarks(
            afterGap,
            [(sprintId, FakeEvent(sprintId, sequence: 0)), (sprintId, FakeEvent(sprintId, sequence: 1))]);

        Assert.Equal(1, afterCatchUp[sprintId.ToString("D")]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AdvanceWatermarksAdvancesNormallyWithNoGap()
    {
        Guid sprintId = Guid.NewGuid();

        Dictionary<string, long> watermarks = ControlEventsReader.AdvanceWatermarks(
            new Dictionary<string, long>(),
            [
                (sprintId, FakeEvent(sprintId, sequence: 0)),
                (sprintId, FakeEvent(sprintId, sequence: 1)),
                (sprintId, FakeEvent(sprintId, sequence: 2)),
            ]);

        Assert.Equal(2, watermarks[sprintId.ToString("D")]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnEventStrandedPastAGapByAPageCutIsNeverDeliveredBeforeItsPredecessorAndNeverRedelivered()
    {
        // Sprint A appends sequence 0 with a *later* OccurredAt than sequence 1 (a non-monotonic
        // clock). Sprint B contributes enough filler events, all timestamped strictly between
        // sequence 1's and sequence 0's OccurredAt, that a single MaxEventsPerRead-bounded read sorts
        // sequence 1 in, sorts every filler in, and cuts off exactly before sequence 0.
        Guid sprintA = Guid.NewGuid();
        Guid sprintB = Guid.NewGuid();
        DateTimeOffset early = DateTimeOffset.UtcNow;
        DateTimeOffset late = early.AddMinutes(10);
        List<WorkflowEvent> sprintAEvents =
        [
            FakeEvent(sprintA, sequence: 0, occurredAt: late),
            FakeEvent(sprintA, sequence: 1, occurredAt: early),
        ];
        List<WorkflowEvent> sprintBEvents = [.. Enumerable.Range(0, ControlEventsReader.MaxEventsPerRead)
            .Select(index => FakeEvent(sprintB, index, early.AddSeconds(index + 1)))];
        FakeSprintStore store = new(new Dictionary<Guid, List<WorkflowEvent>>
        {
            [sprintA] = sprintAEvents,
            [sprintB] = sprintBEvents,
        });
        ControlEventsReader reader = new(store);

        ControlEventsPage first = await reader.ReadAsync(
            "unused-project-root", null, TestContext.Current.CancellationToken);

        // Sequence 1 was cut off from sequence 0 by the page limit — it must not be delivered ahead
        // of its predecessor.
        Assert.DoesNotContain(first.Events, record => record.SprintId == sprintA && record.Event.Sequence == 1);

        // A second read with an unrelated, far-future cursor state for sprint B (simulating "sprint B
        // fully drained") still must not conjure sequence 1 out of order, and once sequence 0 finally
        // fits, both arrive exactly once — never sequence 1 twice.
        ControlEventsPage second = await reader.ReadAsync(
            "unused-project-root", first.Cursor, TestContext.Current.CancellationToken);
        List<ControlEventRecord> sprintARecords = [.. second.Events.Where(record => record.SprintId == sprintA)];
        Assert.Equal([0L, 1L], sprintARecords.Select(record => record.Event.Sequence).Order());
    }

    private sealed class FakeSprintStore(IReadOnlyDictionary<Guid, List<WorkflowEvent>> events) : ISprintStore
    {
        public Task<IReadOnlyList<SprintId>> ListAsync(string projectRoot, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SprintId>>([.. events.Keys.Select(id => new SprintId(id))]);

        public Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowEvent>>(events[sprintId.Value]);

        public Task<SprintWorkflowState?> LoadAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSprintCreatedAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppendOutcome> AppendTransitionAsync(
            string projectRoot, SprintId sprintId, AggregateKind aggregateKind, string aggregateId, string type,
            string messageKey, string toState, long expectedAggregateVersion, Guid idempotencyKey,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? extraArguments = null) =>
            throw new NotSupportedException();

        public Task SaveDefinitionAsync(string projectRoot, SprintDefinition definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SprintDefinition?> LoadDefinitionAsync(string projectRoot, SprintId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveNodeResultAsync(string projectRoot, NodeResult result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NodeResult>> GetNodeResultsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveFindingAsync(string projectRoot, Finding finding, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Finding>> GetFindingsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveHandoffAsync(string projectRoot, Handoff handoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Handoff>> GetHandoffsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveConfirmationAsync(
            string projectRoot, ConfirmationArtifact confirmation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConfirmationArtifact>> GetConfirmationsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveReviewIterationAsync(
            string projectRoot, ReviewIterationRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewIterationRecord>> GetReviewIterationsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetReviewFloorPinnedAsync(
            string projectRoot, SprintId sprintId, string nodeId, ReviewDimension dimension,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsReviewFloorPinnedAsync(
            string projectRoot, SprintId sprintId, string nodeId, ReviewDimension dimension,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AppendRouteDecisionAsync(string projectRoot, RouteDecision decision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
            string projectRoot, SprintId sprintId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AppendAttemptActivityAsync(
            string projectRoot, SprintId sprintId, AttemptId attemptId, CancellationToken cancellationToken,
            AttemptActivityKind kind = AttemptActivityKind.Heartbeat) =>
            throw new NotSupportedException();
    }

    private static WorkflowEvent FakeEvent(Guid sprintId, long sequence, DateTimeOffset occurredAt) =>
        new(
            Guid.NewGuid(),
            sequence,
            occurredAt,
            "SprintTransitioned",
            new(AggregateKind.Sprint, sprintId.ToString("D"), sequence + 1),
            "sprint.transitioned",
            new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.ToStateArgument] = "draft" });

    private static WorkflowEvent FakeEvent(Guid sprintId, long sequence) =>
        new(
            Guid.NewGuid(),
            sequence,
            DateTimeOffset.UtcNow,
            "SprintTransitioned",
            new(AggregateKind.Sprint, sprintId.ToString("D"), sequence + 1),
            "sprint.transitioned",
            new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.ToStateArgument] = "draft" });

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
