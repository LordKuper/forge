using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SidebarViewModelTests
{
    private static SurfaceTextProvider Text() => new(new ResourceLocalizationCatalog(), "en");

    private static SurfaceTextProvider TextRu() => new(new ResourceLocalizationCatalog(), "ru");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncOrdersActiveSprintsByThePlanSection41Rule()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ForgeApplication application = environment.Application;

        // Two running sprints and one still in Draft (rank 4, sorts last): the running sprints
        // must sort ahead of the draft one and, within that bucket, by descending creation
        // sequence. The full bucket ordering (attention/running/paused/blocked-or-failed/other) is
        // covered directly and exhaustively by SprintOrderingRankTests; this proves
        // SidebarViewModel actually wires that rule to real backend state.
        Guid running1 = await CreateAndRunAsync(application, environment.ProjectRoot, cancellationToken);
        Guid running2 = await CreateAndRunAsync(application, environment.ProjectRoot, cancellationToken);
        Guid draft = (await application.CreateSprintAsync(environment.ProjectRoot, cancellationToken)).SprintId!.Value;

        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel = new(catalog, application, new FakeFolderPicker(), Text());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        SidebarProjectItem project = Assert.Single(snapshot.Projects);
        Assert.Equal(
            [running2, running1, draft], [.. project.ActiveSprints.Select(sprint => sprint.SprintId)]);
    }

    private static async Task<Guid> CreateAndRunAsync(
        ForgeApplication application, string root, CancellationToken cancellationToken)
    {
        CreateSprintResult created = await application.CreateSprintAsync(root, cancellationToken);
        Guid sprintId = created.SprintId!.Value;
        await application.RunSprintAsync(root, sprintId, cancellationToken);
        await application.RunSprintAsync(root, sprintId, cancellationToken);
        return sprintId;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncCountsTerminalSprintsAsHistoryWithoutListingThemAsActive()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        SprintSnapshot sprint = (await orchestrator.GetSprintAsync(
            environment.ProjectRoot, created.SprintId!, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, created.SprintId!, sprint.Version,
                SprintOrchestrator.CancelSprintKey(sprint)),
            cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel = new(catalog, environment.Application, new FakeFolderPicker(), Text());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        SidebarProjectItem project = Assert.Single(snapshot.Projects);
        Assert.Empty(project.ActiveSprints);
        Assert.Equal(1, project.HistoryCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddProjectAsyncUsesTheManualPathWithoutInvokingThePicker()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        FakeFolderPicker picker = new();
        SidebarViewModel viewModel =
            new(environment.Resolve<ProjectCatalogStore>(), environment.Application, picker, Text());

        AddProjectResult result = await viewModel.AddProjectAsync(environment.ProjectRoot, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, picker.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddProjectAsyncFallsBackToThePickerWhenNoManualPathIsGiven()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        FakeFolderPicker picker = new(environment.ProjectRoot);
        SidebarViewModel viewModel =
            new(environment.Resolve<ProjectCatalogStore>(), environment.Application, picker, Text());

        AddProjectResult result = await viewModel.AddProjectAsync(null, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, picker.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddProjectAsyncReportsCancelledWhenThePickerIsDismissed()
    {
        using TestEnvironment environment = new();
        FakeFolderPicker picker = new(nextResult: null);
        SidebarViewModel viewModel =
            new(environment.Resolve<ProjectCatalogStore>(), environment.Application, picker, Text());

        AddProjectResult result = await viewModel.AddProjectAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(AddProjectResult.Cancelled, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveProjectAsyncRemovesTheCatalogEntry()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel = new(catalog, environment.Application, new FakeFolderPicker(), Text());

        string diagnosticCode = await viewModel.RemoveProjectAsync(added.Entry!.ProjectId, cancellationToken);

        Assert.Equal(DiagnosticCodes.None, diagnosticCode);
        Assert.Empty((await catalog.ListAsync(cancellationToken)).Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncLocalizesTheStatusRowAndAccessibleProjectNameInsteadOfHardcodedEnglish()
    {
        // PR #98 review finding 8: "available"/"unavailable", "active sprints", "need attention",
        // and both provider-ready phrases were hardcoded English literals in this neutral view-model
        // -- under language.ui = ru they must resolve through the Russian catalog instead.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider ru = TextRu();
        SidebarViewModel viewModel = new(catalog, environment.Application, new FakeFolderPicker(), ru);

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        SidebarProjectItem project = Assert.Single(snapshot.Projects);
        string expectedAvailability =
            ru.Resolve(project.Available ? MessageKeys.SidebarProjectAvailable : MessageKeys.SidebarProjectUnavailable);
        Assert.Contains(expectedAvailability, project.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(ru.Resolve(MessageKeys.SidebarActiveSprintsLabel), project.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(ru.Resolve(MessageKeys.SidebarAttentionNeededLabel), project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("available", project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("active sprints", project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("need attention", project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("providers ready", snapshot.Status.ProviderSummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("providers are ready", snapshot.Status.ProviderAccessibleText, StringComparison.Ordinal);
    }

    /// <summary>Plan 12.6: the quota state must have both text and an accessible name, never color
    /// alone. ADR 0052 found no provider integration exposes a verified quota signal, so this must
    /// truthfully resolve to the "unknown" state rather than fabricating readiness.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsQuotaAsUnknownWithBothTextAndAnAccessibleName()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider en = Text();
        SidebarViewModel viewModel = new(catalog, environment.Application, new FakeFolderPicker(), en);

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(en.Resolve(MessageKeys.QuotaStatusUnavailable), snapshot.Status.QuotaStatusText);
        Assert.Equal(en.Resolve(MessageKeys.QuotaStatusUnknownAccessible), snapshot.Status.QuotaAccessibleText);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Status.QuotaAccessibleText));
    }
}
