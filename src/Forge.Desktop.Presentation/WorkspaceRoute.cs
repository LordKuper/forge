namespace Forge.Desktop.Presentation;

/// <summary>Plan section 4's four content-area destinations, plus the empty "no project cataloged
/// yet" state a fresh installation starts in.</summary>
public enum WorkspacePage
{
    Empty,
    ForgeSettings,
    ProjectOverview,
    ProjectSettings,
    SprintWorkspace,
}

/// <summary>
/// <see cref="WorkspaceViewModel"/>'s selected destination and context. A project-scoped page
/// carries both <see cref="ProjectId"/> (the catalog/manifest identity) and <see cref="ProjectRoot"/>
/// (what every existing <c>ForgeApplication</c> query and <see cref="MainPageViewModel"/> method
/// actually takes) so navigating never needs a second lookup back into the catalog.
/// </summary>
public sealed record WorkspaceRoute(WorkspacePage Page, Guid? ProjectId, string? ProjectRoot, Guid? SprintId)
{
    public static WorkspaceRoute Empty { get; } = new(WorkspacePage.Empty, null, null, null);

    public static WorkspaceRoute ToForgeSettings() => new(WorkspacePage.ForgeSettings, null, null, null);

    public static WorkspaceRoute ToProjectOverview(Guid projectId, string projectRoot) =>
        new(WorkspacePage.ProjectOverview, projectId, projectRoot, null);

    public static WorkspaceRoute ToProjectSettings(Guid projectId, string projectRoot) =>
        new(WorkspacePage.ProjectSettings, projectId, projectRoot, null);

    public static WorkspaceRoute ToSprintWorkspace(Guid projectId, string projectRoot, Guid sprintId) =>
        new(WorkspacePage.SprintWorkspace, projectId, projectRoot, sprintId);

    /// <summary>The machine token <see cref="WorkspaceViewModel"/> persists through
    /// <c>ProjectCatalogStore.SelectAsync</c>'s own <c>route</c> field. <see langword="null"/> for
    /// <see cref="WorkspacePage.SprintWorkspace"/>: that route is already fully identified by the
    /// catalog entry's own <c>LastSelectedSprintId</c> field, so no separate route token is needed to
    /// tell it apart from <see cref="WorkspacePage.ProjectOverview"/> on restore.</summary>
    public string? RouteToken => Page switch
    {
        WorkspacePage.ProjectSettings => RouteTokens.ProjectSettings,
        _ => null,
    };
}

/// <summary>Machine tokens for <see cref="WorkspaceRoute.RouteToken"/> -- kept in one place so a
/// reader and a writer of `catalog.json`'s <c>route</c> field cannot silently desync.</summary>
public static class RouteTokens
{
    public const string ProjectSettings = "project_settings";
}
