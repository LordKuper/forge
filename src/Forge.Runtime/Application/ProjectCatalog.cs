using System.Text.Json;
using Forge.Configuration;

namespace Forge.Application;

/// <summary>
/// Plan section 6.1's user-scoped project catalog row. Keyed by the project's own durable
/// <see cref="ProjectId"/> -- never a second, catalog-local identity -- so an entry only exists for
/// an already-initialized project (<see cref="ProjectCatalogStore.AddAsync"/>). <see cref="Alias"/>
/// is local display text only; it never modifies the project's own manifest.
/// </summary>
public sealed record ProjectCatalogEntry(
    Guid ProjectId,
    string Root,
    string? Alias,
    DateTimeOffset LastOpenedAt,
    Guid? LastSelectedSprintId,
    string? LastRoute);

public sealed record ProjectCatalogResult(bool Succeeded, ProjectCatalogEntry? Entry, string DiagnosticCode)
{
    public static ProjectCatalogResult Fail(string diagnosticCode) => new(false, null, diagnosticCode);
}

/// <summary>
/// User-scoped, Desktop-installation-local persistence for <see cref="ProjectCatalogEntry"/> rows.
/// Stored beside the existing user configuration file (<see cref="ConfigurationStoreFactory.UserPath"/>'s
/// own instance-scoped directory under <see cref="IEnvironmentPaths.LocalApplicationData"/>) --
/// never inside any project's own `.forge/` directory (ADR 0043/0049; plan section 6.1: "outside
/// project state"). Every operation here reads or writes only this one file: adding, removing,
/// relinking, aliasing, or selecting a catalog entry never touches repository data.
/// </summary>
public sealed class ProjectCatalogStore(
    IEnvironmentPaths paths,
    IClock clock,
    ProjectRootResolver rootResolver,
    IConfigurationRegistry registry)
{
    public const string ContractVersion = "1.0.0";
    public const int MaxAliasLength = 200;
    public const int MaxRouteLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static string CatalogPath(IEnvironmentPaths environmentPaths)
    {
        ArgumentNullException.ThrowIfNull(environmentPaths);
        return Path.Combine(
            environmentPaths.LocalApplicationData, "Forge", environmentPaths.InstanceId, "catalog.json");
    }

    public async Task<IReadOnlyList<ProjectCatalogEntry>> ListAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken).ConfigureAwait(false)).Entries;

    /// <summary>Adds a catalog row for an already-initialized project, anchored on its manifest's
    /// own <c>project_id</c> (plan section 6.1: "stable project ID when initialized"). Never
    /// initializes a project itself -- a caller targeting an uninitialized root is told to run
    /// `forge init` first rather than this silently doing it.</summary>
    public async Task<ProjectCatalogResult> AddAsync(string? root, CancellationToken cancellationToken)
    {
        ProjectRootStatus status = await rootResolver.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return ProjectCatalogResult.Fail(
                status.DiagnosticCode == DiagnosticCodes.None
                    ? DiagnosticCodes.ProjectNotInitialized
                    : status.DiagnosticCode);
        }

        Guid projectId = await ProjectIdentity.ReadProjectIdAsync(status.Root, registry, cancellationToken)
            .ConfigureAwait(false);
        Persisted persisted = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (persisted.Entries.Any(entry => entry.ProjectId == projectId))
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryExists);
        }

        ProjectCatalogEntry created = new(projectId, status.Root, null, clock.UtcNow, null, null);
        persisted.Entries.Add(created);
        await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
        return new(true, created, DiagnosticCodes.None);
    }

    /// <summary>Removes the catalog row only. Never deletes the repository or its `.forge/`
    /// directory (plan section 6.1/12.1).</summary>
    public async Task<ProjectCatalogResult> RemoveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        Persisted persisted = await ReadAsync(cancellationToken).ConfigureAwait(false);
        ProjectCatalogEntry? existing = persisted.Entries.FirstOrDefault(entry => entry.ProjectId == projectId);
        if (existing is null)
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
        }

        persisted.Entries.Remove(existing);
        await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
        return new(true, existing, DiagnosticCodes.None);
    }

    /// <summary>Relinks a moved project: verifies the manifest's own `project_id` at
    /// <paramref name="newRoot"/> matches <paramref name="projectId"/> before accepting the new root
    /// (plan section 6.1) -- never trusts the caller's claim that the two refer to the same
    /// project.</summary>
    public async Task<ProjectCatalogResult> RelinkAsync(
        Guid projectId, string? newRoot, CancellationToken cancellationToken)
    {
        Persisted persisted = await ReadAsync(cancellationToken).ConfigureAwait(false);
        int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
        if (index < 0)
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
        }

        ProjectRootStatus status = await rootResolver.ResolveAsync(newRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return ProjectCatalogResult.Fail(
                status.DiagnosticCode == DiagnosticCodes.None
                    ? DiagnosticCodes.ProjectNotInitialized
                    : status.DiagnosticCode);
        }

        Guid actualProjectId = await ProjectIdentity.ReadProjectIdAsync(status.Root, registry, cancellationToken)
            .ConfigureAwait(false);
        if (actualProjectId != projectId)
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogRelinkMismatch);
        }

        ProjectCatalogEntry updated = persisted.Entries[index] with { Root = status.Root, LastOpenedAt = clock.UtcNow };
        persisted.Entries[index] = updated;
        await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
        return new(true, updated, DiagnosticCodes.None);
    }

    /// <summary>Sets or clears (empty/whitespace input) the local display alias. Never touches the
    /// project's own manifest (plan section 4.2: "a local alias belongs to the user project catalog
    /// and does not modify the repository manifest").</summary>
    public async Task<ProjectCatalogResult> SetAliasAsync(
        Guid projectId, string? alias, CancellationToken cancellationToken)
    {
        if (alias is { Length: > MaxAliasLength })
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogAliasTooLong);
        }

        Persisted persisted = await ReadAsync(cancellationToken).ConfigureAwait(false);
        int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
        if (index < 0)
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
        }

        ProjectCatalogEntry updated = persisted.Entries[index] with
        {
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias,
        };
        persisted.Entries[index] = updated;
        await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
        return new(true, updated, DiagnosticCodes.None);
    }

    /// <summary>Records the last selected sprint/route for a catalog entry and bumps
    /// <see cref="ProjectCatalogEntry.LastOpenedAt"/> (plan section 11 Slice 4 item 3) -- the
    /// plumbing a navigation shell calls on every selection so the last valid route survives a
    /// restart (plan section 12.1). <paramref name="sprintId"/> is <see langword="null"/> for a
    /// project-level route (no sprint selected).</summary>
    public async Task<ProjectCatalogResult> SelectAsync(
        Guid projectId, Guid? sprintId, string? route, CancellationToken cancellationToken)
    {
        if (route is { Length: > MaxRouteLength })
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogRouteTooLong);
        }

        Persisted persisted = await ReadAsync(cancellationToken).ConfigureAwait(false);
        int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
        if (index < 0)
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
        }

        ProjectCatalogEntry updated = persisted.Entries[index] with
        {
            LastOpenedAt = clock.UtcNow,
            LastSelectedSprintId = sprintId,
            LastRoute = string.IsNullOrWhiteSpace(route) ? null : route,
        };
        persisted.Entries[index] = updated;
        await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
        return new(true, updated, DiagnosticCodes.None);
    }

    private async Task<Persisted> ReadAsync(CancellationToken cancellationToken)
    {
        string path = CatalogPath(paths);
        if (!File.Exists(path))
        {
            return new();
        }

        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        Persisted? persisted = await JsonSerializer
            .DeserializeAsync<Persisted>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return persisted ?? new();
    }

    private async Task WriteAsync(Persisted persisted, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
        // Reuses the same atomic temp-file-then-replace primitive user configuration already relies
        // on (Forge.Configuration.AtomicConfigurationFile) rather than a second durability mechanism
        // for what is, functionally, another piece of user-scoped local state.
        await AtomicConfigurationFile.WriteAsync(CatalogPath(paths), bytes, cancellationToken, retainPrevious: false)
            .ConfigureAwait(false);
    }

    private sealed class Persisted
    {
        public string SchemaVersion { get; set; } = ContractVersion;

        public List<ProjectCatalogEntry> Entries { get; set; } = [];
    }
}
