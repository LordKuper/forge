using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class StatusAdvisorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UninitializedProjectRecommendsInitializationDeterministically()
    {
        using TestEnvironment environment = new();

        ProjectStatusSnapshot first = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);
        ProjectStatusSnapshot second = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        SuggestedAction action = Assert.Single(first.SuggestedActions);
        Assert.Equal("initialize_project", action.ActionId);
        Assert.Equal(1, action.Rank);
        Assert.Equal(SafetyClass.ConfirmMutation, action.SafetyClass);
        Assert.Equal(StaleBehavior.RejectWithoutSideEffect, action.StaleBehavior);
        Assert.Equal(0, action.ExpectedStateVersion);
        Assert.Equal(
            first.SuggestedActions.Select(item => item.Command.IdempotencyKey),
            second.SuggestedActions.Select(item => item.Command.IdempotencyKey));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedStartupRanksRecoveryWithTheFailingCheck()
    {
        using TestEnvironment environment = new(new UnsupportedPlatformPreflight());

        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        SuggestedAction recovery = snapshot.SuggestedActions.Single(
            action => action.ActionId == "recover_startup");
        Assert.Equal("startup_check", recovery.Target.Kind);
        Assert.Equal("platform", recovery.Target.Id);
        Assert.Equal(StartupState.Failed, snapshot.Startup);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InitializedProjectAdvancesTheStateVersion()
    {
        using TestEnvironment environment = new();
        ProjectStatusSnapshot before = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);
        await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true, before.StateVersion),
            TestContext.Current.CancellationToken);

        ProjectStatusSnapshot after = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, before.StateVersion);
        Assert.Equal(1, after.StateVersion);
        Assert.True(after.Project.Initialized);
        Assert.Empty(after.SuggestedActions);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MachineSnapshotStaysCultureInvariant()
    {
        using TestEnvironment environment = new();
        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(StatusJson.Serialize(snapshot));

        Assert.Equal("1.0.0", json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("blocked", json.RootElement.GetProperty("startup").GetString());
        Assert.Equal(
            "initialize_project",
            json.RootElement.GetProperty("suggested_actions")[0].GetProperty("action_id").GetString());
    }
}
