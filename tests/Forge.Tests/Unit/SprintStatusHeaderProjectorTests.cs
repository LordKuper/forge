using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Plan section 4.3's sticky status header. Confirms it is built from data the Host already
/// computed for a running sprint (never a locally re-derived "current stage") and degrades to a
/// plain node count for a terminal sprint, which <see cref="ProjectWorkspaceSummary.ActiveSprints"/>
/// never includes.
/// </summary>
public sealed class SprintStatusHeaderProjectorTests
{
    private static readonly IReadOnlyList<NodeDefinition> TwoNodeGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])];

    private static readonly IReadOnlyList<NodeDefinition> ImplementationNodeGraph =
        [new("a", NodeKind.Work, [], NodeRole.Implementation)];

    private static SurfaceText Text() =>
        new(new ResourceLocalizationCatalog(), System.Globalization.CultureInfo.GetCultureInfo("en"));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARunningSprintsHeaderReflectsTheWorkspaceSummarysOwnStageAndProgress()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        RecordFindingResult recorded = await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Medium, "finding.example",
            new Dictionary<string, string?>(), ["observed in review"], null, null, cancellationToken);
        Assert.True(recorded.Succeeded);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, sprintId.Value, cancellationToken);
        ProjectWorkspaceSummary summary = await environment.Application.GetWorkspaceSummaryAsync(
            environment.ProjectRoot, cancellationToken);

        SprintStatusHeaderData header = SprintStatusHeaderProjector.Build("My Project", snapshot, summary, Text());

        SprintWorkspaceSummary expectedSummary = Assert.Single(summary.ActiveSprints, s => s.SprintId == sprintId.Value);
        Assert.Equal("My Project", header.ProjectDisplayName);
        Assert.Equal(1, header.SprintSequence);
        Assert.Equal("draft", header.SprintStateText);
        Assert.Equal(expectedSummary.CurrentStageId, header.CurrentStageId);
        Assert.Equal(expectedSummary.StagesCompleted, header.StagesCompleted);
        Assert.Equal(expectedSummary.StagesTotal, header.StagesTotal);
        Assert.Equal(1, header.OpenFindingsCount);
        Assert.Contains(sprintId.Value.ToString("D"), header.DetailsText, StringComparison.Ordinal);
        Assert.Contains(environment.ProjectRoot, header.DetailsText, StringComparison.Ordinal);
    }

    /// <summary>Plan section 12.3: once a model-bearing attempt is actually running, the header
    /// renders its real routed provider/model instead of the "not yet available" placeholder --
    /// closing the gap <see cref="SprintStatusHeaderProjector"/>'s own doc comment used to describe
    /// as a structural absence in <see cref="Forge.Domain.AttemptSnapshot"/>.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARunningAttemptsKnownProviderAndModelReplaceThePlaceholder()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ImplementationNodeGraph), cancellationToken))
            .SprintId!;
        SprintSnapshot draft = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Version, SprintOrchestrator.RunSprintKey(draft)),
            cancellationToken);
        Assert.True(toReady.Succeeded);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        Assert.True(toRunning.Succeeded);
        SprintDefinition definition =
            (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        ExecutionProfile expected = definition.ExecutionProfiles[ExecutionPhase.Implementation];
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, sprintId.Value, cancellationToken);
        ProjectWorkspaceSummary summary = await environment.Application.GetWorkspaceSummaryAsync(
            environment.ProjectRoot, cancellationToken);

        SprintStatusHeaderData header = SprintStatusHeaderProjector.Build("My Project", snapshot, summary, Text());

        Assert.Equal($"{expected.Provider} / {expected.Model}", header.ActiveProviderModelText);
    }

    /// <summary>Regression: <c>SupersedeAttemptAsync</c> appended its replacement's
    /// <c>workflow.attempt_created</c> without provider/model, and <c>StartAttemptAsync</c>'s own
    /// append for the same aggregate always lands as a version conflict (the replacement already
    /// exists at version 1) that is swallowed as the benign-resume case -- silently discarding the
    /// routed provider/model it had just computed. Every human-superseded attempt's replacement
    /// therefore showed the placeholder for its entire lifetime, even while genuinely running with a
    /// known provider/model. Fixed by having <c>SupersedeAttemptAsync</c> carry the superseded
    /// attempt's own recorded provider/model onto the replacement's creation event (the one event
    /// that actually lands), the same way it already carries <c>BaseCommit</c> forward.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASupersededAttemptsReplacementShowsRealProviderAndModelOnceStarted()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ImplementationNodeGraph), cancellationToken))
            .SprintId!;
        SprintSnapshot draft = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Version, SprintOrchestrator.RunSprintKey(draft)),
            cancellationToken);
        Assert.True(toReady.Succeeded);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        Assert.True(toRunning.Succeeded);
        SprintDefinition definition =
            (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        ExecutionProfile expected = definition.ExecutionProfiles[ExecutionPhase.Implementation];

        StartAttemptResult original =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(original.Succeeded);
        SprintWorkflowState afterOriginalStart =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot originalAttempt = afterOriginalStart.Attempts[original.AttemptId!.Value.ToString("D")];

        CompleteAttemptResult superseded = await scheduler.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, originalAttempt.Id, originalAttempt.Version,
            SprintScheduler.SupersedeAttemptKey(sprintId, originalAttempt), confirmed: true,
            "Try a different approach.", cancellationToken);
        Assert.True(superseded.Succeeded, $"diag={superseded.DiagnosticCode}");

        StartAttemptResult replacement = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", superseded.Node!.Version, cancellationToken);
        Assert.True(replacement.Succeeded, $"diag={replacement.DiagnosticCode}");
        Assert.NotEqual(originalAttempt.Id, replacement.AttemptId);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, sprintId.Value, cancellationToken);
        ProjectWorkspaceSummary summary = await environment.Application.GetWorkspaceSummaryAsync(
            environment.ProjectRoot, cancellationToken);

        SprintStatusHeaderData header = SprintStatusHeaderProjector.Build("My Project", snapshot, summary, Text());

        Assert.Equal($"{expected.Provider} / {expected.Model}", header.ActiveProviderModelText);
    }

    /// <summary>The placeholder fallback branch (activated when a role never routes -- e.g. a
    /// Generic-role node, which nothing ever assigns an execution profile to) had no assertion
    /// anywhere in the suite: a regression rendering an empty string, a bare " / ", or a stale
    /// attempt's values would have passed the entire existing suite unnoticed.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANonModelBearingAttemptsHeaderShowsTheExactPlaceholderText()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]), cancellationToken))
            .SprintId!;
        SprintSnapshot draft = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Version, SprintOrchestrator.RunSprintKey(draft)),
            cancellationToken);
        Assert.True(toReady.Succeeded);
        SprintTransitionResult toRunning = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        Assert.True(toRunning.Succeeded);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, sprintId.Value, cancellationToken);
        ProjectWorkspaceSummary summary = await environment.Application.GetWorkspaceSummaryAsync(
            environment.ProjectRoot, cancellationToken);

        SprintStatusHeaderData header = SprintStatusHeaderProjector.Build("My Project", snapshot, summary, Text());

        SurfaceText text = Text();
        Assert.Equal(
            text.Resolve(MessageKeys.SprintStatusHeaderProviderModelUnavailable), header.ActiveProviderModelText);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATerminalSprintsHeaderFallsBackToACountRatherThanThrowing()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TwoNodeGraph), cancellationToken)).SprintId!;
        SprintSnapshot draft = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        SprintTransitionResult cancelled = await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, sprintId, draft.Version, SprintOrchestrator.CancelSprintKey(draft)),
            cancellationToken);
        Assert.True(cancelled.Succeeded);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot, SnapshotDetail.Full, sprintId.Value, cancellationToken);
        ProjectWorkspaceSummary summary = await environment.Application.GetWorkspaceSummaryAsync(
            environment.ProjectRoot, cancellationToken);

        SprintStatusHeaderData header = SprintStatusHeaderProjector.Build("My Project", snapshot, summary, Text());

        Assert.DoesNotContain(summary.ActiveSprints, s => s.SprintId == sprintId.Value);
        Assert.Equal("cancelled", header.SprintStateText);
        Assert.Equal(2, header.StagesTotal);
    }
}
