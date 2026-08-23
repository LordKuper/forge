using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop;

/// <summary>
/// The Windows adapter's own fixed two-panel workspace shell (plan section 4). Every routing,
/// ordering, validation, and delegation decision lives in the neutral view-models this page only
/// renders (<see cref="WorkspaceViewModel"/>, <see cref="SidebarViewModel"/>,
/// <see cref="ProjectOverviewViewModel"/>, <see cref="ForgeSettingsViewModel"/>,
/// <see cref="ProjectSettingsViewModel"/>, <see cref="SprintWorkspaceViewModel"/>) -- this class only
/// builds controls and assigns their properties from what those view-models already computed.
/// </summary>
public partial class WorkspaceShellPage : ContentPage
{
    private readonly SurfaceTextProvider text;
    private readonly ForgeApplication application;
    private readonly ProjectCatalogStore catalog;
    private readonly ProviderCatalog providerCatalog;
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations;
    private readonly WorkspaceViewModel workspace;
    private readonly SidebarViewModel sidebar;
    private readonly ShellRenderGate renderGate;
    /// <summary>Set right before a sidebar re-render whose triggering action (add/remove project)
    /// has a result worth telling the user about (PR #98 review finding 3); read once by
    /// <see cref="RenderSidebarAsync"/> and left in place until the next add/remove so it survives
    /// that render instead of a stale value flashing and vanishing.</summary>
    private string? sidebarNotice;
    private MainPageViewModel legacy;
    private ProjectOverviewViewModel projectOverview;
    private ProjectSettingsViewModel projectSettings;
    private SprintWorkspaceViewModel sprintWorkspace;
    private ForgeSettingsViewModel forgeSettings;

    public WorkspaceShellPage(
        SurfaceTextProvider text,
        ForgeApplication application,
        ProjectCatalogStore catalog,
        ProviderCatalog providerCatalog,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        IFolderPickerPort folderPicker)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(resolveMutations);
        ArgumentNullException.ThrowIfNull(folderPicker);
        InitializeComponent();
        this.text = text;
        this.application = application;
        this.catalog = catalog;
        this.providerCatalog = providerCatalog;
        this.resolveMutations = resolveMutations;
        workspace = new(catalog, application);
        sidebar = new(catalog, application, folderPicker, text);
        forgeSettings = new(application, providerCatalog, text);
        (legacy, projectOverview, projectSettings, sprintWorkspace) = BuildLegacyDependents(folderPicker);
        renderGate = new(RenderSidebarAsync, RenderContentAsync);
        // PR #98 review finding 1: NavigateAsync raises RouteChanged synchronously from inside the
        // very click handler whose own mutation guard is still held, so the render this triggers
        // must not go through that same guard directly -- ShellRenderGate.RequestContentRender
        // records it instead and flushes it the moment the guard releases (see its own remarks).
        workspace.RouteChanged += (_, _) => renderGate.RequestContentRender();
        // Plan 5.1/12.2: a UI-language save applies without restart -- every legacy-backed
        // view-model wraps a MainPageViewModel bound to one fixed SurfaceText snapshot, so a
        // language change rebuilds all of them against the newly current text before re-rendering.
        // Same finding-1 reasoning as RouteChanged above: SaveAsync raises this event from inside
        // its own save button's guard.
        text.Changed += (_, _) =>
        {
            (legacy, projectOverview, projectSettings, sprintWorkspace) = BuildLegacyDependents(folderPicker);
            renderGate.RequestSidebarRender();
            renderGate.RequestContentRender();
        };
    }

    private (MainPageViewModel, ProjectOverviewViewModel, ProjectSettingsViewModel, SprintWorkspaceViewModel)
        BuildLegacyDependents(IFolderPickerPort folderPicker)
    {
        MainPageViewModel freshLegacy = new(text.Current, application, resolveMutations);
        return (
            freshLegacy,
            new(application, freshLegacy),
            new(application, catalog, freshLegacy, resolveMutations, folderPicker, text),
            new(freshLegacy, application, catalog, resolveMutations, text.Current));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(async () =>
        {
            await workspace.RestoreAsync(CancellationToken.None).ConfigureAwait(true);
            await RenderSidebarAsync().ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>The sprint workspace's timeline poll (plan 12.3: "new items appear without manual
    /// refresh while the sprint page is visible") must not keep ticking once the page itself is
    /// gone -- <see cref="StopTimelinePoll"/> is also called on every route change (see
    /// <see cref="RenderContentAsync"/>), so this is the backstop for closing the page entirely.
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopTimelinePoll();
    }

    /// <summary>Serializes shell-driven mutations so a second click cannot re-enter one while the
    /// first is still in flight -- the same discipline the previous monolithic page applied. Delegates
    /// to <see cref="ShellRenderGate"/> (see its own remarks for PR #98 review finding 1: a route or
    /// language change raised while this guard is held must still produce a real re-render once the
    /// guard releases, not be silently dropped).</summary>
    private Task RunAsync(Func<Task> action) => renderGate.RunAsync(action);

    private async Task RenderSidebarAsync()
    {
        SidebarSnapshot snapshot = await sidebar.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        SidebarHost.Children.Clear();
        SidebarHost.Children.Add(BuildAddProjectRow());
        foreach (SidebarProjectItem project in snapshot.Projects)
        {
            SidebarHost.Children.Add(BuildProjectRow(project));
        }

        if (snapshot.Projects.Count == 0)
        {
            SidebarHost.Children.Add(Describe(new Label { Text = text.Resolve(MessageKeys.SidebarNoProjectsHint) }));
        }

        Button forgeSettingsButton = new() { Text = text.Resolve(MessageKeys.SidebarForgeSettingsAction) };
        forgeSettingsButton.Clicked += (_, _) => _ = RunAsync(async () =>
            await workspace.NavigateAsync(WorkspaceRoute.ToForgeSettings(), CancellationToken.None).ConfigureAwait(true));
        SidebarHost.Children.Add(forgeSettingsButton);

        Label status = new() { Text = snapshot.Status.ProviderSummaryText };
        SemanticProperties.SetDescription(status, snapshot.Status.ProviderAccessibleText);
        SidebarHost.Children.Add(status);
        Label quota = new() { Text = snapshot.Status.QuotaStatusText };
        SidebarHost.Children.Add(quota);

        // PR #98 review finding 3: add/remove-project results were discarded, leaving a failure (an
        // invalid root, an already-cataloged entry, a catalog write failure) completely silent.
        // `sidebarNotice` is set by the add/remove handlers just before they trigger this render, so
        // it survives the rebuild instead of being wiped by it.
        if (!string.IsNullOrEmpty(sidebarNotice))
        {
            SidebarHost.Children.Add(Describe(new Label { Text = sidebarNotice }));
        }
    }

    private VerticalStackLayout BuildAddProjectRow()
    {
        Entry pathEntry = new() { Placeholder = text.Resolve(MessageKeys.SidebarAddProjectPathLabel) };
        SemanticProperties.SetDescription(pathEntry, text.Resolve(MessageKeys.SidebarAddProjectPathLabel));
        Button addButton = new() { Text = text.Resolve(MessageKeys.SidebarAddProjectAction) };
        addButton.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            AddProjectResult addResult = await sidebar.AddProjectAsync(pathEntry.Text, CancellationToken.None)
                .ConfigureAwait(true);
            // A dismissed folder picker is not a failure and has nothing to report (see
            // AddProjectResult.Cancelled's own remarks) -- leave any earlier notice alone rather
            // than replacing it with a blank one.
            if (addResult != AddProjectResult.Cancelled)
            {
                sidebarNotice = Message(
                    text.Resolve(addResult.Succeeded ? MessageKeys.ProjectAdded : MessageKeys.ProjectAddFailed),
                    addResult.DiagnosticCode);
            }

            await RenderSidebarAsync().ConfigureAwait(true);
        });
        return new VerticalStackLayout { Children = { pathEntry, addButton } };
    }

    private VerticalStackLayout BuildProjectRow(SidebarProjectItem project)
    {
        VerticalStackLayout column = new();
        Button projectButton = new() { Text = project.DisplayName };
        SemanticProperties.SetDescription(projectButton, project.AccessibleName);
        projectButton.Clicked += (_, _) => _ = RunAsync(async () =>
            await workspace
                .NavigateAsync(WorkspaceRoute.ToProjectOverview(project.ProjectId, project.Root), CancellationToken.None)
                .ConfigureAwait(true));
        column.Children.Add(projectButton);

        foreach (SidebarSprintItem sprint in project.ActiveSprints)
        {
            Button sprintButton = new()
            {
                Text = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture, $"  {sprint.CreationSequence}. {sprint.StateText}"),
            };
            SemanticProperties.SetDescription(sprintButton, sprint.AccessibleName);
            sprintButton.Clicked += (_, _) => _ = RunAsync(async () =>
                await workspace
                    .NavigateAsync(
                        WorkspaceRoute.ToSprintWorkspace(project.ProjectId, project.Root, sprint.SprintId),
                        CancellationToken.None)
                    .ConfigureAwait(true));
            column.Children.Add(sprintButton);
        }

        if (project.HistoryCount > 0)
        {
            column.Children.Add(new Label
            {
                Text = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"  {text.Resolve(MessageKeys.SidebarHistoryLabel)} ({project.HistoryCount})"),
            });
        }

        Button settingsButton = new() { Text = "..." };
        SemanticProperties.SetDescription(settingsButton, text.Resolve(MessageKeys.ProjectSettingsTitle));
        settingsButton.Clicked += (_, _) => _ = RunAsync(async () =>
            await workspace
                .NavigateAsync(WorkspaceRoute.ToProjectSettings(project.ProjectId, project.Root), CancellationToken.None)
                .ConfigureAwait(true));
        column.Children.Add(settingsButton);

        Button removeButton = new() { Text = text.Resolve(MessageKeys.SidebarRemoveProjectAction) };
        removeButton.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string diagnosticCode = await sidebar.RemoveProjectAsync(project.ProjectId, CancellationToken.None)
                .ConfigureAwait(true);
            bool succeeded = diagnosticCode == DiagnosticCodes.None;
            // PR #98 review finding 3: surface the outcome instead of discarding it.
            sidebarNotice = Message(
                text.Resolve(succeeded ? MessageKeys.ProjectRemoved : MessageKeys.ProjectRemoveFailed), diagnosticCode);
            // PR #98 review finding 5: removing the currently open project must not leave its page
            // live and actionable -- mirror WorkspaceShellPage.ProjectSettings.cs's own
            // remove-from-catalog handler, which already resets the route in this situation.
            if (succeeded && workspace.Route.ProjectId == project.ProjectId)
            {
                await workspace.RestoreAsync(CancellationToken.None).ConfigureAwait(true);
                await RenderContentAsync().ConfigureAwait(true);
            }

            await RenderSidebarAsync().ConfigureAwait(true);
        });
        column.Children.Add(removeButton);
        return column;
    }

    private async Task RenderContentAsync()
    {
        WorkspaceRoute route = workspace.Route;
        StopTimelinePoll();
        // PR #99 review finding 11: scrollTrackedSprintId is only ever set to a real sprint id by
        // RenderSprintWorkspaceAsync itself (see WorkspaceShellPage.SprintWorkspace.cs) -- resetting
        // it here for every render means a scroll on any other route is never attributed to the
        // last-open sprint's saved position, since ContentHost's shared ScrollView.Scrolled handler
        // ignores Guid.Empty.
        scrollTrackedSprintId = Guid.Empty;
        ContentHost.Children.Clear();
        ContextualActionHost.Children.Clear();
        StickyHeaderHost.Children.Clear();
        PageStatusHeader.Text = route.Page switch
        {
            WorkspacePage.ForgeSettings => text.Resolve(MessageKeys.ForgeSettingsTitle),
            WorkspacePage.ProjectOverview => text.Resolve(MessageKeys.ProjectOverviewTitle),
            WorkspacePage.ProjectSettings => text.Resolve(MessageKeys.ProjectSettingsTitle),
            WorkspacePage.SprintWorkspace => text.Resolve(MessageKeys.SprintWorkspaceTitle),
            _ => text.Resolve(MessageKeys.WorkspaceEmptyStateTitle),
        };

        switch (route.Page)
        {
            case WorkspacePage.ForgeSettings:
                await RenderForgeSettingsAsync().ConfigureAwait(true);
                break;
            case WorkspacePage.ProjectOverview when route.ProjectRoot is { } overviewRoot:
                await RenderProjectOverviewAsync(route.ProjectId!.Value, overviewRoot).ConfigureAwait(true);
                break;
            case WorkspacePage.ProjectSettings when route.ProjectRoot is { } settingsRoot:
                await RenderProjectSettingsAsync(route.ProjectId!.Value, settingsRoot).ConfigureAwait(true);
                break;
            case WorkspacePage.SprintWorkspace when route.ProjectRoot is { } sprintRoot && route.SprintId is { } sprintId:
                await RenderSprintWorkspaceAsync(sprintRoot, sprintId).ConfigureAwait(true);
                break;
            default:
                ContentHost.Children.Add(new Label { Text = text.Resolve(MessageKeys.SidebarNoProjectsHint) });
                break;
        }
    }

    /// <summary>Same shape as <see cref="MainPageViewModel"/>'s own private helper of the same name:
    /// appends a failure's machine diagnostic code parenthetically so it stays available without
    /// being the whole message (PR #98 review findings 3/4).</summary>
    private static string Message(string message, string diagnosticCode) =>
        diagnosticCode == DiagnosticCodes.None
            ? message
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{message} ({diagnosticCode})");

    private static Entry Describe(Entry entry, string label)
    {
        entry.Placeholder = label;
        SemanticProperties.SetDescription(entry, label);
        return entry;
    }

    private static Label Describe(Label label)
    {
        SemanticProperties.SetDescription(label, label.Text);
        return label;
    }
}
