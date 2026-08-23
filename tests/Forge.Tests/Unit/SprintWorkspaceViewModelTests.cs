using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SprintWorkspaceViewModelTests
{
    private static SurfaceText Text() =>
        new(new ResourceLocalizationCatalog(), System.Globalization.CultureInfo.GetCultureInfo("en"));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncForwardsTheRouteSprintIdAsAGuidStringToTheLegacyViewModel()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel legacy =
            new(Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));
        SprintWorkspaceViewModel viewModel = new(legacy);
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
        MainPageViewModel legacy =
            new(Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));
        SprintWorkspaceViewModel viewModel = new(legacy);
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
        MainPageViewModel legacy =
            new(Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));
        SprintWorkspaceViewModel viewModel = new(legacy);
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
        MainPageViewModel legacy = new(Text(), environment.Application);
        SprintWorkspaceViewModel viewModel = new(legacy);
        Guid sprintId = Guid.NewGuid();

        string prompt = viewModel.GatePrompt(sprintId, null);

        Assert.Contains(sprintId.ToString("D"), prompt, StringComparison.Ordinal);
    }
}
