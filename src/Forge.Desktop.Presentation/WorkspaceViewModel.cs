using Forge.Application;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 9.3's top-level routing/selected-context state. Neither decides workflow policy nor
/// talks to a Host directly -- it only chooses which page is active and persists that choice through
/// <see cref="ProjectCatalogStore.SelectAsync"/> (Slice 4's own <c>LastSelectedSprintId</c>/
/// <c>LastRoute</c> fields), so the last valid route survives a restart (plan 12.1).
/// </summary>
public sealed class WorkspaceViewModel(ProjectCatalogStore catalog, ForgeApplication application)
{
    private readonly ProjectCatalogStore catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));

    public WorkspaceRoute Route { get; private set; } = WorkspaceRoute.Empty;

    /// <summary>Raised whenever <see cref="Route"/> changes, whether from
    /// <see cref="NavigateAsync"/> or <see cref="RestoreAsync"/> -- the shell page's one hook to
    /// swap its displayed content.</summary>
    public event EventHandler? RouteChanged;

    /// <summary>Resolves the route to show on launch: the most recently opened cataloged project's
    /// last sprint (if it still exists) or last route, falling back to
    /// <see cref="WorkspaceRoute.Empty"/> when the catalog itself is empty. A project-agnostic
    /// "Forge settings was left open" choice cannot be restored -- <c>ProjectCatalogEntry</c> only
    /// ever records a per-project route (ADR 0049), so that specific case degrades to the owning
    /// project's own last route instead of losing the selection entirely.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        ProjectCatalogEntry? mostRecent = listing.Entries
            .OrderByDescending(entry => entry.LastOpenedAt)
            .FirstOrDefault();
        if (mostRecent is null)
        {
            SetRoute(WorkspaceRoute.Empty);
            return;
        }

        if (mostRecent.LastSelectedSprintId is { } sprintId &&
            await SprintExistsAsync(mostRecent.Root, sprintId, cancellationToken).ConfigureAwait(false))
        {
            SetRoute(WorkspaceRoute.ToSprintWorkspace(mostRecent.ProjectId, mostRecent.Root, sprintId));
            return;
        }

        SetRoute(mostRecent.LastRoute == RouteTokens.ProjectSettings
            ? WorkspaceRoute.ToProjectSettings(mostRecent.ProjectId, mostRecent.Root)
            : WorkspaceRoute.ToProjectOverview(mostRecent.ProjectId, mostRecent.Root));
    }

    /// <summary>Navigates and durably records the selection for the next restore, for every
    /// project-scoped route. <see cref="WorkspaceRoute.ToForgeSettings"/> and
    /// <see cref="WorkspaceRoute.Empty"/> carry no project id, so nothing is persisted for them --
    /// see <see cref="RestoreAsync"/>'s own remarks on that gap.</summary>
    public async Task NavigateAsync(WorkspaceRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        SetRoute(route);
        if (route.ProjectId is { } projectId)
        {
            await catalog.SelectAsync(projectId, route.SprintId, route.RouteToken, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void SetRoute(WorkspaceRoute route)
    {
        Route = route;
        RouteChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> SprintExistsAsync(string root, Guid sprintId, CancellationToken cancellationToken)
    {
        ProjectSnapshot snapshot = await application
            .GetProjectSnapshotAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Sprints.Any(sprint => sprint.Id == sprintId);
    }
}
