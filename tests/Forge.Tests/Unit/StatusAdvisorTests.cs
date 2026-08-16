using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class StatusAdvisorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UninitializedProjectRecommendsInitializationDeterministically()
    {
        using TestEnvironment environment = new();

        ProjectSnapshot first = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);
        ProjectSnapshot second = await environment.Application.GetProjectSnapshotAsync(
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
    public async Task ASuggestedActionsSchemaVersionNeverFollowsTheSnapshotsOwnSchemaVersion()
    {
        // Regression test: ProjectSnapshot.SchemaVersion and SuggestedAction.SchemaVersion are
        // versioned independently (suggested-action.schema.json did not change when the snapshot
        // gained provider/startup-check fields). They previously shared one constant, so bumping
        // the snapshot's version accidentally bumped every suggested action's version with it.
        using TestEnvironment environment = new();

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null, TestContext.Current.CancellationToken);

        SuggestedAction action = Assert.Single(snapshot.SuggestedActions);
        Assert.NotEqual(snapshot.SchemaVersion, action.SchemaVersion);
        Assert.Equal("1.0.0", action.SchemaVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedStartupRanksRecoveryWithTheFailingCheck()
    {
        using TestEnvironment environment = new();
        string path = ConfigurationStoreFactory.UserPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);

        SuggestedAction recovery = snapshot.SuggestedActions.Single(
            action => action.ActionId == "recover_startup");
        Assert.Equal(1, recovery.Rank);
        Assert.Equal("startup_check", recovery.Target.Kind);
        Assert.Equal("user_configuration", recovery.Target.Id);
        Assert.Equal(StartupState.Failed, snapshot.Startup);
        Assert.DoesNotContain(
            snapshot.SuggestedActions,
            action => action.ActionId == "initialize_project");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InitializedProjectAdvancesTheStateVersion()
    {
        using TestEnvironment environment = new();
        ProjectSnapshot before = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);
        await environment.InitializeAsync(
            environment.ProjectRoot,
            true,
            TestContext.Current.CancellationToken);

        ProjectSnapshot after = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, before.StateVersion);
        Assert.Equal(1, after.StateVersion);
        Assert.True(after.Project.Initialized);
        Assert.Empty(after.SuggestedActions);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADeferredRouteDecisionSurfacesAsTheSprintsResumeNotBeforeInTheSnapshot()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        RoutingLedger routingLedger = environment.Resolve<RoutingLedger>();
        IClock clock = environment.Resolve<IClock>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        HealthKey key = new("claude_code", "sonnet", "sprint");
        RouteDecision routed = await routingLedger.DecideAsync(
            environment.ProjectRoot, sprintId, "a", AttemptId.New(), key, cancellationToken);
        DateTimeOffset resumeAt = clock.UtcNow + TimeSpan.FromMinutes(5);
        await routingLedger.RecordDeferralAsync(
            environment.ProjectRoot, sprintId, routed, resumeAt, cancellationToken);

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null, SnapshotDetail.Summary, sprintId.Value, cancellationToken);

        Assert.Equal(resumeAt, snapshot.Details!.Routing.ResumeNotBefore);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheSnapshotCarriesTheSameStartupChecksAndProviderHealthTheStartupPassAlreadyComputed()
    {
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(
            FakeProviderToolchainManager.Ready));

        ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null, TestContext.Current.CancellationToken);

        Assert.Equal(8, snapshot.StartupChecks.Count);
        Assert.Contains(snapshot.StartupChecks, check => check.Id == StartupCheckId.Providers);
        ProviderHealthEntry codex = Assert.Single(snapshot.Providers, entry => entry.Id == "codex");
        Assert.Equal(ProviderState.Ready, codex.State);
        Assert.True(codex.Registered);
        Assert.True(codex.Enabled);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MachineSnapshotStaysCultureInvariant()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            using TestEnvironment environment = new();
            await environment.Application.SetConfigurationAsync(
                ConfigurationScope.User,
                null,
                "language.ui",
                JsonSerializer.SerializeToElement("ru"),
                TestContext.Current.CancellationToken);

            ProjectSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
                null,
                TestContext.Current.CancellationToken);
            using JsonDocument json = JsonDocument.Parse(StatusJson.Serialize(snapshot));

            Assert.Equal("1.3.0", json.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("blocked", json.RootElement.GetProperty("startup").GetString());
            Assert.Equal(
                "initialize_project",
                json.RootElement.GetProperty("suggested_actions")[0].GetProperty("action_id").GetString());
            Assert.Equal(
                "confirm_mutation",
                json.RootElement.GetProperty("suggested_actions")[0].GetProperty("safety_class").GetString());
            Assert.Equal(
                TimeSpan.Zero,
                DateTimeOffset
                    .Parse(
                        json.RootElement.GetProperty("generated_at").GetString()!,
                        CultureInfo.InvariantCulture)
                    .Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
