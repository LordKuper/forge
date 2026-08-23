using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SprintWorkspaceViewModelTests
{
    private static SurfaceText Text() =>
        new(new ResourceLocalizationCatalog(), System.Globalization.CultureInfo.GetCultureInfo("en"));

    private static SprintWorkspaceViewModel BuildViewModel(TestEnvironment environment, IForgeMutations mutations)
    {
        MainPageViewModel legacy =
            new(Text(), environment.Application, (_, _) => Task.FromResult(mutations));
        return new(legacy, environment.Application, environment.Resolve<ProjectCatalogStore>(), (_, _) => Task.FromResult(mutations), Text());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindActiveAttemptIdReturnsTheRunningAttemptNeverAnyOtherState()
    {
        SprintDetails details = new(
            Guid.NewGuid(),
            [],
            [
                new(Guid.NewGuid().ToString("D"), "succeeded"),
                new(Guid.NewGuid().ToString("D"), "running"),
            ],
            [],
            [],
            [],
            new(0, null));

        Guid? active = SprintWorkspaceViewModel.FindActiveAttemptId(details);

        Assert.NotNull(active);
        Assert.Equal("running", details.Attempts.Single(attempt => Guid.Parse(attempt.Id) == active).State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindActiveAttemptIdReturnsNullWhenNothingIsRunning()
    {
        SprintDetails details = new(Guid.NewGuid(), [], [new(Guid.NewGuid().ToString("D"), "succeeded")], [], [], [], new(0, null));

        Assert.Null(SprintWorkspaceViewModel.FindActiveAttemptId(details));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindActiveAttemptIdReturnsNullWhenNoDetailsAreAvailable() =>
        Assert.Null(SprintWorkspaceViewModel.FindActiveAttemptId(null));

    [Fact]
    [Trait("Category", "Unit")]
    public void HasPendingGateIsTrueOnlyWhenANodeIsAwaitingHuman()
    {
        SprintDetails awaiting = new(
            Guid.NewGuid(), [new("human_approval", "awaiting_human")], [], [], [], [], new(0, null));
        SprintDetails none = new(Guid.NewGuid(), [new("human_approval", "ready")], [], [], [], [], new(0, null));

        Assert.True(SprintWorkspaceViewModel.HasPendingGate(awaiting));
        Assert.False(SprintWorkspaceViewModel.HasPendingGate(none));
        Assert.False(SprintWorkspaceViewModel.HasPendingGate(null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncForwardsTheRouteSprintIdAsAGuidStringToTheLegacyViewModel()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintWorkspaceViewModel viewModel = BuildViewModel(environment, mutations);
        Guid sprintId = Guid.NewGuid();

        await viewModel.ResolveGateAsync(
            environment.ProjectRoot, sprintId, "custom_node", true, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.ResolveGateCalls);
        Assert.Equal(sprintId, mutations.LastGateSprintId);
        Assert.Equal("custom_node", mutations.LastGateNodeId);
        Assert.True(mutations.LastGateApproved);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncForwardsTheRouteSprintIdAsAGuidStringToTheLegacyViewModel()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintWorkspaceViewModel viewModel = BuildViewModel(environment, mutations);
        Guid sprintId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId, attemptId.ToString("D"), "do this instead", true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.SupersedeAttemptCalls);
        Assert.Equal(sprintId, mutations.LastSupersedeSprintId);
        Assert.Equal(attemptId, mutations.LastSupersedeAttemptId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelSprintAsyncForwardsConfirmationAndSprintId()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        SprintWorkspaceViewModel viewModel = BuildViewModel(environment, mutations);
        Guid sprintId = Guid.NewGuid();

        await viewModel.CancelSprintAsync(environment.ProjectRoot, sprintId, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.CancelSprintCalls);
        Assert.True(mutations.LastCancelSprintConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GatePromptNamesTheRouteSprintIdRatherThanTheActiveSprintPlaceholder()
    {
        using TestEnvironment environment = new();
        SprintWorkspaceViewModel viewModel = BuildViewModel(environment, new FakeForgeMutations());
        Guid sprintId = Guid.NewGuid();

        string prompt = viewModel.GatePrompt(sprintId, null);

        Assert.Contains(sprintId.ToString("D"), prompt, StringComparison.Ordinal);
    }
}
