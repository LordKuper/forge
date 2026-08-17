using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Stage 11: <see cref="SprintScheduler.RecordReviewIterationAsync"/> is the ASD severity-floor
/// engine's one entry point. See ADR 0015 for the design; these tests exercise it end to end
/// through a real sprint rather than <see cref="ReviewConvergencePolicyTests"/>'s pure functions.
/// </summary>
public sealed class ReviewEngineTests
{
    private static readonly IReadOnlyList<NodeDefinition> ReviewGraph =
        [new("review", NodeKind.Work, [], NodeRole.Review)];

    private static readonly CoverageLedger CompleteCoverage = new(["a.cs"], ["rule_1"], ["a.cs"], ["rule_1"]);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAgainstAnUnknownNodeIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult result = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "does_not_exist", ReviewDimension.Implementation,
            ReviewerKind.Internal, ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeNotFound, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecordingAgainstANonReviewNodeIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]), cancellationToken))
            .SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        RecordReviewIterationResult result = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "a", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.NodeKindMismatch, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnIncompleteCoverageLedgerRecordsNothingAndConsumesNoIteration()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);
        CoverageLedger incomplete = new(["a.cs", "b.cs"], ["rule_1"], ["a.cs"], ["rule_1"]);

        RecordReviewIterationResult rejected = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], incomplete, cancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, rejected.DiagnosticCode);

        RecordReviewIterationResult accepted = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);
        Assert.True(accepted.Succeeded);
        // The rejected call must not have consumed iteration 1 -- this is the fresh re-dispatch
        // ADR 0006 describes ("causes one fresh re-dispatch in the same iteration"), not a new one.
        Assert.Equal(1, accepted.Record!.Iteration);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IterationCounterIsIndependentPerDimension()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult design1 = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Design, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);
        RecordReviewIterationResult implementation1 = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);
        RecordReviewIterationResult design2 = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Design, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);

        Assert.Equal(1, design1.Record!.Iteration);
        Assert.Equal(1, implementation1.Record!.Iteration);
        Assert.Equal(2, design2.Record!.Iteration);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindingsBelowTheSeverityFloorAreRecordedDismissedNotOpen()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        // Iteration 1's floor is Low (ADR 0006) -- an Info-severity finding is below it.
        await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.ChangesRequested,
            [
                new(FindingSeverity.Info, "review.info_finding", new Dictionary<string, string?>(), ["evidence"]),
                new(FindingSeverity.Low, "review.low_finding", new Dictionary<string, string?>(), ["evidence"]),
            ],
            CompleteCoverage, cancellationToken);

        IReadOnlyList<Finding> findings = await scheduler.GetFindingsAsync(
            environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(
            FindingStatus.Dismissed, findings.Single(item => item.MessageKey == "review.info_finding").Status);
        Assert.Equal(FindingStatus.Open, findings.Single(item => item.MessageKey == "review.low_finding").Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARepeatedExternalFindingSetBlocksTheSprintForConvergence()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);
        ReviewFindingDraft finding = new(
            FindingSeverity.Critical, "review.same_finding", new Dictionary<string, string?>(), ["evidence"],
            new("src/a.cs", 1));

        await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested, [finding], null, cancellationToken);
        RecordReviewIterationResult second = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested, [finding], null, cancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(DiagnosticCodes.ReviewRepeatedFindings, second.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, state.Sprint.State);
        Assert.Equal("review_convergence", state.Sprint.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConvergenceBlockReasonIsNotLaunderedByAFindingRecordedInTheSameCall()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "review", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "review", started.AttemptId!, true,
            "sha256:" + new string('0', 64), [], [], cancellationToken);
        ReviewFindingDraft finding = new(
            FindingSeverity.Critical, "review.same_finding", new Dictionary<string, string?>(), ["evidence"],
            new("src/a.cs", 1));

        // The first iteration establishes the repeated-set history and, having no prior set to
        // repeat, is not itself a convergence trigger -- but the finding it records at/above the
        // floor blocks the (now node-complete, otherwise-settled) sprint with reason `finding`.
        await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested, [finding], null, cancellationToken);
        IReadOnlyList<Finding> openFindings =
            await scheduler.GetFindingsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        foreach (Finding open in openFindings.Where(item => item.Status == FindingStatus.Open))
        {
            await scheduler.ResolveFindingAsync(
                environment.ProjectRoot, sprintId, open.FindingId, FindingStatus.Resolved, cancellationToken);
        }

        // Resolving the last open finding walks the sprint back to `ReadyToFinalize` -- exactly
        // the state a second, repeated-set call must not let its own re-recorded finding launder
        // into an auto-recovering `finding` block.
        SprintWorkflowState readyToFinalize =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.ReadyToFinalize, readyToFinalize.Sprint.State);

        RecordReviewIterationResult second = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested, [finding], null, cancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(DiagnosticCodes.ReviewRepeatedFindings, second.DiagnosticCode);
        SprintWorkflowState blocked = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, blocked.Sprint.State);
        // Must stay `review_convergence` -- not silently reverted to `finding` by the very
        // finding this same call re-recorded, which would let resolving it alone (no explicit
        // resume_sprint/run_sprint) clear a gate that requires an operator decision.
        Assert.Equal("review_convergence", blocked.Sprint.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AConvergenceTriggerWhileAlreadyBlockedForAnotherReasonReportsFailureRatherThanFalseSuccess()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "review", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "review", started.AttemptId!, true,
            "sha256:" + new string('0', 64), [], [], cancellationToken);
        ReviewFindingDraft finding = new(
            FindingSeverity.Critical, "review.same_finding", new Dictionary<string, string?>(), ["evidence"],
            new("src/a.cs", 1));

        // Unlike `ConvergenceBlockReasonIsNotLaunderedByAFindingRecordedInTheSameCall`, the first
        // iteration's finding is deliberately left unresolved -- the sprint is still `Blocked`/
        // `finding` (not `ReadyToFinalize`) when the second, repeated-set call arrives. There is
        // no legal `Blocked -> Blocked` sprint transition to durably re-tag the reason, so this
        // must not silently report success while leaving the wrong, auto-recovering reason in
        // place (ADR 0015's documented limitation).
        await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested, [finding], null, cancellationToken);
        SprintWorkflowState blockedOnFinding =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, blockedOnFinding.Sprint.State);
        Assert.Equal("finding", blockedOnFinding.Sprint.BlockedReason);

        RecordReviewIterationResult second = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested, [finding], null, cancellationToken);

        Assert.False(second.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowEventConflict, second.DiagnosticCode);
        // The verdict itself is still durable (it governs the repeated-set history regardless).
        Assert.NotNull(second.Record);
        SprintWorkflowState stillBlockedOnFinding =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal("finding", stillBlockedOnFinding.Sprint.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AChangesRequestedFindingWithAnInvalidLocationLineIsRejectedAndConsumesNoIteration()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult rejected = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.ChangesRequested,
            [
                new(
                    FindingSeverity.Critical, "review.bad_line", new Dictionary<string, string?>(), ["evidence"],
                    new("src/a.cs", 0)),
            ],
            CompleteCoverage, cancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, rejected.DiagnosticCode);

        RecordReviewIterationResult accepted = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);
        Assert.Equal(1, accepted.Record!.Iteration);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DifferentConsecutiveExternalFindingSetsDoNotBlock()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested,
            [new(FindingSeverity.Critical, "review.finding_a", new Dictionary<string, string?>(), ["e"])],
            null, cancellationToken);
        RecordReviewIterationResult second = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.External,
            ReviewOutcome.ChangesRequested,
            [new(FindingSeverity.Critical, "review.finding_b", new Dictionary<string, string?>(), ["e"])],
            null, cancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(DiagnosticCodes.None, second.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheFourteenthIterationDoesNotBlockButTheFifteenthDoes()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult? last = null;
        for (int i = 0; i < 14; i++)
        {
            // A distinct finding set each time -- otherwise the repeated-set convergence gate
            // would trip first and this test would no longer isolate the iteration-limit gate.
            last = await scheduler.RecordReviewIterationAsync(
                environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
                ReviewOutcome.ChangesRequested,
                [new(FindingSeverity.Critical, $"review.finding_{i}", new Dictionary<string, string?>(), ["e"])],
                CompleteCoverage, cancellationToken);
        }

        Assert.Equal(14, last!.Record!.Iteration);
        Assert.Equal(DiagnosticCodes.None, last.DiagnosticCode);
        SprintWorkflowState stillRunning =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Running, stillRunning.Sprint.State);

        RecordReviewIterationResult fifteenth = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.ChangesRequested,
            [new(FindingSeverity.Critical, "review.finding_14", new Dictionary<string, string?>(), ["e"])],
            CompleteCoverage, cancellationToken);

        Assert.Equal(15, fifteenth.Record!.Iteration);
        Assert.Equal(DiagnosticCodes.ReviewIterationLimit, fifteenth.DiagnosticCode);
        SprintWorkflowState blocked = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, blocked.Sprint.State);
        Assert.Equal("review_convergence", blocked.Sprint.BlockedReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnApprovedVerdictOnTheFifteenthIterationDoesNotBlock()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult? last = null;
        for (int i = 0; i < 14; i++)
        {
            last = await scheduler.RecordReviewIterationAsync(
                environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
                ReviewOutcome.ChangesRequested,
                [new(FindingSeverity.Critical, $"review.finding_{i}", new Dictionary<string, string?>(), ["e"])],
                CompleteCoverage, cancellationToken);
        }

        Assert.Equal(14, last!.Record!.Iteration);

        // Review approves on what would be iteration 15 -- nothing is left to converge on, so this
        // must not trip the iteration-limit gate the way a fifteenth ChangesRequested verdict does.
        RecordReviewIterationResult fifteenth = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);

        Assert.Equal(15, fifteenth.Record!.Iteration);
        Assert.Equal(DiagnosticCodes.None, fifteenth.DiagnosticCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Running, state.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AChangesRequestedFindingWithNoEvidenceIsRejectedAndConsumesNoIteration()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult rejected = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.ChangesRequested,
            [new(FindingSeverity.Critical, "review.no_evidence", new Dictionary<string, string?>(), [])],
            CompleteCoverage, cancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, rejected.DiagnosticCode);

        RecordReviewIterationResult accepted = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);
        Assert.Equal(1, accepted.Record!.Iteration);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AChangesRequestedFindingWithAnInvalidMessageKeyIsRejectedAndConsumesNoIteration()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        RecordReviewIterationResult rejected = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.ChangesRequested,
            [new(FindingSeverity.Critical, "Review Finding!", new Dictionary<string, string?>(), ["evidence"])],
            CompleteCoverage, cancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Equal(DiagnosticCodes.WorkflowRecordInvalid, rejected.DiagnosticCode);

        RecordReviewIterationResult accepted = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.Approved, [], CompleteCoverage, cancellationToken);
        Assert.Equal(1, accepted.Record!.Iteration);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PinningTheFloorSuppressesTheIterationLimitGateAndAppliesCriticalRegardless()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = await CreateRunningSprintAsync(orchestrator, environment.ProjectRoot, cancellationToken);

        NodeActionResult pinned = await scheduler.PinReviewFloorAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, cancellationToken);
        Assert.True(pinned.Succeeded);

        RecordReviewIterationResult result = await scheduler.RecordReviewIterationAsync(
            environment.ProjectRoot, sprintId, "review", ReviewDimension.Implementation, ReviewerKind.Internal,
            ReviewOutcome.ChangesRequested,
            [new(FindingSeverity.Low, "review.low_finding", new Dictionary<string, string?>(), ["e"])],
            CompleteCoverage, cancellationToken);

        // Iteration 1 would normally floor at Low (admitting a Low finding); pinned-at-critical
        // means the same Low finding is dropped instead.
        Assert.Equal(1, result.Record!.Iteration);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
        IReadOnlyList<Finding> findings = await scheduler.GetFindingsAsync(
            environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal(FindingStatus.Dismissed, findings.Single().Status);
    }

    private static async Task<SprintId> CreateRunningSprintAsync(
        SprintOrchestrator orchestrator, string root, CancellationToken cancellationToken)
    {
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(root, 1, Guid.NewGuid(), Graph: ReviewGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, root, sprintId, cancellationToken);
        return sprintId;
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

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
