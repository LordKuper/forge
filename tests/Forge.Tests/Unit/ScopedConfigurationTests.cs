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
            "providers.enabled",
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
            "providers.enabled",
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
        ScopedConfigurationStores stores = new(new ConfigurationStoreFactory(registry), environment);
        ScopedConfigurationProviderEnablementSource source = new(new ConfigurationMigrator([]), stores);

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
