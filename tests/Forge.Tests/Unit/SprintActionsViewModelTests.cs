using System.Globalization;
using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Slice 6's stop/stage-transition renderer. The confirmation-bypass bug class Slice 5's review
/// caught twice (a hardcoded <c>confirmed: true</c> silently defeating a real dialog answer) is the
/// primary risk this slice repeats for two new destructive actions, so every mutation-forwarding test
/// here proves both a <see langword="true"/> and a <see langword="false"/> caller answer reach the
/// mutation unchanged -- a test that would fail immediately if either method replaced its
/// <c>confirmed</c> parameter with a literal.
/// </summary>
public sealed class SprintActionsViewModelTests
{
    private static SurfaceText Text() =>
        new(new ResourceLocalizationCatalog(), CultureInfo.GetCultureInfo("en"));

    private static AvailableAction StopAction(Guid sprintId, string nodeId, Guid attemptId) =>
        new(
            AvailableAction.ContractVersion,
            AvailableActionProjector.StopCurrentOperationActionId,
            "workspace_action.stop_current_operation",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new(null, sprintId, nodeId, attemptId, null),
            1,
            SafetyClass.HumanApproval,
            true,
            [],
            true,
            [],
            Guid.NewGuid(),
            StaleBehavior.RejectWithoutSideEffect);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StopAsyncForwardsAFalseDialogAnswerRatherThanAHardcodedTrue()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations), Text());
        AvailableAction stop = StopAction(Guid.NewGuid(), "implementation", Guid.NewGuid());

        await actions.StopAsync(environment.ProjectRoot, stop, confirmed: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.StopCurrentOperationCalls);
        Assert.False(mutations.LastStopConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StopAsyncForwardsATrueDialogAnswerAndTheExactTarget()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations), Text());
        Guid sprintId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        AvailableAction stop = StopAction(sprintId, "implementation", attemptId);

        await actions.StopAsync(environment.ProjectRoot, stop, confirmed: true, TestContext.Current.CancellationToken);

        Assert.True(mutations.LastStopConfirmed);
        Assert.Equal(sprintId, mutations.LastStopSprintId);
        Assert.Equal(attemptId, mutations.LastStopAttemptId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StopPromptNamesTheExactNodeAndAttemptRatherThanAGenericQuestion()
    {
        using TestEnvironment environment = new();
        Guid attemptId = Guid.NewGuid();
        AvailableAction stop = StopAction(Guid.NewGuid(), "implementation", attemptId);
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => throw new InvalidOperationException(), Text());

        string prompt = actions.StopPrompt(stop);

        Assert.Contains("implementation", prompt, StringComparison.Ordinal);
        Assert.Contains(attemptId.ToString("D"), prompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveAsyncForwardsAFalseDialogAnswerRatherThanAHardcodedTrue()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations), Text());
        Guid sprintId = Guid.NewGuid();
        StageTransitionAssessment assessment = FakeAssessment(sprintId, allowed: true, confirmationRequired: true);

        await actions.MoveAsync(
            environment.ProjectRoot, sprintId, assessment, "reason", confirmed: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.MoveSprintToStageCalls);
        Assert.False(mutations.LastMoveStageConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MoveAsyncForwardsATrueDialogAnswerAndTheFreshAssessmentsOwnTokenAndVersion()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations), Text());
        Guid sprintId = Guid.NewGuid();
        StageTransitionAssessment assessment = FakeAssessment(sprintId, allowed: true, confirmationRequired: true);

        await actions.MoveAsync(
            environment.ProjectRoot, sprintId, assessment, "reason", confirmed: true, TestContext.Current.CancellationToken);

        Assert.True(mutations.LastMoveStageConfirmed);
        Assert.Equal("target", mutations.LastMoveStageTargetStageId);
        Assert.Equal(assessment.ExpectedStateVersion, mutations.LastMoveStageExpectedStateVersion);
        Assert.Equal(assessment.AssessmentToken, mutations.LastMoveStageAssessmentToken);
        Assert.Equal("reason", mutations.LastMoveStageReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MovePromptSurfacesTheAssessmentsOwnBlockersAndConsequencesRatherThanRecomputingThem()
    {
        using TestEnvironment environment = new();
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => throw new InvalidOperationException(), Text());
        StageTransitionAssessment assessment = new(
            true,
            DiagnosticCodes.None,
            new SprintId(Guid.NewGuid()),
            "implementation",
            "planning",
            StageTransitionDirection.Rewind,
            true,
            [new("no_active_operation", true, "stage_transition.no_active_operation", new Dictionary<string, string?>())],
            [new("no_blocking_findings", false, "stage_transition.no_blocking_findings", new Dictionary<string, string?>())],
            new(false, null, null, false),
            new(["planning", "implementation"], [Guid.NewGuid(), Guid.NewGuid()], 2, 1, 0),
            true,
            42,
            new StageRevision(1).Next(),
            "token");

        string prompt = actions.MovePrompt(assessment);

        Assert.Contains("implementation", prompt, StringComparison.Ordinal);
        Assert.Contains("planning", prompt, StringComparison.Ordinal);
        Assert.Contains("no_active_operation", prompt, StringComparison.Ordinal);
        // The unsatisfied prerequisite's own message key is rendered verbatim -- never a locally
        // recomputed explanation of why it is unsatisfied (ADR 0046: the UI may explain, never
        // calculate).
        Assert.Contains("no_blocking_findings", prompt, StringComparison.Ordinal);
        Assert.Contains("stage_transition.no_blocking_findings", prompt, StringComparison.Ordinal);
        // Rewind consequences (what would be superseded) come straight from the assessment's own
        // Supersession field.
        Assert.Contains("2", prompt, StringComparison.Ordinal);
    }

    // PR #101 review finding 3: an unconverged rewind's own assessment reports Allowed=false, but --
    // unlike every other blocked target -- confirming this specific row actually resumes and finishes
    // it (StageTransitionCoordinator.MoveAsync's own PendingRewindTargetStageId bypass). The prompt
    // must say so, not claim the move is impossible the way MoveToStageBlockedCannotProceed does.
    [Fact]
    [Trait("Category", "Unit")]
    public void MovePromptTellsTheUserAnInProgressRewindWillResumeRatherThanClaimingTheMoveIsBlocked()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        SprintActionsViewModel actions = new(
            environment.Application, (_, _) => throw new InvalidOperationException(), text);
        StageTransitionAssessment assessment = new(
            true,
            DiagnosticCodes.StageTransitionRewindInProgress,
            new SprintId(Guid.NewGuid()),
            "implementation",
            "planning",
            StageTransitionDirection.Rewind,
            false,
            [],
            [],
            new(false, null, null, false),
            null,
            false,
            7,
            default,
            null);

        string prompt = actions.MovePrompt(assessment);

        Assert.Contains(text.Resolve(MessageKeys.MoveToStageResumeRewindPrompt), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Resolve(MessageKeys.MoveToStageBlockedCannotProceed), prompt, StringComparison.Ordinal);
    }

    private static StageTransitionAssessment FakeAssessment(Guid sprintId, bool allowed, bool confirmationRequired) =>
        new(
            true,
            DiagnosticCodes.None,
            new SprintId(sprintId),
            "source",
            "target",
            StageTransitionDirection.Advance,
            allowed,
            [],
            [],
            new(false, null, null, false),
            null,
            confirmationRequired,
            7,
            default,
            "assessment-token");
}
