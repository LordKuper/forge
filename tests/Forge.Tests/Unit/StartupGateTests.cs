using System.Text.Json;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.UnitTests;

public sealed class StartupGateTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedStartupRefusesProjectMutation()
    {
        using TestEnvironment environment = new(new UnsupportedPlatformPreflight());

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.StartupFailed, result.DiagnosticCode);
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedStartupOffersRecoveryOnly()
    {
        using TestEnvironment environment = new(new UnsupportedPlatformPreflight());

        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        SuggestedAction action = Assert.Single(snapshot.SuggestedActions);
        Assert.Equal("recover_startup", action.ActionId);
        Assert.Equal(1, action.Rank);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForeignIdempotencyKeyIsRejectedWithoutSideEffect()
    {
        using TestEnvironment environment = new();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true, null, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecommendedIdempotencyKeyIsAccepted()
    {
        using TestEnvironment environment = new();
        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectStatusAsync(
            null,
            TestContext.Current.CancellationToken);
        SuggestedAction action = snapshot.SuggestedActions.Single();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(
                environment.ProjectRoot,
                true,
                action.ExpectedStateVersion,
                action.Command.IdempotencyKey),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellationLeavesNoStagingTree()
    {
        using TestEnvironment environment = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => environment.Application.InitializeProjectAsync(
                new(environment.ProjectRoot, true),
                cancellation.Token));

        Assert.Empty(Directory.GetDirectories(environment.ProjectRoot, ".forge.staging-*"));
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NonStringConfigurationValuesRoundTrip()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "interaction.confirm_destructive",
            ConfigurationValueParser.Parse("false"),
            TestContext.Current.CancellationToken);
        IReadOnlyList<EffectiveConfigurationValue> values =
            await environment.Application.GetUserConfigurationAsync(
                TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(values
            .Single(value => value.Key == "interaction.confirm_destructive")
            .Value
            .GetBoolean());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnknownConfigurationKeyReportsItsOwnDiagnostic()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.unknown",
            ConfigurationValueParser.Parse("ru"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationKeyUnknown, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CorruptUserConfigurationIsReportedInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        string path = Path.Combine(environment.LocalApplicationData, "Forge", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, result.DiagnosticCode);
        Assert.Empty(await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SharedHostRegistersTheBuiltInConfigurationKeys()
    {
        ServiceCollection services = new();
        services.AddForgeCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        IConfigurationRegistry registry = provider.GetRequiredService<IConfigurationRegistry>();

        Assert.NotEmpty(registry.Keys);
        Assert.Equal(ConfigurationScope.User, registry.FindRequired("language.ui").Scope);
    }
}
