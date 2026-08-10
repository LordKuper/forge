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
        Assert.Equal("en", definition.ConversationLanguage);
        Assert.Matches("^sha256:[0-9a-f]{64}$", definition.ArtifactPolicySnapshotHash);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheArtifactPolicySnapshotHashStaysFrozenAfterALaterConfigurationChange()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        string originalHash =
            (await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .ArtifactPolicySnapshotHash;

        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.agent_facing",
            "ru",
            cancellationToken);
        SprintDefinition? afterChange =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.Equal(originalHash, afterChange!.ArtifactPolicySnapshotHash);

        SprintId secondSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        string secondHash =
            (await orchestrator.GetDefinitionAsync(environment.ProjectRoot, secondSprintId, cancellationToken))!
            .ArtifactPolicySnapshotHash;
        Assert.NotEqual(originalHash, secondHash);
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

        // Naming a source sprint is always rejected — fail-closed, since no publication record
        // exists to verify against — regardless of whether that source sprint is even terminal yet.
        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Artifact, "sha256:" + new string('a', 64), upstream)]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyNotPublished, result.DiagnosticCode);
        Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnArtifactDependencyWithASourceSprintIsRejectedEvenOnceItIsCompleted()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId upstream = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        await CompleteDirectlyAsync(store, environment.ProjectRoot, upstream, cancellationToken);

        // No durable artifact-publication record exists yet, so a claim that a specific source
        // sprint published this exact digest can never be verified — it fails closed even though
        // the well-formed digest and the terminal source sprint would otherwise look plausible.
        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Artifact, "sha256:" + new string('a', 64), upstream)]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyNotPublished, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnArtifactDependencyWithNoSourceSprintIsTrustedAsAlreadyPublished()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Artifact, "sha256:" + new string('a', 64))]),
            cancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnArtifactDependencyWithAMalformedDigestIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), [new(SprintDependencyKind.Artifact, "sha256:abc")]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyInvalid, result.DiagnosticCode);
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACommitDependencyOnABranchNameIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), [new(SprintDependencyKind.Commit, "main")]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACommitDependencyWithAnAbbreviatedShaIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Commit, new string('b', 7))]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACommitDependencyWithUppercaseHexIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Commit, new string('B', 40))]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACommitDependencyWithATrailingNewlineIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // .NET regex `$` matches immediately before a trailing '\n', not just at the true end of the
        // string — a canonical-looking id smuggling one in must still be rejected.
        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Commit, new string('a', 40) + "\n")]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnArtifactDependencyWithATrailingNewlineIsRejected()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                [new(SprintDependencyKind.Artifact, "sha256:" + new string('a', 64) + "\n")]),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintDependencyInvalid, result.DiagnosticCode);
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
