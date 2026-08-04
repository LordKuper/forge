using Forge.Application;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProjectInitializationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RelativeRootIsRejected()
    {
        using TestEnvironment environment = new();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new("relative/path", true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectRootNotAbsolute, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingRootIsRejected()
    {
        using TestEnvironment environment = new();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(Path.Combine(environment.Root, "absent"), true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectRootMissing, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InitializationRequiresConfirmationAndCreatesNothing()
    {
        using TestEnvironment environment = new();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, false),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfirmationRequired, result.DiagnosticCode);
        Assert.Equal(environment.ProjectRoot, result.Root);
        Assert.Empty(Directory.GetFileSystemEntries(environment.ProjectRoot));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConfirmedInitializationPublishesTheMinimalTreeWithoutStagingLeftovers()
    {
        using TestEnvironment environment = new();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.ProjectId);
        Assert.True(File.Exists(ProjectRootResolver.ManifestPath(environment.ProjectRoot)));
        Assert.True(File.Exists(Path.Combine(
            ProjectRootResolver.ForgeDirectory(environment.ProjectRoot),
            "workflows",
            "implementation-critical.yaml")));
        Assert.Empty(Directory.GetDirectories(environment.ProjectRoot, ".forge.staging-*"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RepeatedInitializationIsIdempotent()
    {
        using TestEnvironment environment = new();
        await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true),
            TestContext.Current.CancellationToken);
        string manifest = await File.ReadAllTextAsync(
            ProjectRootResolver.ManifestPath(environment.ProjectRoot),
            TestContext.Current.CancellationToken);

        InitializeProjectResult repeated = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true),
            TestContext.Current.CancellationToken);

        Assert.True(repeated.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectAlreadyInitialized, repeated.DiagnosticCode);
        Assert.Equal(
            manifest,
            await File.ReadAllTextAsync(
                ProjectRootResolver.ManifestPath(environment.ProjectRoot),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnknownForgeDirectoryIsNeverOverwritten()
    {
        using TestEnvironment environment = new();
        string forge = ProjectRootResolver.ForgeDirectory(environment.ProjectRoot);
        Directory.CreateDirectory(forge);
        string foreign = Path.Combine(forge, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "keep", TestContext.Current.CancellationToken);

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectDirectoryUnknown, result.DiagnosticCode);
        Assert.Equal(
            "keep",
            await File.ReadAllTextAsync(foreign, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ProjectRootResolver.ManifestPath(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolutionNeverSearchesUpward()
    {
        using TestEnvironment environment = new();
        await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true),
            TestContext.Current.CancellationToken);
        string child = Path.Combine(environment.ProjectRoot, "src");
        Directory.CreateDirectory(child);

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            child,
            TestContext.Current.CancellationToken);

        Assert.False(status.Project.Initialized);
        Assert.Equal(DiagnosticCodes.ProjectNotInitialized, status.Project.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StaleExpectedStateVersionIsRejectedWithoutSideEffect()
    {
        using TestEnvironment environment = new();

        InitializeProjectResult result = await environment.Application.InitializeProjectAsync(
            new(environment.ProjectRoot, true, 7),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.SuggestionStale, result.DiagnosticCode);
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }
}
