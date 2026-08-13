using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class StartupTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnsupportedPlatformFailsStartupBeforeAnyMutation()
    {
        using TestEnvironment environment = new(new UnsupportedPlatformPreflight());

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(StartupState.Failed, status.State);
        Assert.False(status.AllowsSprintWork);
        Assert.Equal(
            DiagnosticCodes.PlatformNotSupported,
            Check(status, StartupCheckId.UpdateStrategy).DiagnosticCode);
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PendingProviderToolchainKeepsSprintWorkBlocked()
    {
        using TestEnvironment environment = new();

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(StartupState.Blocked, status.State);
        Assert.False(status.AllowsSprintWork);
        Assert.Equal(
            DiagnosticCodes.ProviderPreflightPending,
            Check(status, StartupCheckId.Providers).DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartupChecksRunInContractOrder()
    {
        using TestEnvironment environment = new();

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                StartupCheckId.UserConfiguration,
                StartupCheckId.Language,
                StartupCheckId.Platform,
                StartupCheckId.UpdateStrategy,
                StartupCheckId.Release,
                StartupCheckId.Providers,
                StartupCheckId.ProjectRoot,
                StartupCheckId.ProjectConfiguration,
            ],
            status.Checks.Select(check => check.Id));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LanguagesFallBackToEnglishAndInheritTheUserInterface()
    {
        using TestEnvironment environment = new();
        StartupStatus initial = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(new("en", "en", "en"), initial.Language);

        await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "language.ui",
            JsonSerializer.SerializeToElement("ru"),
            TestContext.Current.CancellationToken);

        StartupStatus updated = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(new("ru", "ru", "ru"), updated.Language);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MalformedUserConfigurationFailsStartupWithoutOverwritingIt()
    {
        using TestEnvironment environment = new();
        string path = ConfigurationStoreFactory.UserPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(StartupState.Failed, status.State);
        Assert.Equal(
            DiagnosticCodes.ConfigurationInvalid,
            Check(status, StartupCheckId.UserConfiguration).DiagnosticCode);
        Assert.Equal(
            "{broken",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExistingPreInstanceIsolationConfigurationFileIsMigratedIntoTheNewInstanceScopedPath()
    {
        using TestEnvironment environment = new();
        string legacyPath = Path.Combine(environment.LocalApplicationData, "Forge", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        const string content = """{"schema_version":"1.0.0","language":{"ui":"ru"},"interaction":{}}""";
        await File.WriteAllTextAsync(legacyPath, content, TestContext.Current.CancellationToken);

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null,
            TestContext.Current.CancellationToken);

        string currentPath = ConfigurationStoreFactory.UserPath(environment);
        Assert.NotEqual(legacyPath, currentPath);
        Assert.True(File.Exists(currentPath), $"'{currentPath}' should have been migrated from the legacy path.");
        Assert.Equal(content, await File.ReadAllTextAsync(currentPath, TestContext.Current.CancellationToken));
        // Copied, not moved: the legacy file survives so a rollback or a concurrently starting
        // instance is never disrupted.
        Assert.True(File.Exists(legacyPath));
        Assert.Equal(StartupCheckState.Passed, Check(status, StartupCheckId.UserConfiguration).State);
    }

    private static StartupCheck Check(StartupStatus status, StartupCheckId id) =>
        status.Checks.Single(check => check.Id == id);
}
