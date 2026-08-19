using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ScopedConfigurationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnablingARegisteredProviderIdSucceeds()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")]);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            ConfigurationKeys.ProvidersEnabled,
            JsonSerializer.SerializeToElement<string[]>(["codex"]),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnablingAnUnregisteredProviderIdIsRejected()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")]);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            ConfigurationKeys.ProvidersEnabled,
            JsonSerializer.SerializeToElement<string[]>(["codex", "no-such-provider"]),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AMalformedUserConfigurationFileDegradesToOmittedInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        string path = ConfigurationStoreFactory.UserPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not json", TestContext.Current.CancellationToken);
        ConfigurationRegistry registry = new();
        ScopedConfigurationStores stores =
            new(new ConfigurationStoreFactory(registry), new ConfigurationMigrator([]), environment);
        ScopedConfigurationProviderEnablementSource source = new(stores);

        IReadOnlyList<string>? enabledIds =
            await source.GetEnabledIdsAsync(TestContext.Current.CancellationToken);

        Assert.Null(enabledIds);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UserValuesExposeProvenance()
    {
        using TestEnvironment environment = new();

        IReadOnlyList<EffectiveConfigurationValue> defaults =
            (await environment.Application.GetUserConfigurationAsync(
                TestContext.Current.CancellationToken)).Values;
        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);
        IReadOnlyList<EffectiveConfigurationValue> updated =
            (await environment.Application.GetUserConfigurationAsync(
                TestContext.Current.CancellationToken)).Values;

        Assert.Equal(
            ConfigurationProvenance.BuiltInDefault,
            Value(defaults, "language.ui").Provenance);
        Assert.Equal(ConfigurationProvenance.User, Value(updated, "language.ui").Provenance);
        Assert.Equal(
            ConfigurationProvenance.Inherited,
            Value(updated, "language.llm").Provenance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectKeyInUserScopeIsRejected()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "artifacts.language.user_facing",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationScopeViolation, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UserKeyInProjectScopeIsRejected()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationScopeViolation, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ArtifactLanguagesAreIndependentFromTheUserLanguage()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);
        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        ConfigurationWriteResult write = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.agent_facing",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);
        IReadOnlyList<EffectiveConfigurationValue> project =
            (await environment.Application.GetProjectConfigurationAsync(
                environment.ProjectRoot,
                TestContext.Current.CancellationToken)).Values;

        Assert.True(write.Succeeded);
        Assert.Equal("en", Value(project, "artifacts.language.user_facing").Value.GetString());
        Assert.Equal("ru", Value(project, "artifacts.language.agent_facing").Value.GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TokenBudgetRoundTripsAsAProjectScopedIntegerDefaultingToTheRegisteredValue()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);

        IReadOnlyList<EffectiveConfigurationValue> defaults =
            (await environment.Application.GetProjectConfigurationAsync(
                environment.ProjectRoot,
                TestContext.Current.CancellationToken)).Values;
        ConfigurationWriteResult write = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "context.token_budget",
            JsonSerializer.SerializeToElement(40000),
            TestContext.Current.CancellationToken);
        IReadOnlyList<EffectiveConfigurationValue> updated =
            (await environment.Application.GetProjectConfigurationAsync(
                environment.ProjectRoot,
                TestContext.Current.CancellationToken)).Values;

        Assert.Equal(32000, Value(defaults, "context.token_budget").Value.GetInt32());
        Assert.True(write.Succeeded);
        Assert.Equal(40000, Value(updated, "context.token_budget").Value.GetInt32());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANonPositiveTokenBudgetIsRejected()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);

        ConfigurationWriteResult write = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "context.token_budget",
            JsonSerializer.SerializeToElement(0),
            TestContext.Current.CancellationToken);

        Assert.False(write.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, write.DiagnosticCode);
    }

    // Round 1 review of PR #69: a value satisfying project-manifest.schema.json's "integer,
    // minimum: 1" (JSON Schema's "integer" type has no bit-width of its own) but exceeding
    // Int32.MaxValue used to reach ConfigurationSchemaCodec.ToProject's typed serialization
    // unguarded. GetOptionalInt32's own TryGetInt32 check already rejects it on the write path --
    // this proves that write-time rejection still holds now that the schema also carries an
    // explicit "maximum" bound.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnOutOfInt32RangeTokenBudgetIsRejected()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);

        ConfigurationWriteResult write = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "context.token_budget",
            JsonSerializer.SerializeToElement(3_000_000_000L),
            TestContext.Current.CancellationToken);

        Assert.False(write.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, write.DiagnosticCode);
    }

    // Round 2 review of PR #69: the first fix attempt for the token-budget round-trip bug used
    // YamlDotNet's broad WithAttemptingUnquotedStringTypeDeserialization() builder option, which
    // was reverted after being shown to coerce `true`/`false`-valued strings to bool -- breaking
    // this exact round trip for any string field that happens to hold one of those two literal
    // values (a real regression for artifacts.language.*, which permits "true" as a valid 4-letter
    // BCP-47 subtag). The replacement fix (CoerceTokenBudgetToNumber, scoped only to
    // context.token_budget) must not reintroduce this.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AStringValuedFieldThatLooksLikeABooleanRoundTripsAsAStringUnchanged()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);

        ConfigurationWriteResult write = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.agent_facing",
            JsonSerializer.SerializeToElement("true"),
            TestContext.Current.CancellationToken);
        IReadOnlyList<EffectiveConfigurationValue> project =
            (await environment.Application.GetProjectConfigurationAsync(
                environment.ProjectRoot,
                TestContext.Current.CancellationToken)).Values;

        Assert.True(write.Succeeded);
        Assert.Equal("true", Value(project, "artifacts.language.agent_facing").Value.GetString());
    }

    // Round 2 review of PR #69: the same reverted fix attempt above also parsed YAML float specials
    // (`.inf`/`.nan`) as `double.PositiveInfinity`/`NaN`, which JsonSerializer.SerializeToElement
    // then threw an unguarded ArgumentException on -- reproduced directly against YamlDotNet 18.1.0
    // with this store's exact configuration. The replacement fix never invokes YamlDotNet's own
    // type inference at all, so a `.inf`-like scalar in any field is just a string that fails its
    // own schema type/pattern check like any other garbled value, not a crash.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AYamlFloatSpecialInAnyFieldDegradesGracefullyInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);
        string manifestPath = ProjectRootResolver.ManifestPath(environment.ProjectRoot);
        string manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.Replace("workflow: implementation-critical", "workflow: .inf"),
            TestContext.Current.CancellationToken);

        ConfigurationView result = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot, TestContext.Current.CancellationToken);

        // Degrades via ProjectRootResolver.ReadManifestAsync's own recoverable-error path (reached
        // before GetProjectConfigurationAsync's own try block even begins), not via
        // ConfigurationInvalid -- the exact call-order finding round 1 traced for the int-overflow
        // bug. What matters here is that it degrades cleanly at all, not which of the two codes.
        Assert.Equal(DiagnosticCodes.ProjectDirectoryUnknown, result.DiagnosticCode);
        Assert.Empty(result.Values);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectConfigurationRequiresAnInitializedProject()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.user_facing",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectNotInitialized, result.DiagnosticCode);
        Assert.Empty((await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken)).Values);
    }

    private static EffectiveConfigurationValue Value(
        IReadOnlyList<EffectiveConfigurationValue> values,
        string key) =>
        values.Single(value => value.Key == key);
}
