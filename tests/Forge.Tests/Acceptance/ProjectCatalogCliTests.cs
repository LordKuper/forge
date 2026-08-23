using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Plan section 6.1's user-scoped project catalog, wired to `forge project`. No capability
/// id and no Host protocol surface (ADR 0043/0049): every subcommand here reads or writes only the
/// local catalog file, never the project's own `.forge/` directory.</summary>
public sealed class ProjectCatalogCliTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AddListAliasAndRemoveRoundTripThroughTheCliWithoutTouchingRepositoryData()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog localization = new();
        SurfaceText text = new(localization, CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(
            text, output, environment.Application, catalog: catalog);

        int addExitCode = await root.Parse(["project", "add", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        Assert.Equal(0, addExitCode);
        Assert.Contains(text.Resolve(MessageKeys.ProjectAdded), output.ToString(), StringComparison.Ordinal);
        ProjectCatalogEntry entry = Assert.Single(await catalog.ListAsync(cancellationToken));

        output.GetStringBuilder().Clear();
        int listExitCode = await root.Parse(["project", "list", "--json"])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        Assert.Equal(0, listExitCode);
        Assert.Contains(entry.ProjectId.ToString(), output.ToString(), StringComparison.OrdinalIgnoreCase);

        output.GetStringBuilder().Clear();
        int aliasExitCode = await root
            .Parse(["project", "alias", entry.ProjectId.ToString(), "My Project"])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        Assert.Equal(0, aliasExitCode);
        Assert.Equal("My Project", (await catalog.ListAsync(cancellationToken))[0].Alias);

        output.GetStringBuilder().Clear();
        int removeExitCode = await root.Parse(["project", "remove", entry.ProjectId.ToString()])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        Assert.Equal(0, removeExitCode);
        Assert.Empty(await catalog.ListAsync(cancellationToken));
        // The repository itself was never touched by any of the catalog commands above.
        Assert.True(Directory.Exists(Path.Combine(environment.ProjectRoot, ".forge")));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task RelinkThroughTheCliRejectsAMismatchedManifestProjectId()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string otherRoot = Path.Combine(environment.Root, "other-project");
        Directory.CreateDirectory(otherRoot);
        await environment.InitializeAsync(otherRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog localization = new();
        SurfaceText text = new(localization, CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(
            text, output, environment.Application, diagnostics, catalog: catalog);

        int exitCode = await root
            .Parse(["project", "relink", added.Entry!.ProjectId.ToString(), otherRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(
            DiagnosticCodes.ProjectCatalogRelinkMismatch, diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SelectRecordsTheLastSprintAndRoute()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Guid sprintId = Guid.NewGuid();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog localization = new();
        SurfaceText text = new(localization, CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(
            text, output, environment.Application, catalog: catalog);

        int exitCode = await root
            .Parse([
                "project", "select", added.Entry!.ProjectId.ToString(),
                "--sprint", sprintId.ToString(), "--route", "sprint_workspace",
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        ProjectCatalogEntry entry = Assert.Single(await catalog.ListAsync(cancellationToken));
        Assert.Equal(sprintId, entry.LastSelectedSprintId);
        Assert.Equal("sprint_workspace", entry.LastRoute);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ProjectCommandsAreOmittedWhenNoCatalogIsWired()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        Assert.DoesNotContain(root.Subcommands, command => command.Name == "project");
    }
}
