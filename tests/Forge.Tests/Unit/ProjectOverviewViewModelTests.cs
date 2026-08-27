using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProjectOverviewViewModelTests
{
    private static ProjectOverviewViewModel Create(ForgeApplication application) =>
        new(application, new MainPageViewModel(Text(), application), Text());

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), System.Globalization.CultureInfo.GetCultureInfo("en"));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncSplitsActiveAndTerminalSprintsAndOrdersActiveOnesByThePlanRule()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ForgeApplication application = environment.Application;
        Guid running1 = await CreateAndRunAsync(application, environment.ProjectRoot, cancellationToken);
        Guid running2 = await CreateAndRunAsync(application, environment.ProjectRoot, cancellationToken);
        CreateSprintResult cancelledSprint = await application.CreateSprintAsync(environment.ProjectRoot, null, cancellationToken);
        await application.CancelSprintAsync(environment.ProjectRoot, cancelledSprint.SprintId!.Value, true, cancellationToken);
        ProjectOverviewViewModel viewModel = Create(application);

        ProjectOverviewSnapshot snapshot =
            await viewModel.LoadAsync(environment.ProjectRoot, null, cancellationToken);

        Assert.Equal([running2, running1], [.. snapshot.ActiveSprints.Select(card => card.SprintId)]);
        Assert.All(snapshot.ActiveSprints, card => Assert.False(card.Terminal));
        ProjectOverviewSprintCard history = Assert.Single(snapshot.RecentHistory);
        Assert.Equal(cancelledSprint.SprintId!.Value, history.SprintId);
        Assert.True(history.Terminal);
        Assert.Equal(environment.ProjectRoot, snapshot.Root);
    }

    private static async Task<Guid> CreateAndRunAsync(
        ForgeApplication application, string root, CancellationToken cancellationToken)
    {
        CreateSprintResult created = await application.CreateSprintAsync(root, null, cancellationToken);
        Guid sprintId = created.SprintId!.Value;
        await application.RunSprintAsync(root, sprintId, cancellationToken);
        await application.RunSprintAsync(root, sprintId, cancellationToken);
        return sprintId;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncUsesTheAliasWhenGiven()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectOverviewViewModel viewModel = Create(environment.Application);

        ProjectOverviewSnapshot snapshot =
            await viewModel.LoadAsync(environment.ProjectRoot, "My Alias", cancellationToken);

        Assert.Equal("My Alias", snapshot.DisplayName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncExposesSuggestedActionsFromTheSharedAvailableActionProjection()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        // Not initialized yet: initialize_project is the expected suggested action, matching
        // `forge next`'s own behavior for a fresh root.
        ProjectOverviewViewModel viewModel = Create(environment.Application);

        ProjectOverviewSnapshot snapshot =
            await viewModel.LoadAsync(environment.ProjectRoot, null, cancellationToken);

        Assert.Contains(
            snapshot.SuggestedActions, action => action.ActionId == ForgeApplication.InitializeProjectAction);
        Assert.True(snapshot.InitializeEnabled);
    }

    /// <summary>ADR 0057, end to end through the Desktop presentation layer: a titled sprint's card
    /// carries its own title, and an untitled one (every sprint that already existed) falls back to
    /// the localized "Sprint {N}" rather than rendering blank. Sidebar visuals are deliberately not
    /// asserted -- that slice is separate.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SprintCardsCarryTheFrozenTitleOrTheLocalizedUntitledFallback()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ForgeApplication application = environment.Application;
        Guid titled = (await application
            .CreateSprintAsync(environment.ProjectRoot, "Close the parity gap", cancellationToken)).SprintId!.Value;
        Guid untitled = (await application
            .CreateSprintAsync(environment.ProjectRoot, null, cancellationToken)).SprintId!.Value;
        ProjectOverviewViewModel viewModel = Create(application);

        ProjectOverviewSnapshot snapshot =
            await viewModel.LoadAsync(environment.ProjectRoot, null, cancellationToken);

        ProjectOverviewSprintCard titledCard =
            Assert.Single(snapshot.ActiveSprints, card => card.SprintId == titled);
        ProjectOverviewSprintCard untitledCard =
            Assert.Single(snapshot.ActiveSprints, card => card.SprintId == untitled);
        Assert.Equal("Close the parity gap", titledCard.DisplayTitle);
        Assert.Equal($"Sprint {untitledCard.CreationSequence}", untitledCard.DisplayTitle);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintAsyncDelegatesToMainPageViewModel()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectOverviewViewModel viewModel = Create(environment.Application);

        string message = await viewModel.CreateSprintAsync(environment.ProjectRoot, null, cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(message));
        ProjectSnapshot snapshot =
            await environment.Application.GetProjectSnapshotAsync(environment.ProjectRoot, cancellationToken);
        Assert.Single(snapshot.Sprints);
    }
}
