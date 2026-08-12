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

        InitializeProjectResult result = await environment.InitializeAsync(
            environment.ProjectRoot,
            true,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.PlatformNotSupported, result.DiagnosticCode);
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverableFailureOffersRecoveryOnly()
    {
        using TestEnvironment environment = new();
        await WriteCorruptUserConfigurationAsync(environment);

        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);

        SuggestedAction action = Assert.Single(snapshot.SuggestedActions);
        Assert.Equal("recover_startup", action.ActionId);
        Assert.Equal(1, action.Rank);
        Assert.Equal("user_configuration", action.Target.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ForeignIdempotencyKeyIsRejectedWithoutSideEffect()
    {
        using TestEnvironment environment = new();
        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken);

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true, snapshot.StateVersion, Guid.NewGuid()),
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
        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
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
    public async Task ReplayingACompletedInitializationIsRejected()
    {
        using TestEnvironment environment = new();
        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);
        InitializeProjectCommand command = new(
            environment.ProjectRoot,
            true,
            snapshot.StateVersion,
            ForgeApplication.InitializationKey(snapshot));
        await environment.Application.InitializeProjectAsync(
            command,
            TestContext.Current.CancellationToken);

        InitializeProjectResult replay = await environment.Application.InitializeProjectAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.False(replay.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, replay.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancellationLeavesNoStagingTree()
    {
        using TestEnvironment environment = new();
        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => environment.Application.InitializeProjectAsync(
                new(
                    environment.ProjectRoot,
                    true,
                    snapshot.StateVersion,
                    ForgeApplication.InitializationKey(snapshot)),
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
            "false",
            TestContext.Current.CancellationToken);
        ConfigurationView values = await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(values.Values
            .Single(value => value.Key == "interaction.confirm_destructive")
            .Value
            .GetBoolean());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StringTypedKeysKeepLiteralLookingText()
    {
        using TestEnvironment environment = new();

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            "ru",
            TestContext.Current.CancellationToken);
        ConfigurationView values = await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "ru",
            values.Values.Single(value => value.Key == "language.ui").Value.GetString());
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
            "ru",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationKeyUnknown, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CorruptUserConfigurationIsReportedInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        await WriteCorruptUserConfigurationAsync(environment);

        ConfigurationWriteResult result = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);
        ConfigurationView values = await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, result.DiagnosticCode);
        Assert.Empty(values.Values);
        Assert.Equal(DiagnosticCodes.ConfigurationInvalid, values.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveryQuarantinesUnreadableConfiguration()
    {
        using TestEnvironment environment = new();
        string path = await WriteCorruptUserConfigurationAsync(environment);

        RecoverStartupResult refused = await environment.Application.RecoverStartupAsync(
            null,
            false,
            TestContext.Current.CancellationToken);
        RecoverStartupResult recovered = await environment.Application.RecoverStartupAsync(
            null,
            true,
            TestContext.Current.CancellationToken);
        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticCodes.ConfirmationRequired, refused.DiagnosticCode);
        Assert.True(recovered.Succeeded);
        Assert.Equal(StartupCheckId.UserConfiguration, recovered.Check);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists($"{path}{StartupRecovery.QuarantineSuffix}"));
        Assert.Null(status.FirstFailure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveryIsUnavailableForAnUnsupportedPlatform()
    {
        using TestEnvironment environment = new(new UnsupportedPlatformPreflight());

        RecoverStartupResult result = await environment.Application.RecoverStartupAsync(
            null,
            true,
            TestContext.Current.CancellationToken);
        ProjectStatusSnapshot snapshot = await environment.Application.GetProjectSnapshotAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.PlatformNotSupported, result.DiagnosticCode);
        Assert.Empty(snapshot.SuggestedActions);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveryReportsNothingToRepairForAHealthyStartup()
    {
        using TestEnvironment environment = new();

        RecoverStartupResult result = await environment.Application.RecoverStartupAsync(
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.Check);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveryKeepsEveryQuarantinedRevision()
    {
        using TestEnvironment environment = new();
        string path = await WriteCorruptUserConfigurationAsync(environment);
        await environment.Application.RecoverStartupAsync(
            null,
            true,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "{broken again", TestContext.Current.CancellationToken);

        await environment.Application.RecoverStartupAsync(
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "{broken",
            await File.ReadAllTextAsync(
                $"{path}{StartupRecovery.QuarantineSuffix}",
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "{broken again",
            await File.ReadAllTextAsync(
                $"{path}{StartupRecovery.QuarantineSuffix}.1",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoveryNeverTouchesAReadableFile()
    {
        using TestEnvironment environment = new();
        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            "ru",
            TestContext.Current.CancellationToken);
        string path = ConfigurationStoreFactory.UserPath(environment.LocalApplicationData);
        string before = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        RecoverStartupResult result = await environment.Application.RecoverStartupAsync(
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.Check);
        Assert.Equal(
            before,
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists($"{path}{StartupRecovery.QuarantineSuffix}"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DisabledDestructiveConfirmationInitializesWithoutExplicitConfirmation()
    {
        using TestEnvironment environment = new();
        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "interaction.confirm_destructive",
            "false",
            TestContext.Current.CancellationToken);

        InitializeProjectResult result = await environment.InitializeAsync(
            environment.ProjectRoot,
            false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(ProjectRootResolver.ManifestPath(environment.ProjectRoot)));
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

    private static async Task<string> WriteCorruptUserConfigurationAsync(TestEnvironment environment)
    {
        string path = ConfigurationStoreFactory.UserPath(environment.LocalApplicationData);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);
        return path;
    }
}
