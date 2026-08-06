using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SprintDefinitionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationFreezesTheBaseCommitWorkflowAndConfigurationSnapshot()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(new string('a', 40), definition.BaseCommit);
        Assert.Equal("implementation-critical", definition.Workflow);
        Assert.Equal("1.0.0", definition.WorkflowVersion);
        Assert.Equal("\"en\"", definition.ConfigurationSnapshot["artifacts.language.user_facing"]);
        Assert.Empty(definition.Dependencies);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheFrozenSnapshotDoesNotChangeWhenProjectConfigurationChangesLater()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.user_facing",
            "ru",
            cancellationToken);
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.Equal("\"en\"", definition!.ConfigurationSnapshot["artifacts.language.user_facing"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnavailableRepositoryFailsCreationWithoutRegisteringASprint()
    {
        using TestEnvironment environment = await InitializedAsync(new UnavailableRepository());
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.RepositoryHeadUnavailable, result.DiagnosticCode);
        Assert.Empty(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnArtifactDependencyOnANonTerminalSprintIsRejectedWithoutCreatingAnything()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId upstream = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Artifact, "sha256:abc", upstream)]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyNotTerminal, result.DiagnosticCode);
        Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnArtifactDependencyOnACompletedSprintSucceeds()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId upstream = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        await CompleteDirectlyAsync(store, environment.ProjectRoot, upstream, cancellationToken);

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Artifact, "sha256:abc", upstream)]),
            cancellationToken);

        Assert.True(result.Succeeded);
        SprintDefinition? definition = await orchestrator.GetDefinitionAsync(
            environment.ProjectRoot, result.SprintId!, cancellationToken);
        Assert.Equal(upstream, definition!.Dependencies.Single().SourceSprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACommitDependencyNeedsNoTerminalityCheck()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Commit, new string('b', 40))]),
            cancellationToken);

        Assert.True(result.Succeeded);
    }

    private static async Task CompleteDirectlyAsync(
        ISprintStore store,
        string root,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string id = sprintId.Value.ToString("D");
        (string state, long version)[] steps =
        [
            ("ready", 1), ("running", 2), ("ready_to_finalize", 3), ("completed", 4),
        ];
        foreach ((string state, long version) in steps)
        {
            await store.AppendTransitionAsync(
                root, sprintId, AggregateKind.Sprint, id, "SprintChanged", "workflow.sprint_advanced",
                state, version, Guid.NewGuid(), cancellationToken);
        }
    }

    private static async Task<TestEnvironment> InitializedAsync(IRepository? repository = null)
    {
        TestEnvironment environment = new(repository: repository);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
