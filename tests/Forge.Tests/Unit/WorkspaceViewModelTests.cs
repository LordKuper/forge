using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsyncWithAnEmptyCatalogSelectsTheEmptyRoute()
    {
        using TestEnvironment environment = new();
        WorkspaceViewModel viewModel = new(environment.Resolve<ProjectCatalogStore>(), environment.Application);

        await viewModel.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRoute.Empty, viewModel.Route);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsyncWithNoSavedRouteOpensTheProjectOverview()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(added.Succeeded);
        WorkspaceViewModel viewModel = new(catalog, environment.Application);

        await viewModel.RestoreAsync(cancellationToken);

        Assert.Equal(
            WorkspaceRoute.ToProjectOverview(added.Entry!.ProjectId, environment.ProjectRoot), viewModel.Route);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsyncRestoresTheSavedProjectSettingsRoute()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        await catalog.SelectAsync(added.Entry!.ProjectId, null, RouteTokens.ProjectSettings, cancellationToken);
        WorkspaceViewModel viewModel = new(catalog, environment.Application);

        await viewModel.RestoreAsync(cancellationToken);

        Assert.Equal(
            WorkspaceRoute.ToProjectSettings(added.Entry.ProjectId, environment.ProjectRoot), viewModel.Route);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsyncRestoresTheLastSprintWhenItStillExists()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CreateSprintResult sprint = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        await catalog.SelectAsync(added.Entry!.ProjectId, sprint.SprintId!.Value, null, cancellationToken);
        WorkspaceViewModel viewModel = new(catalog, environment.Application);

        await viewModel.RestoreAsync(cancellationToken);

        Assert.Equal(
            WorkspaceRoute.ToSprintWorkspace(added.Entry.ProjectId, environment.ProjectRoot, sprint.SprintId.Value),
            viewModel.Route);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsyncFallsBackToOverviewWhenTheLastSprintNoLongerExists()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        await catalog.SelectAsync(added.Entry!.ProjectId, Guid.NewGuid(), null, cancellationToken);
        WorkspaceViewModel viewModel = new(catalog, environment.Application);

        await viewModel.RestoreAsync(cancellationToken);

        Assert.Equal(
            WorkspaceRoute.ToProjectOverview(added.Entry.ProjectId, environment.ProjectRoot), viewModel.Route);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NavigateAsyncPersistsTheSelectionForTheNextRestore()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        WorkspaceViewModel viewModel = new(catalog, environment.Application);
        int routeChangedCalls = 0;
        viewModel.RouteChanged += (_, _) => routeChangedCalls++;

        await viewModel.NavigateAsync(
            WorkspaceRoute.ToProjectSettings(added.Entry!.ProjectId, environment.ProjectRoot), cancellationToken);

        Assert.Equal(1, routeChangedCalls);
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken);
        ProjectCatalogEntry entry = Assert.Single(listing.Entries);
        Assert.Equal(RouteTokens.ProjectSettings, entry.LastRoute);
        Assert.Null(entry.LastSelectedSprintId);
    }
}
