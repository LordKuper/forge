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
    private bool busy;
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
        workspace.RouteChanged += (_, _) => RenderContent();
        // Plan 5.1/12.2: a UI-language save applies without restart -- every legacy-backed
        // view-model wraps a MainPageViewModel bound to one fixed SurfaceText snapshot, so a
        // language change rebuilds all of them against the newly current text before re-rendering.
        text.Changed += (_, _) =>
        {
            (legacy, projectOverview, projectSettings, sprintWorkspace) = BuildLegacyDependents(folderPicker);
            RenderSidebar();
            RenderContent();
        };
    }

    private (MainPageViewModel, ProjectOverviewViewModel, ProjectSettingsViewModel, SprintWorkspaceViewModel)
        BuildLegacyDependents(IFolderPickerPort folderPicker)
    {
        MainPageViewModel freshLegacy = new(text.Current, application, resolveMutations);
        return (
            freshLegacy,
            new(application, freshLegacy),
            new(application, catalog, freshLegacy, resolveMutations, folderPicker),
            new(freshLegacy));
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

    /// <summary>Serializes shell-driven mutations so a second click cannot re-enter one while the
    /// first is still in flight -- the same discipline the previous monolithic page applied.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            busy = false;
        }
    }

    private void RenderSidebar() => _ = RunAsync(RenderSidebarAsync);

    private void RenderContent() => _ = RunAsync(RenderContentAsync);

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
    }

    private VerticalStackLayout BuildAddProjectRow()
    {
        Entry pathEntry = new() { Placeholder = text.Resolve(MessageKeys.SidebarAddProjectPathLabel) };
        SemanticProperties.SetDescription(pathEntry, text.Resolve(MessageKeys.SidebarAddProjectPathLabel));
        Button addButton = new() { Text = text.Resolve(MessageKeys.SidebarAddProjectAction) };
        addButton.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            await sidebar.AddProjectAsync(pathEntry.Text, CancellationToken.None).ConfigureAwait(true);
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
            await sidebar.RemoveProjectAsync(project.ProjectId, CancellationToken.None).ConfigureAwait(true);
            await RenderSidebarAsync().ConfigureAwait(true);
        });
        column.Children.Add(removeButton);
        return column;
    }

    private async Task RenderContentAsync()
    {
        WorkspaceRoute route = workspace.Route;
        ContentHost.Children.Clear();
        ContextualActionHost.Children.Clear();
        PageStatusHeader.Text = route.Page switch
        {
            WorkspacePage.ForgeSettings => text.Resolve(MessageKeys.ForgeSettingsTitle),
            WorkspacePage.ProjectOverview => text.Resolve(MessageKeys.ProjectOverviewTitle),
            WorkspacePage.ProjectSettings => text.Resolve(MessageKeys.ProjectSettingsTitle),
            WorkspacePage.SprintWorkspace => text.Resolve(MessageKeys.SprintWorkspacePlaceholderTitle),
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
