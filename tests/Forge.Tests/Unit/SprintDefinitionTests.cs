using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SprintDefinitionTests
{
    private static readonly string[] ViolatingModelPolicy = ["codex:some-other-model"];
    private static readonly string[] SatisfyingModelPolicy = ["codex:codex-fake-model"];


    // ADR 0008: "Routing candidates are the ordered intersection of the frozen project profile and
    // the user-enabled set... The resolved candidate list is frozen into the sprint profile." No
    // project constraint is configurable yet, so this proves the user-enabled resolution alone --
    // in the enabled list's order, not provider-registration order.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationFreezesTheEnabledProviderCandidatesInTheEnabledOrder()
    {
        using TestEnvironment environment = new(
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0"),
            ],
            providerEnablement: new FakeProviderEnablementSource(["claude_code", "codex"]));
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(["claude_code", "codex"], definition.FrozenProviders);
    }

    // ADR 0006/0014: "The sprint snapshot resolves one profile for planning, implementation, and
    // review." Two candidates exist, so review must prefer the one implementation does not use.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationFreezesThreeExecutionProfilesWithAnIndependentReviewLineage()
    {
        using TestEnvironment environment = new(
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0"),
            ],
            providerEnablement: new FakeProviderEnablementSource(["claude_code", "codex"]));
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(3, definition.ExecutionProfiles.Count);
        Assert.Equal("claude_code", definition.ExecutionProfiles[ExecutionPhase.Implementation].Provider);
        Assert.Equal("codex", definition.ExecutionProfiles[ExecutionPhase.Review].Provider);
        Assert.True(definition.ExecutionProfiles[ExecutionPhase.Review].Lineage!.AchievedIndependence);
    }

    // A single enabled provider must still complete a sprint -- ADR 0006: "A single-provider
    // configuration can complete review while Forge still prefers a distinct provider/model
    // lineage whenever one is available." Reduced separation is recorded, never a gate.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationFreezesAReviewProfileWithoutIndependenceWhenOnlyOneProviderIsEnabled()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["claude_code"]));
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal("claude_code", definition.ExecutionProfiles[ExecutionPhase.Review].Provider);
        Assert.False(definition.ExecutionProfiles[ExecutionPhase.Review].Lineage!.AchievedIndependence);
    }

    // ADR 0008: "An empty intersection blocks execution with a stable diagnostic rather than
    // silently selecting another provider."
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationWithNoEnabledProvidersFailsWithoutRegisteringASprint()
    {
        using TestEnvironment environment = new(llmProviders: []);
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintProviderCandidatesEmpty, result.DiagnosticCode);
        Assert.Empty(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    // A provider id enabled by configuration but never registered (not installed/shipped in this
    // build) must be dropped, not frozen as a phantom routing candidate -- ProviderCatalog.
    // ResolveEnabled already drops unknown ids; this proves the freeze doesn't undo that.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnEnabledButUnregisteredProviderIsNotFrozenAsACandidate()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex", "unregistered"]));
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.Equal(["codex"], definition!.FrozenProviders);
    }

    // ADR 0042: models.allowed_models restricts codex to a model FakeLlmProvider never resolves
    // ("codex-fake-model" is its own fixed DefaultModel) -- creation must refuse before any event
    // is written, the same shape CreationWithNoEnabledProvidersFailsWithoutRegisteringASprint proves
    // for the adjacent empty-candidates case.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationRefusesAProviderWhoseDefaultModelViolatesTheConfiguredPolicy()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "models.allowed_models",
            JsonSerializer.SerializeToElement(ViolatingModelPolicy),
            cancellationToken);
        Assert.True(configured.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ModelPolicyViolation, result.DiagnosticCode);
        Assert.Empty(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    // The same policy, but listing codex's actual resolved model -- creation must succeed, proving
    // the gate does not refuse every configured policy, only a genuine mismatch.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationSucceedsWhenTheFrozenModelIsListedInTheConfiguredPolicy()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "models.allowed_models",
            JsonSerializer.SerializeToElement(SatisfyingModelPolicy),
            cancellationToken);
        Assert.True(configured.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    // A definition.json written before FrozenProviders existed has no such key at all -- proves
    // LoadDefinitionAsync tolerates that instead of throwing, defaulting to an empty list.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ALegacyDefinitionWithNoFrozenProvidersFieldLoadsWithAnEmptyList()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        JsonNode definitionRoot = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath, cancellationToken))!;
        JsonObject definitionObject = definitionRoot.AsObject();
        string frozenProvidersKey = definitionObject.Select(property => property.Key)
            .First(key => string.Equals(key, "frozen_providers", StringComparison.OrdinalIgnoreCase));
        definitionObject.Remove(frozenProvidersKey);
        await File.WriteAllTextAsync(definitionPath, definitionRoot.ToJsonString(), cancellationToken);

        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Empty(definition.FrozenProviders);
    }

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
        // Only the upstream sprint exists: the rejected creation registered nothing.
        Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
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

    [Theory]
    [Trait("Category", "Unit")]
    // A branch name is not an immutable id at all; an abbreviated or uppercase sha is not the
    // canonical spelling; and .NET regex `$` matches immediately before a trailing '\n', so a
    // canonical-looking id smuggling one in must still be rejected.
    [InlineData(SprintDependencyKind.Commit, "main")]
    [InlineData(SprintDependencyKind.Commit, "bbbbbbb")]
    [InlineData(SprintDependencyKind.Commit, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData(SprintDependencyKind.Commit, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData(SprintDependencyKind.Artifact, "sha256:abc")]
    [InlineData(
        SprintDependencyKind.Artifact,
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public async Task ADependencyIdThatIsNotItsCanonicalImmutableSpellingIsRejected(
        SprintDependencyKind kind,
        string id)
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), [new(kind, id)]),
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
