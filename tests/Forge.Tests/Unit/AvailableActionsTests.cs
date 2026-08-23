using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 6.4's versioned contextual-action projection, computed from a sprint's
/// actual current state rather than a parallel policy engine. Confirms real behavior for both the
/// project-level (no sprint selected) and sprint-scoped shapes before the smallest risk-based tests
/// were added.</summary>
public sealed class AvailableActionsTests
{
    private static readonly IReadOnlyList<NodeDefinition> TwoNodeGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUninitializedProjectOffersInitializeAsAProjectLevelAction()
    {
        using TestEnvironment environment = new();

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, null, TestContext.Current.CancellationToken);

        AvailableAction initialize =
            Assert.Single(actions, action => action.ActionId == ForgeApplication.InitializeProjectAction);
        Assert.Equal(environment.ProjectRoot, initialize.Target.ProjectRoot);
        Assert.True(initialize.Enabled);
        Assert.Empty(initialize.Blockers);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADraftSprintOffersRunButNeitherResumeNorStop()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        Assert.Contains(actions, action => action.ActionId == AvailableActionProjector.RunSprintActionId);
        Assert.DoesNotContain(actions, action => action.ActionId == AvailableActionProjector.ResumeSprintActionId);
        Assert.DoesNotContain(
            actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
        Assert.Contains(actions, action => action.ActionId == AvailableActionProjector.CancelSprintActionId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARunningSprintWithAnActiveAttemptOffersStopTargetingTheExactAttempt()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        AvailableAction stop =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
        Assert.Equal(sprintId.Value, stop.Target.SprintId);
        Assert.Equal("a", stop.Target.NodeId);
        Assert.Equal(started.AttemptId!.Value, stop.Target.AttemptId);
        Assert.Equal(SafetyClass.HumanApproval, stop.SafetyClass);
        Assert.True(stop.ConfirmationRequired);

        // "b" depends on "a" (not yet succeeded), so moving straight to "b" must report a blocker,
        // never silently allow skipping ahead (plan section 8.3: "never fabricates completion").
        AvailableAction moveToB = Assert.Single(
            actions, action => action.ActionId == $"{AvailableActionProjector.MoveToStageActionPrefix}b");
        Assert.False(moveToB.Enabled);
        Assert.NotEmpty(moveToB.Blockers);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFailedSprintOffersResumeAndNoActiveOperation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        string sampleDigest = "sha256:" + new string('0', 64);
        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", nodeVersion, cancellationToken);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, sampleDigest, [], [],
                cancellationToken);
            nodeVersion = completed.Node!.Version;
        }

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);

        Assert.Contains(actions, action => action.ActionId == AvailableActionProjector.ResumeSprintActionId);
        Assert.DoesNotContain(
            actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
    }

    // Round-trip regression (PR #97 review): the whole point of ExpectedStateVersion/IdempotencyKey
    // on an AvailableAction is that a client can feed them straight back into the exact mutation the
    // action describes and have it succeed. Earlier, run/resume/cancel_sprint reported
    // SprintWorkflowState.LastSequence (the journal position) while SprintOrchestrator.TransitionAsync
    // validates freshness against SprintSnapshot.Version (the sprint aggregate's own transition
    // count) -- coincidentally equal only for a freshly created sprint, diverging as soon as any
    // non-sprint-aggregate event (a node/attempt transition) is appended. These tests assert against
    // the real mutation, not just a plausible-looking version number, so they would have caught it.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARunSprintActionsReportedVersionAndKeySucceedAgainstTheRealMutation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction run =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.RunSprintActionId);

        SprintTransitionResult result = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, run.ExpectedStateVersion, run.IdempotencyKey),
            cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACancelSprintActionsReportedVersionAndKeySucceedAgainstTheRealMutation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        // Advances SprintSnapshot.Version away from LastSequence by appending a non-sprint-aggregate
        // (node) event, reproducing the exact divergence the reviewer's probe found.
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction cancel =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.CancelSprintActionId);

        SprintTransitionResult result = await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, cancel.ExpectedStateVersion, cancel.IdempotencyKey),
            cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AResumeSprintActionsReportedVersionAndKeySucceedAgainstTheRealMutation()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        string sampleDigest = "sha256:" + new string('0', 64);
        long nodeVersion = 2;
        for (int attempt = 0; attempt < SprintScheduler.MaxAutomaticRetries + 1; attempt++)
        {
            StartAttemptResult started = await scheduler.StartAttemptAsync(
                environment.ProjectRoot, sprintId, "a", nodeVersion, cancellationToken);
            CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
                environment.ProjectRoot, sprintId, "a", started.AttemptId!, false, sampleDigest, [], [],
                cancellationToken);
            nodeVersion = completed.Node!.Version;
        }

        IReadOnlyList<AvailableAction> actions = await environment.Application.GetAvailableActionsAsync(
            environment.ProjectRoot, sprintId.Value, cancellationToken);
        AvailableAction resume =
            Assert.Single(actions, action => action.ActionId == AvailableActionProjector.ResumeSprintActionId);

        SprintTransitionResult result = await orchestrator.ResumeSprintAsync(
            new(environment.ProjectRoot, sprintId, resume.ExpectedStateVersion, resume.IdempotencyKey),
            cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
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
