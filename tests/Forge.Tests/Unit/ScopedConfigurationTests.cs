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

    /// <summary>ADR 0067's four keys travel the same surface path the settings page will use — the
    /// raw-string <c>SetConfigurationAsync</c> overload, so <see cref="ConfigurationValueParser"/>
    /// is exercised too — and come back out of the resolver with their written values. The effort
    /// map's model id is asserted verbatim because <c>JsonSerializerOptions.PropertyNamingPolicy</c>
    /// is snake_case here and only <c>DictionaryKeyPolicy</c> (unset) governs map keys; if that ever
    /// changed, every configured model id would silently stop matching a real model.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheApprovalThemePriorityAndEffortKeysRoundTripThroughTheUserStore()
    {
        using TestEnvironment environment = new(
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0")
            ]);
        (string Key, string RawValue)[] writes =
        [
            (ConfigurationKeys.AutoApproveGate, "true"),
            (ConfigurationKeys.ProvidersPriority, """["claude_code","codex"]"""),
            (ConfigurationKeys.ModelsEffort, """{"claude-sonnet-4-5":"high","gpt-5-Codex":"xhigh"}"""),
            (ConfigurationKeys.ShellTheme, "light"),
        ];

        foreach ((string key, string rawValue) in writes)
        {
            ConfigurationWriteResult write = await environment.Application.SetConfigurationAsync(
                ConfigurationScope.User, null, key, rawValue, TestContext.Current.CancellationToken);
            Assert.True(write.Succeeded, key);
        }

        IReadOnlyList<EffectiveConfigurationValue> resolved =
            (await environment.Application.GetUserConfigurationAsync(
                TestContext.Current.CancellationToken)).Values;

        Assert.True(Value(resolved, ConfigurationKeys.AutoApproveGate).Value.GetBoolean());
        Assert.Equal(
            ["claude_code", "codex"],
            Value(resolved, ConfigurationKeys.ProvidersPriority).Value
                .EnumerateArray().Select(item => item.GetString()));
        JsonElement effort = Value(resolved, ConfigurationKeys.ModelsEffort).Value;
        Assert.Equal("high", effort.GetProperty("claude-sonnet-4-5").GetString());
        Assert.Equal("xhigh", effort.GetProperty("gpt-5-Codex").GetString());
        Assert.Equal("light", Value(resolved, ConfigurationKeys.ShellTheme).Value.GetString());
        Assert.Equal(ConfigurationProvenance.User, Value(resolved, ConfigurationKeys.ShellTheme).Provenance);
    }

    /// <summary>ADR 0014's tolerant-read philosophy applied to configuration rather than sprint
    /// definitions: a file written before ADR 0067 existed (literal <c>schema_version</c> "1.0.0",
    /// none of the four objects present) still loads, and every new key resolves to its built-in
    /// default rather than failing the whole document.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AConfigurationFileWrittenBeforeTheseKeysExistedResolvesThemToTheirDefaults()
    {
        using TestEnvironment environment = new();
        string path = ConfigurationStoreFactory.UserPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """{"schema_version":"1.0.0","language":{"ui":"en"},"interaction":{}}""",
            TestContext.Current.CancellationToken);

        IReadOnlyList<EffectiveConfigurationValue> resolved =
            (await environment.Application.GetUserConfigurationAsync(
                TestContext.Current.CancellationToken)).Values;

        Assert.False(Value(resolved, ConfigurationKeys.AutoApproveGate).Value.GetBoolean());
        Assert.Empty(Value(resolved, ConfigurationKeys.ProvidersPriority).Value.EnumerateArray());
        Assert.Empty(Value(resolved, ConfigurationKeys.ModelsEffort).Value.EnumerateObject());
        Assert.Equal("dark", Value(resolved, ConfigurationKeys.ShellTheme).Value.GetString());
        Assert.All(
            new[]
            {
                ConfigurationKeys.AutoApproveGate, ConfigurationKeys.ProvidersPriority,
                ConfigurationKeys.ModelsEffort, ConfigurationKeys.ShellTheme,
            },
            key => Assert.Equal(ConfigurationProvenance.BuiltInDefault, Value(resolved, key).Provenance));
    }

    /// <summary>An id with no registration invalidates <c>providers.priority</c> exactly as it does
    /// <c>providers.enabled</c> (ADR 0008's rule, extended by ADR 0067), reported through the same
    /// <see cref="DiagnosticCodes.ConfigurationInvalid"/> contract.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrioritizingAnUnregisteredProviderIdIsRejected()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")]);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            ConfigurationKeys.ProvidersPriority,
            JsonSerializer.SerializeToElement<string[]>(["codex", "no-such-provider"]),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, result.DiagnosticCode);
    }

    /// <summary>A priority list is an ordering, so an id may appear once. Enforced by
    /// `user-config.schema.json`'s `uniqueItems`, the same way `providers.enabled` enforces it.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADuplicateProviderPriorityEntryIsRejected()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")]);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            ConfigurationKeys.ProvidersPriority,
            JsonSerializer.SerializeToElement<string[]>(["codex", "codex"]),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, result.DiagnosticCode);
    }

    /// <summary>Effort vocabulary outside <see cref="ProviderEffortLevels.KnownLevels"/> is rejected
    /// at the configuration boundary rather than accepted and silently dropped later — the exact
    /// failure mode <see cref="ProviderEffortLevels.Resolve"/> documents for an unknown level.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnknownEffortLevelIsRejected()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            ConfigurationKeys.ModelsEffort,
            JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["claude-sonnet-4-5"] = "aggressive" }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AThemeOutsideTheDeclaredEnumIsRejected()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            ConfigurationKeys.ShellTheme,
            JsonSerializer.SerializeToElement("solarized"),
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

    // Round 2 review of PR #69: the same reverted fix attempt above also parsed a plain, untagged
    // YAML float special (`.inf`/`.nan`) as `double.PositiveInfinity`/`NaN`, which
    // JsonSerializer.SerializeToElement then threw an unguarded ArgumentException on -- reproduced
    // directly against YamlDotNet 18.1.0 with this store's exact configuration. The replacement fix
    // (CoerceTokenBudgetToNumber) never invokes YamlDotNet's own broad type-inference option, so a
    // plain, untagged `.inf`-like scalar in any field is just a string that fails its own schema
    // type/pattern check like any other garbled value, not a crash. This test covers only the plain
    // form; see AnExplicitlyFloatTaggedYamlSpecialDegradesGracefullyInsteadOfThrowing for the
    // explicitly-tagged form round 3 found this test does not cover.
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

    // Round 3 review of PR #69, confirmed by direct reproduction: round 2's own fix and its test
    // above only cover a PLAIN, untagged `.inf`/`.nan` scalar. YamlDotNet's plain, option-less
    // Deserialize<object> still honors an EXPLICIT YAML type tag (`!!float .inf`) even with no
    // type-inference builder option enabled at all -- only untagged scalars are stringified by
    // default. An explicitly float-tagged value still parsed as a real `double` and still crashed
    // JsonSerializer.SerializeToElement with the identical unguarded ArgumentException, pre-existing
    // on `main`, not introduced by this PR, but left uncovered by round 2's own claim to have closed
    // it. Fixed at the actual point of failure: SerializeToElement is now called with
    // JsonNumberHandling.AllowNamedFloatingPointLiterals (see RawSerializerOptions), writing the
    // three named values as JSON strings instead of throwing, so schema validation rejects them the
    // ordinary way instead of a crash reaching this test at all.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExplicitlyFloatTaggedYamlSpecialDegradesGracefullyInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);
        string manifestPath = ProjectRootResolver.ManifestPath(environment.ProjectRoot);
        string manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.Replace("workflow: implementation-critical", "workflow: !!float .inf"),
            TestContext.Current.CancellationToken);

        ConfigurationView result = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot, TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticCodes.ProjectDirectoryUnknown, result.DiagnosticCode);
        Assert.Empty(result.Values);
    }

    // Round 3 review of PR #69: a bare long.TryParse(string, out long) used NumberStyles.Integer
    // (permitting leading/trailing whitespace and a leading sign) under CultureInfo.CurrentCulture,
    // so a hand-edited manifest could parse a whitespace-padded token_budget differently -- or
    // silently succeed where it should not -- depending on the reading machine's culture settings.
    // Pinned to InvariantCulture with only AllowLeadingSign; a whitespace-padded value is now left
    // as a string and rejected by the ordinary schema "type": "integer" check instead of being
    // silently trimmed and accepted.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AWhitespacePaddedTokenBudgetIsNotSilentlyAccepted()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(
            environment.ProjectRoot, true,
            TestContext.Current.CancellationToken);
        string manifestPath = ProjectRootResolver.ManifestPath(environment.ProjectRoot);
        string manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest + "context:\n  token_budget: \"  +40000  \"\n",
            TestContext.Current.CancellationToken);

        ConfigurationView result = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot, TestContext.Current.CancellationToken);

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
