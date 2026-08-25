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
    /// <summary>Set right before a sidebar re-render whose triggering action (add/remove project, or
    /// a failed collapse/expand write -- PR #103 review finding 1) has a result worth telling the
    /// user about (PR #98 review finding 3); read once by <see cref="RenderSidebarFromSnapshot"/> and
    /// left in place until the next such action so it survives that render instead of a stale value
    /// flashing and vanishing. Rendered in both the expanded and collapsed layouts (PR #103 review
    /// finding 1, iteration 2): a notice set by a failed collapse/expand write must stay visible even
    /// while the rail is collapsed, since a failed *expand* attempt is exactly the case that leaves
    /// the sidebar in that state with no other control to surface it later.</summary>
    private string? sidebarNotice;
    /// <summary>The most recently loaded sidebar snapshot (PR #103 review finding 3): lets the
    /// collapse/expand toggle re-render from data already in hand instead of paying for
    /// <see cref="SidebarViewModel.LoadAsync"/>'s full per-project workspace-summary refetch just to
    /// flip a column width. Refreshed by every <see cref="RenderSidebarAsync"/> call and by the
    /// toggle itself; never read for anything that needs current data.</summary>
    private SidebarSnapshot? lastSidebarSnapshot;
    /// <summary>Plan 12.6 ("focus-stable after refresh"): the neutral half of the sidebar's focus-
    /// preservation mechanism -- see <see cref="FocusKeyTracker"/>'s own remarks. Paired with
    /// <see cref="sidebarFocusRegistry"/>, which maps those same keys to a live control instance (see
    /// <see cref="FocusControlRegistry{TControl}"/>'s own remarks for why that mapping itself stays
    /// generic/testable even though only a live MAUI visual tree ever actually populates it here).
    /// </summary>
    private readonly FocusKeyTracker sidebarFocusTracker = new();
    private readonly FocusControlRegistry<VisualElement> sidebarFocusRegistry = new();
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

    /// <summary>Sidebar column width when expanded -- the plan 4.1 layout's own fixed 280 (matching
    /// <c>WorkspaceShellPage.xaml</c>'s original <c>ColumnDefinitions</c>).</summary>
    private const double SidebarExpandedWidth = 280;

    /// <summary>Minimum comfortable tap-target width for the collapsed rail's toggle button (PR #103
    /// review finding 2). The collapsed column itself is sized to its content
    /// (<see cref="GridLength.Auto"/>, set below) rather than a fixed width that has to happen to
    /// exceed <c>SidebarHost</c>'s own <c>Padding="12"</c> plus the button's default chrome -- a
    /// fixed 56 left roughly 32 units of content width, and WinUI's default <c>Button</c> horizontal
    /// padding alone is about 22, before any user text-scaling grows the glyph further. Because the
    /// collapsed state now persists across restart, a clipped/untappable toggle would be
    /// unrecoverable without hand-editing <c>config.json</c>; sizing to content plus this floor keeps
    /// the tap target comfortable at every text-scale setting (plan 12.6) instead of only the one
    /// scale factor 56 happened to fit.</summary>
    private const double SidebarCollapsedToggleMinimumWidth = 44;

    private async Task RenderSidebarAsync()
    {
        SidebarSnapshot snapshot = await sidebar.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        RenderSidebarFromSnapshot(snapshot);
    }

    /// <summary>Rebuilds the sidebar UI tree from an already-loaded <paramref name="snapshot"/>
    /// without itself fetching anything (PR #103 review finding 3): the collapse/expand toggle calls
    /// this directly from data it already has, so a purely cosmetic width change never pays for
    /// <see cref="SidebarViewModel.LoadAsync"/>'s full per-project workspace-summary refetch and
    /// configuration read while holding <see cref="ShellRenderGate"/>'s mutation guard.</summary>
    private void RenderSidebarFromSnapshot(SidebarSnapshot snapshot)
    {
        lastSidebarSnapshot = snapshot;
        // Plan 12.6: every control this render creates below is re-registered under its own stable key
        // (see TrackSidebarFocus's own remarks) -- clearing first means a project/sprint removed since
        // the last render leaves no stale entry behind for RestoreSidebarFocus to (harmlessly, but
        // pointlessly) try to focus.
        sidebarFocusRegistry.Clear();
        SidebarHost.Children.Clear();
        ShellGrid.ColumnDefinitions[0].Width =
            snapshot.Collapsed ? GridLength.Auto : new GridLength(SidebarExpandedWidth);
        SidebarHost.Children.Add(TrackSidebarFocus("sidebar-toggle", BuildSidebarToggleButton(snapshot.Collapsed)));
        // PR #103 review finding 1 (iteration 2): this used to sit after the collapsed early return
        // below, so a notice set by a failed expand attempt -- the write fails while the rail is
        // still collapsed -- was built and then immediately discarded by that return, leaving the
        // click silently inert with no diagnostic and, since the collapsed rail has no other
        // control, no in-app way back to an expanded sidebar. Rendering it here, before the return,
        // means both directions of a failed write are always visible (see `sidebarNotice`'s own
        // remarks and the click handler below, which also stopped rolling the visible state back to
        // collapsed on a failed write for the same reason).
        if (!string.IsNullOrEmpty(sidebarNotice))
        {
            SidebarHost.Children.Add(Describe(new Label { Text = sidebarNotice }));
        }

        if (snapshot.Collapsed)
        {
            // ADR 0050 addendum: collapsed is an icon-only rail -- only the re-expand affordance
            // and any pending failure notice above stay visible, matching plan 12.6 ("state conveyed
            // by an icon/text change, not merely a width change") since the toggle's own accessible
            // name already flips with it.
            RestoreSidebarFocus();
            return;
        }

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
        SidebarHost.Children.Add(TrackSidebarFocus("forge-settings", forgeSettingsButton));

        Label status = new() { Text = snapshot.Status.ProviderSummaryText };
        SemanticProperties.SetDescription(status, snapshot.Status.ProviderAccessibleText);
        SidebarHost.Children.Add(status);
        Label quota = new() { Text = snapshot.Status.QuotaStatusText };
        SemanticProperties.SetDescription(quota, snapshot.Status.QuotaAccessibleText);
        SidebarHost.Children.Add(quota);
        // PR #98 review finding 3: add/remove-project results were discarded, leaving a failure (an
        // invalid root, an already-cataloged entry, a catalog write failure) completely silent.
        // `sidebarNotice` is set by the add/remove handlers just before they trigger this render, so
        // it survives the rebuild instead of being wiped by it -- now rendered once, above, right
        // after the toggle button, so it is also visible in the collapsed layout (see that block's
        // own remarks, PR #103 review finding 1 iteration 2).
        RestoreSidebarFocus();
    }

    /// <summary>Registers <paramref name="control"/>'s stable <paramref name="key"/> in
    /// <see cref="sidebarFocusRegistry"/> and wires its <c>Focused</c> event into
    /// <see cref="sidebarFocusTracker"/>, so a later <see cref="RestoreSidebarFocus"/> call -- once this
    /// render's replacement subtree is fully built -- can find and refocus whichever new control now
    /// occupies the same logical slot as the one that had focus before the rebuild (plan 12.6:
    /// "focus-stable after refresh"). <paramref name="key"/> is always derived from a domain identifier
    /// (a project id, a sprint id) or a fixed name for a singleton control, never a raw instance
    /// reference, which cannot survive the rebuild.</summary>
    private T TrackSidebarFocus<T>(string key, T control) where T : VisualElement
    {
        sidebarFocusRegistry.Register(key, control);
        control.Focused += (_, _) => sidebarFocusTracker.Capture(key);
        return control;
    }

    /// <summary>Restores focus to whichever freshly built sidebar control has the same stable key the
    /// previously focused control had, if any -- a no-op when nothing in the sidebar was focused, or
    /// when that key no longer exists in this snapshot (e.g. its project was just removed).</summary>
    private void RestoreSidebarFocus()
    {
        if (sidebarFocusTracker.Consume() is { } key && sidebarFocusRegistry.TryResolve(key, out VisualElement? control))
        {
            control.Focus();
        }
    }

    /// <summary>The whole-sidebar collapse/expand toggle (ADR 0050 addendum): always the sidebar's
    /// first control, in both states, so the rail retains its own re-expand affordance. The write
    /// still goes through <see cref="RunAsync"/>/<see cref="ShellRenderGate"/> -- the same guard
    /// every other shell-driven mutation already uses -- but the re-render after it does not: see
    /// the click handler's own remarks (PR #103 review finding 3).</summary>
    private Button BuildSidebarToggleButton(bool collapsed)
    {
        Button toggle = new() { Text = collapsed ? ">>" : "<<", MinimumWidthRequest = SidebarCollapsedToggleMinimumWidth };
        SemanticProperties.SetDescription(
            toggle, text.Resolve(collapsed ? MessageKeys.SidebarExpandAction : MessageKeys.SidebarCollapseAction));
        toggle.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            ConfigurationWriteResult result =
                await sidebar.SetCollapsedAsync(!collapsed, CancellationToken.None).ConfigureAwait(true);
            // PR #103 review finding 1: this used to discard `result` and always re-render as if the
            // toggle succeeded. A failed write (unwritable config.json, schema validation rejection,
            // a scope violation) left the sidebar silently inert -- the click did nothing and no
            // diagnostic appeared anywhere. Same sidebarNotice/Message pattern PR #98 review finding
            // 3 already established in this file for add/remove-project.
            //
            // Iteration-2 review on this same finding: rolling the visible state back to `collapsed`
            // on failure (the original fix here) matched what was actually persisted, but for the
            // expand direction that rollback re-enters the collapsed rail -- the one layout whose
            // only control is this same toggle -- with the failure notice now rendered there too
            // (see RenderSidebarFromSnapshot), but the write keeps failing for the same durable
            // reason (locked/read-only/full config.json) on every retry. Collapse state is a cosmetic
            // view preference, not domain data, so -- matching how ForgeSettingsViewModel.SaveAsync
            // and ProjectSettingsViewModel.SaveAsync already treat a failed settings save elsewhere
            // in this shell (the caller's requested edits stay visible/applied; only a failure notice
            // is added, nothing is rolled back for the user) -- the visible state now always follows
            // the click. Only the notice reports whether the preference actually persisted.
            bool nowCollapsed = !collapsed;
            if (!result.Succeeded)
            {
                sidebarNotice = Message(text.Resolve(MessageKeys.SidebarCollapseSaveFailed), result.DiagnosticCode);
            }

            // PR #103 review finding 3: collapsing/expanding changes no domain data, so render
            // straight from the snapshot already loaded (falling back to a real load only if this
            // is somehow reached before the sidebar has ever loaded once) instead of paying for
            // SidebarViewModel.LoadAsync's full per-project workspace-summary refetch and
            // configuration read just to flip a column width -- the exact "unnecessary work holding
            // the mutation guard" pattern PR #99 review finding 1 and PR #100 review finding 1 both
            // already pushed back on in this same surface.
            SidebarSnapshot snapshot =
                lastSidebarSnapshot ?? await sidebar.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            RenderSidebarFromSnapshot(snapshot with { Collapsed = nowCollapsed });
        });
        return toggle;
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
        return new VerticalStackLayout
        {
            Children =
            {
                TrackSidebarFocus("add-project-path", pathEntry),
                TrackSidebarFocus("add-project-button", addButton),
            },
        };
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
        column.Children.Add(TrackSidebarFocus(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"project:{project.ProjectId:D}"),
            projectButton));

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
            column.Children.Add(TrackSidebarFocus(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sprint:{sprint.SprintId:D}"),
                sprintButton));
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
        column.Children.Add(TrackSidebarFocus(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"project-settings:{project.ProjectId:D}"),
            settingsButton));

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
        column.Children.Add(TrackSidebarFocus(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"project-remove:{project.ProjectId:D}"),
            removeButton));
        return column;
    }

    private async Task RenderContentAsync()
    {
        WorkspaceRoute route = workspace.Route;
        StopTimelinePoll();
        // PR #110 review round 2 finding 2: mirrors ClearContentFocusWhenFocused's own discipline for
        // the content half (WorkspaceShellPage.SprintWorkspace.cs) -- rendering the content pane means
        // the content pane, not the sidebar, is now what the user is looking at, so any key
        // sidebarFocusTracker is still holding from before this render is no longer a meaningful
        // restoration target for a LATER, unrelated sidebar-only rebuild (add/remove project, the
        // collapse toggle, or a UI-language save's RequestSidebarRender). Without this, that later
        // rebuild's RestoreSidebarFocus can still resolve the stale key against the freshly rebuilt
        // sidebar and yank focus away from wherever the user has since moved it in the content pane --
        // concretely: focus the sidebar's Forge Settings button, which navigates here (this method
        // runs and, with this fix, clears the captured "forge-settings" key immediately); the user then
        // edits and saves a UI-language change from the content pane, which raises
        // SurfaceTextProvider.Changed and requests a sidebar re-render -- RestoreSidebarFocus finds
        // nothing captured and leaves focus alone, instead of stealing it back into the sidebar.
        sidebarFocusTracker.Clear();
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
