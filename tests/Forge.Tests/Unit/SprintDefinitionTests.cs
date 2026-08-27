using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Infrastructure;
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

    // Round 2 review of PR #120: ADR 0063 made ILlmProvider.DefaultModel resolvable at runtime, so a
    // provider-capability refresh can change it at any moment -- including between this method's gate
    // check and its freeze, which are separated by durable writes. Two reads there would let the gate
    // approve model A while the sprint freezes and runs model B, silently defeating the very
    // models.allowed_models restriction the gate exists to enforce. Against a provider that reports a
    // NEW model on every single read, the model the allowlist approved must be exactly the model the
    // definition freezes -- one resolution per creation call, used for both.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationResolvesTheDefaultModelOnceSoTheGateApprovesTheModelItFreezes()
    {
        ShiftingModelProvider provider = new(new ProviderId("codex"));
        using TestEnvironment environment = new(
            llmProviders: [provider],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        string approvedModel = ShiftingModelProvider.ModelName(1);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "models.allowed_models",
            JsonSerializer.SerializeToElement(new[] { $"codex:{approvedModel}" }),
            cancellationToken);
        Assert.True(configured.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        // Discards whatever setup above read, so the creation call under test starts at read 1 -- the
        // one value the allowlist names.
        provider.Rewind();

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        SprintDefinition? definition = await orchestrator.GetDefinitionAsync(
            environment.ProjectRoot, result.SprintId!, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.All(definition!.ExecutionProfiles.Values, profile => Assert.Equal(approvedModel, profile.Model));
        Assert.Equal(
            approvedModel,
            definition.ExecutionProfiles[ExecutionPhase.Review].Lineage!.ImplementationModel);
        Assert.Equal(1, provider.ModelReads);
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

    // ADR 0057: the optional title is frozen with everything else and survives the definition.json
    // round trip. Whitespace is trimmed on the way in -- normalization belongs to the orchestrator,
    // so no surface has to do it (or forget to).
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreationFreezesTheSuppliedTitleAndItSurvivesTheDefinitionRoundTrip()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Title: "  Close the sidebar parity gap  "),
            cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.Equal("Close the sidebar parity gap", definition!.Title);
    }

    // ADR 0057: a blank title is not an error -- it freezes no title at all, never an empty string a
    // surface would then have to re-detect as "untitled" (ProjectCatalogStore.SetAliasAsync's own
    // "empty/whitespace clears" rule).
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreationWithABlankTitleFreezesNoTitleRatherThanAnEmptyOne(string? title)
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Title: title), cancellationToken);
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, result.SprintId!, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(definition!.Title);
    }

    // ADR 0057: a title is free-typed user text that can carry a pasted credential, so it is
    // redacted before it is ever written to definition.json or projected into a snapshot.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATitleCarryingASecretIsRedactedBeforeItIsFrozen()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Title: "rotate api_key=qwerty"),
            cancellationToken)).SprintId!;
        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.DoesNotContain("qwerty", definition!.Title!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", definition.Title!, StringComparison.Ordinal);
    }

    // ADR 0057: refused before any event is written, the same fail-closed shape
    // CreationWithNoEnabledProvidersFailsWithoutRegisteringASprint proves for the adjacent gates.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATitleLongerThanTheBoundIsRefusedWithoutRegisteringASprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Title: new string('t', SprintOrchestrator.MaxSprintTitleLength + 1)),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTitleTooLong, result.DiagnosticCode);
        Assert.Empty(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    // ADR 0057: "Redaction runs before the length check, not after." This is the case that ordering
    // exists for, and the only one that can tell the two orderings apart -- the plain over-length
    // title above is refused either way, because redaction does not touch it. Here the title is
    // exactly at the bound as typed but its redaction placeholder pushes the stored value past it,
    // so check-then-redact would accept the input and freeze a title that violates
    // project-snapshot.schema.json's own maxLength: 200. Swapping the two steps in NormalizeTitle
    // breaks this test.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATitleWithinTheBoundWhoseRedactionExceedsItIsRefused()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string secret = "token=qwerty";
        string title =
            $"{new string('t', SprintOrchestrator.MaxSprintTitleLength - secret.Length - 1)} {secret}";

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Title: title), cancellationToken);

        // The premise: accepted by a length check on the raw input, refused by one on the redaction.
        Assert.Equal(SprintOrchestrator.MaxSprintTitleLength, title.Length);
        Assert.True(SecretRedactor.Redact(title).Length > SprintOrchestrator.MaxSprintTitleLength);
        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTitleTooLong, result.DiagnosticCode);
        Assert.Empty(await store.ListAsync(environment.ProjectRoot, cancellationToken));
    }

    // ADR 0057's actual compatibility guarantee, and the most important assertion in that slice: a
    // definition.json written before the field existed has no "title" key at all. Same shape as
    // ALegacyDefinitionWithNoFrozenProvidersFieldLoadsWithAnEmptyList above, and for the same reason
    // -- every already-durable sprint in every existing project takes this path on its next read.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ALegacyDefinitionWithNoTitleFieldLoadsWithANullTitleInsteadOfThrowing()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Title: "Titled once"), cancellationToken)).SprintId!;

        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        JsonNode definitionRoot = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath, cancellationToken))!;
        JsonObject definitionObject = definitionRoot.AsObject();
        string titleKey = definitionObject.Select(property => property.Key)
            .First(key => string.Equals(key, "title", StringComparison.OrdinalIgnoreCase));
        definitionObject.Remove(titleKey);
        await File.WriteAllTextAsync(definitionPath, definitionRoot.ToJsonString(), cancellationToken);

        SprintDefinition? definition =
            await orchestrator.GetDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Null(definition.Title);
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

    /// <summary>Reports a NEW model on every read of <see cref="DefaultModel"/> — the pathological end
    /// of what ADR 0063 makes runtime-resolvable, so a second read anywhere in the creation path is
    /// caught deterministically instead of depending on a real refresh actually racing it.</summary>
    private sealed class ShiftingModelProvider(ProviderId id) : ILlmProvider
    {
        private int reads;

        public ProviderId Id => id;

        public int ModelReads => Volatile.Read(ref reads);

        public string DefaultModel => ModelName(Interlocked.Increment(ref reads));

        /// <summary>The value the <paramref name="read"/>-th read since the last
        /// <see cref="Rewind"/> reports.</summary>
        public static string ModelName(int read) => $"codex-model-{read}";

        public void Rewind() => Interlocked.Exchange(ref reads, 0);

        /// <summary>A no-op that deliberately does NOT count as a read: the point of this fake is
        /// that every <see cref="DefaultModel"/> read reports something new, so the refresh
        /// <c>ExecutionProfilePolicy.ResolveModelsAsync</c> performs must not itself consume one.</summary>
        public Task RefreshDefaultModelAsync(bool bypassCache, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderStatus.Ready(id, "1.0.0"));

        public Task<ProviderStatus> InstallOrUpdateAsync(
            bool bypassReleaseCache, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderStatus.Ready(id, "1.0.0"));

        public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderAuthenticationStatus.Ready);

        public Task<ProviderRunResult> RunAsync(
            string prompt,
            string workingDirectory,
            string? model,
            string? effort,
            CancellationToken cancellationToken,
            Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null) =>
            throw new NotSupportedException("This fake only exercises sprint creation.");
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
