using Forge.Application;
using Forge.Configuration;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 6.1's user-scoped project catalog (ADR 0043/0049). Confirms real add/list/
/// relink/alias/remove/select behavior -- including that none of it ever touches the project's own
/// `.forge/` directory -- and that a catalog entry survives a fresh store instance over the same
/// paths (simulating a process restart), before the smallest risk-based tests were added.</summary>
public sealed class ProjectCatalogStoreTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddingAnUninitializedRootIsRefusedWithoutTouchingTheCatalog()
    {
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();

        ProjectCatalogResult result = await catalog.AddAsync(
            environment.ProjectRoot, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectNotInitialized, result.DiagnosticCode);
        Assert.Empty((await catalog.ListAsync(TestContext.Current.CancellationToken)).Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddListAliasAndRemoveOnlyEverTouchTheLocalCatalogFile()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        string forgeDirectory = ProjectRootResolver.ForgeDirectory(environment.ProjectRoot);
        DateTime forgeDirectoryWriteTimeBefore = Directory.GetLastWriteTimeUtc(forgeDirectory);

        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(added.Succeeded);
        Guid projectId = added.Entry!.ProjectId;
        Assert.Equal(environment.ProjectRoot, added.Entry.Root);
        Assert.Null(added.Entry.Alias);

        ProjectCatalogEntry listed = Assert.Single((await catalog.ListAsync(cancellationToken)).Entries);
        Assert.Equal(projectId, listed.ProjectId);

        ProjectCatalogResult duplicate = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Assert.False(duplicate.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectCatalogEntryExists, duplicate.DiagnosticCode);

        ProjectCatalogResult aliased = await catalog.SetAliasAsync(projectId, "My Project", cancellationToken);
        Assert.True(aliased.Succeeded);
        Assert.Equal("My Project", aliased.Entry!.Alias);

        ProjectCatalogResult cleared = await catalog.SetAliasAsync(projectId, "  ", cancellationToken);
        Assert.True(cleared.Succeeded);
        Assert.Null(cleared.Entry!.Alias);

        ProjectCatalogResult removed = await catalog.RemoveAsync(projectId, cancellationToken);
        Assert.True(removed.Succeeded);
        Assert.Empty((await catalog.ListAsync(cancellationToken)).Entries);

        // None of the catalog operations above ever wrote to the project's own `.forge/` directory.
        Assert.Equal(forgeDirectoryWriteTimeBefore, Directory.GetLastWriteTimeUtc(forgeDirectory));
        Assert.True(Directory.Exists(environment.ProjectRoot));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemovingAnUnknownEntryIsRejectedWithoutSideEffects()
    {
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();

        ProjectCatalogResult result = await catalog.RemoveAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectCatalogEntryNotFound, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RelinkVerifiesTheManifestProjectIdAtTheNewRootBeforeAccepting()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Guid projectId = added.Entry!.ProjectId;

        string movedRoot = Path.Combine(environment.Root, "moved-project");
        Directory.Move(environment.ProjectRoot, movedRoot);
        try
        {
            // The claimed id does not match what the manifest at the new root actually says.
            ProjectCatalogResult mismatch =
                await catalog.RelinkAsync(Guid.NewGuid(), movedRoot, cancellationToken);
            Assert.False(mismatch.Succeeded);
            Assert.Equal(DiagnosticCodes.ProjectCatalogEntryNotFound, mismatch.DiagnosticCode);

            ProjectCatalogResult relinked = await catalog.RelinkAsync(projectId, movedRoot, cancellationToken);
            Assert.True(relinked.Succeeded);
            Assert.Equal(movedRoot, relinked.Entry!.Root);

            ProjectCatalogEntry listed = Assert.Single((await catalog.ListAsync(cancellationToken)).Entries);
            Assert.Equal(movedRoot, listed.Root);
        }
        finally
        {
            Directory.Move(movedRoot, environment.ProjectRoot);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RelinkRejectsARootWhoseManifestBelongsToADifferentProject()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Guid projectId = added.Entry!.ProjectId;

        // A second, genuinely different initialized project.
        string otherRoot = Path.Combine(environment.Root, "other-project");
        Directory.CreateDirectory(otherRoot);
        await environment.InitializeAsync(otherRoot, true, cancellationToken);

        ProjectCatalogResult mismatch = await catalog.RelinkAsync(projectId, otherRoot, cancellationToken);

        Assert.False(mismatch.Succeeded);
        Assert.Equal(DiagnosticCodes.ProjectCatalogRelinkMismatch, mismatch.DiagnosticCode);
        ProjectCatalogEntry listed = Assert.Single((await catalog.ListAsync(cancellationToken)).Entries);
        Assert.Equal(environment.ProjectRoot, listed.Root);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SelectingRecordsTheLastSprintAndRouteAndSurvivesAFreshStoreInstance()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Guid projectId = added.Entry!.ProjectId;
        Guid sprintId = Guid.NewGuid();

        ProjectCatalogResult selected =
            await catalog.SelectAsync(projectId, sprintId, "sprint_workspace", cancellationToken);
        Assert.True(selected.Succeeded);
        Assert.Equal(sprintId, selected.Entry!.LastSelectedSprintId);
        Assert.Equal("sprint_workspace", selected.Entry.LastRoute);

        // A brand-new store instance over the exact same paths, sharing no in-memory state with
        // `catalog` above -- the only way this test can prove the selection was durable rather than
        // merely held in `catalog`'s own object (simulating a Host/CLI process restart, matching
        // acceptance 12.1's "last valid route... survive restart").
        ProjectCatalogStore restarted = new(
            environment,
            environment.Resolve<IClock>(),
            environment.Resolve<ProjectRootResolver>(),
            environment.Resolve<IConfigurationRegistry>());

        ProjectCatalogEntry persisted = Assert.Single((await restarted.ListAsync(cancellationToken)).Entries);
        Assert.Equal(sprintId, persisted.LastSelectedSprintId);
        Assert.Equal("sprint_workspace", persisted.LastRoute);
    }

    // Regression (PR #97 review, finding 3): every mutating method here does a plain
    // read-modify-write with nothing serializing the pair, and ProjectCatalogStore is registered as
    // a DI singleton, so concurrent calls could interleave into a lost update. Adding several
    // distinct, already-initialized projects concurrently against the exact same store instance
    // must never lose one -- reliably reproducible without the per-path lock at this concurrency
    // level (real async file I/O yields control between the read and the write).
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentAddCallsForDistinctProjectsNeverLoseAnUpdate()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        const int projectCount = 8;
        string[] roots = new string[projectCount];
        for (int i = 0; i < projectCount; i++)
        {
            roots[i] = Path.Combine(environment.Root, $"project-{i}");
            Directory.CreateDirectory(roots[i]);
            await environment.InitializeAsync(roots[i], true, cancellationToken);
        }

        ProjectCatalogResult[] results =
            await Task.WhenAll(roots.Select(root => catalog.AddAsync(root, cancellationToken)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        IReadOnlyList<ProjectCatalogEntry> entries = (await catalog.ListAsync(cancellationToken)).Entries;
        Assert.Equal(projectCount, entries.Count);
        Assert.Equal(projectCount, entries.Select(entry => entry.ProjectId).Distinct().Count());
    }

    // Regression (PR #97 review, finding 4): a corrupt primary file must recover from its
    // `.previous` sibling (the same convention JsonConfigurationStore already establishes) instead
    // of throwing an unhandled exception out of every catalog operation.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACorruptCatalogRecoversFromItsPreviousSiblingInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Guid projectId = added.Entry!.ProjectId;
        // The first write has nothing to replace (no `.previous` yet); a second write does.
        await catalog.SetAliasAsync(projectId, "My Project", cancellationToken);
        string path = ProjectCatalogStore.CatalogPath(environment);
        Assert.True(File.Exists($"{path}.previous"));

        await File.WriteAllTextAsync(path, "{ not valid json", cancellationToken);

        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken);

        Assert.Equal(DiagnosticCodes.None, listing.DiagnosticCode);
        Assert.Single(listing.Entries);
    }

    // Regression (PR #97 review, finding 4): with no `.previous` to fall back to, a corrupt catalog
    // must report a clean diagnostic, never an unhandled exception.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACorruptCatalogWithNoPreviousSiblingReportsUnreadableInsteadOfThrowing()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        string path = ProjectCatalogStore.CatalogPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not valid json", cancellationToken);

        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken);

        Assert.Equal(DiagnosticCodes.ProjectCatalogUnreadable, listing.DiagnosticCode);
        Assert.Empty(listing.Entries);
    }

    // Regression (PR #97 review, finding 5): schema_version is written but must also be validated
    // on read -- a newer/unrecognized version must fail closed, never be silently downgraded (which
    // would permanently drop whatever a newer schema added the moment this build next writes).
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AFutureSchemaVersionCatalogFailsClosedInsteadOfBeingSilentlyDowngraded()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string path = ProjectCatalogStore.CatalogPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {"schema_version":"2.0.0","entries":[{"project_id":"11111111-1111-1111-1111-111111111111",
            "root":"C:\\future","alias":null,"last_opened_at":"2026-01-01T00:00:00+00:00",
            "last_selected_sprint_id":null,"last_route":null}]}
            """,
            cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();

        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken);

        Assert.Equal(DiagnosticCodes.ProjectCatalogUnreadable, listing.DiagnosticCode);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SelectingWithNoSprintClearsAPreviousSprintSelectionForAProjectLevelRoute()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        Guid projectId = added.Entry!.ProjectId;
        await catalog.SelectAsync(projectId, Guid.NewGuid(), "sprint_workspace", cancellationToken);

        ProjectCatalogResult result = await catalog.SelectAsync(projectId, null, "project_overview", cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.Entry!.LastSelectedSprintId);
        Assert.Equal("project_overview", result.Entry.LastRoute);
    }
}
