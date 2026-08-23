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
