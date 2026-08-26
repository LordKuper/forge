using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Desktop.Theme;
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
        IFolderPickerPort folderPicker,
        IHostConnectivityMonitor connectivityMonitor)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(resolveMutations);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(connectivityMonitor);
        InitializeComponent();
        // Nocturne visual pass: the page-status header (route title) is the one XAML-declared
        // control this file itself drives (PageStatusHeader.Text, set on every RenderContentAsync
        // call below) -- styling it here, once, is equivalent to a XAML Style attribute without
        // adding one to WorkspaceShellPage.xaml, which this pass leaves alone per its own note.
        PageStatusHeader.Style = ThemeStyle("HeadingLabelStyle");
        PageStatusHeader.FontSize = 15;
        this.text = text;
        this.application = application;
        this.catalog = catalog;
        this.providerCatalog = providerCatalog;
        this.resolveMutations = resolveMutations;
        workspace = new(catalog, application);
        sidebar = new(catalog, application, folderPicker, text, connectivityMonitor: connectivityMonitor);
        forgeSettings = new(application, providerCatalog, text);
        (legacy, projectOverview, projectSettings, sprintWorkspace) = BuildLegacyDependents(folderPicker);
        // Always calls through the *current* sprintWorkspace field (never captured once as a fixed
        // reference) so a language-change rebuild of that field above never leaves this coordinator
        // pointing at a stale instance -- see its own field remarks in
        // WorkspaceShellPage.SprintWorkspace.cs.
        scrollPersistCoordinator = new((projectId, sprintId, position, cancellationToken) =>
            sprintWorkspace.SaveScrollPositionAsync(projectId, sprintId, position, cancellationToken));
        renderGate = new(RenderSidebarAsync, RenderContentAsync);
        // PR #98 review finding 1: NavigateAsync raises RouteChanged synchronously from inside the
        // very click handler whose own mutation guard is still held, so the render this triggers
        // must not go through that same guard directly -- ShellRenderGate.RequestContentRender
        // records it instead and flushes it the moment the guard releases (see its own remarks).
        // PR #106 review finding 5: the sidebar's Host-connectivity indicator now names the CURRENTLY
        // SELECTED project (see RenderSidebarAsync below), so a route change -- which is exactly what
        // changes which project is selected -- must also re-render the sidebar, not only the content
        // pane, or the indicator would keep showing whichever project was selected at the last
        // sidebar render instead of the one the user just navigated to.
        //
        // PR #106 round-2 review finding 2: the first cut of that fix called RequestSidebarRender()
        // here, which routes through RenderSidebarAsync -> SidebarViewModel.LoadAsync's full
        // per-project catalog scan (GetWorkspaceSummaryAsync + GetProjectSnapshotAsync per cataloged
        // project) just to refresh one label -- on EVERY navigation click, while holding the
        // mutation guard. That paid for a redundant full reload on top of the one
        // RenderProjectOverviewAsync/RenderProjectSettingsAsync already run themselves, made app
        // launch load the sidebar twice (RestoreAsync raises this event from inside OnAppearing's own
        // guard, and OnAppearing's own explicit RenderSidebarAsync call runs again), and made removing
        // a project load it three times -- the exact "no refetch for a render that changes no domain
        // data" cost PR #99/#100/#103 already pushed back on elsewhere in this file (see
        // BuildSidebarToggleButton's own remarks). A route change never changes any cataloged
        // project's own data, only which project is selected, so this now re-renders the already
        // -loaded lastSidebarSnapshot with just the Host-connectivity pair recomputed for the newly
        // selected project via ShellRenderGate.RequestRender's cheap, synchronous path -- the same
        // lastSidebarSnapshot/RenderSidebarFromSnapshot mechanism the toggle already uses for the
        // same reason. Nothing loaded yet (this fires from RestoreAsync before OnAppearing's own
        // first render, or before the sidebar has ever rendered) is a deliberate no-op: whichever real
        // render already runs shortly after -- OnAppearing's own explicit call, or
        // RenderProjectOverviewAsync/RenderProjectSettingsAsync's own sidebar.LoadAsync -- reports
        // this same route's connectivity correctly on its own.
        workspace.RouteChanged += (_, _) =>
        {
            renderGate.RequestRender(() =>
            {
                if (lastSidebarSnapshot is { } snapshot)
                {
                    (string hostText, string hostAccessible) = sidebar.HostConnectivityFor(workspace.Route.ProjectId);
                    RenderSidebarFromSnapshot(
                        snapshot with
                        {
                            Status = snapshot.Status with
                            {
                                HostConnectivityText = hostText,
                                HostConnectivityAccessibleText = hostAccessible,
                            },
                        });
                }
            });
            renderGate.RequestContentRender();
        };
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
        // PR #105 review finding 3's flush-on-navigate-away, for the whole page closing (not just an
        // in-app route change, which RenderContentAsync already covers) -- best-effort: OnDisappearing
        // is not async-capable, so a page closing mid-write cannot itself be awaited, but this still
        // issues the flush instead of leaving a pending debounced value unwritten.
        _ = FlushPendingScrollPositionAsync();
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
        // PR #106 review finding 5: the status row's Host-connectivity text names the CURRENTLY
        // SELECTED project (workspace.Route.ProjectId), never a process-global "whichever project was
        // last mutated" reading -- see SidebarViewModel.LoadAsync's own remarks.
        SidebarSnapshot snapshot =
            await sidebar.LoadAsync(CancellationToken.None, workspace.Route.ProjectId).ConfigureAwait(true);
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
            // SurfaceParityTests.SidebarCollapseToggleNeverStrandsTheUserInACollapsedRailAfterAFailedWrite
            // pins this exact call shape in source (PR #103 review finding 1 iteration 2: the notice
            // must render before the collapsed-state early return below) -- styled via the separate
            // statement underneath instead of inline, so that literal substring survives unchanged.
            SidebarHost.Children.Add(Describe(new Label { Text = sidebarNotice }));
            ((Label)SidebarHost.Children[^1]).TextColor = ThemeColor("ColorStatusAmberText");
            ((Label)SidebarHost.Children[^1]).FontSize = 11;
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

        SidebarHost.Children.Add(BuildSidebarBrandRow());
        SidebarHost.Children.Add(BuildAddProjectRow());
        foreach (SidebarProjectItem project in snapshot.Projects)
        {
            SidebarHost.Children.Add(BuildProjectRow(project));
        }

        if (snapshot.Projects.Count == 0)
        {
            SidebarHost.Children.Add(Describe(new Label
            {
                Text = text.Resolve(MessageKeys.SidebarNoProjectsHint),
                Style = ThemeStyle("MutedLabelStyle"),
            }));
        }

        Button forgeSettingsButton = new()
        {
            Text = text.Resolve(MessageKeys.SidebarForgeSettingsAction),
            Style = ThemeStyle("GhostButtonStyle"),
            TextColor = ThemeColor("ColorNeutral300"),
            HorizontalOptions = LayoutOptions.Fill,
            ImageSource = SidebarIcon(IconGlyphs.SlidersHorizontal, ThemeColor("ColorNeutral500")),
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, ThemeSpace("Space2")),
        };
        forgeSettingsButton.Clicked += (_, _) => _ = RunAsync(async () =>
            await workspace.NavigateAsync(WorkspaceRoute.ToForgeSettings(), CancellationToken.None).ConfigureAwait(true));
        SidebarHost.Children.Add(TrackSidebarFocus("forge-settings", forgeSettingsButton));

        // Plan 12.6: the status row distinguishes provider health, authentication, model
        // availability, quota (known and unknown), and Host connectivity (including stale data) --
        // each its own text/accessible-name pair, per this file's established convention. Never
        // color alone: every state is named in text, not merely implied by a color or icon -- the
        // dot color below is a decorative reinforcement of the same word the label text already
        // states, not a substitute for it.
        VerticalStackLayout statusRow = new() { Spacing = ThemeSpace("Space1"), Padding = new Thickness(2, 0) };
        Label status = SidebarStatusLine(snapshot.Status.ProviderSummaryText, ThemeColor("ColorNeutral600"));
        SemanticProperties.SetDescription(status, snapshot.Status.ProviderAccessibleText);
        statusRow.Children.Add(status);
        Label authentication = SidebarStatusLine(
            snapshot.Status.AuthenticationStatusText, AuthenticationDotColor(snapshot.Status.AuthenticationStatusText));
        SemanticProperties.SetDescription(authentication, snapshot.Status.AuthenticationAccessibleText);
        statusRow.Children.Add(authentication);
        // PR #106 review finding 1: AnyModelUnavailable used to be computed and never read by any
        // UI -- the exact "dead field" defect it was supposed to supersede
        // (see SidebarStatusRow's own remarks). Bold is a non-color-only emphasis (plan 12.6: "color
        // is never the only carrier") that draws attention to the label when at least one enabled
        // provider is not yet usable for model work, without hiding the state behind color alone --
        // the text itself already names the shortfall, this only makes it harder to miss.
        Label modelAvailability = SidebarStatusLine(
            snapshot.Status.ModelAvailabilityText,
            snapshot.Status.AnyModelUnavailable ? ThemeColor("ColorStatusAmber") : ThemeColor("ColorStatusGreen"));
        modelAvailability.FontAttributes = snapshot.Status.AnyModelUnavailable ? FontAttributes.Bold : FontAttributes.None;
        SemanticProperties.SetDescription(modelAvailability, snapshot.Status.ModelAvailabilityAccessibleText);
        statusRow.Children.Add(modelAvailability);
        Label quota = SidebarStatusLine(snapshot.Status.QuotaStatusText, ThemeColor("ColorNeutral600"));
        SemanticProperties.SetDescription(quota, snapshot.Status.QuotaAccessibleText);
        statusRow.Children.Add(quota);
        Label hostConnectivity = SidebarStatusLine(
            snapshot.Status.HostConnectivityText, HostConnectivityDotColor(snapshot.Status.HostConnectivityText));
        SemanticProperties.SetDescription(hostConnectivity, snapshot.Status.HostConnectivityAccessibleText);
        statusRow.Children.Add(hostConnectivity);
        SidebarHost.Children.Add(statusRow);
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
        Button toggle = new()
        {
            Text = collapsed ? ">>" : "<<",
            MinimumWidthRequest = SidebarCollapsedToggleMinimumWidth,
            Style = ThemeStyle("GhostButtonStyle"),
            TextColor = ThemeColor("ColorNeutral500"),
            HorizontalOptions = LayoutOptions.Start,
        };
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
            SidebarSnapshot snapshot = lastSidebarSnapshot ??
                await sidebar.LoadAsync(CancellationToken.None, workspace.Route.ProjectId).ConfigureAwait(true);
            RenderSidebarFromSnapshot(snapshot with { Collapsed = nowCollapsed });
        });
        return toggle;
    }

    /// <summary>Nocturne's small "brand mark + version" header (the mockup's <c>&lt;aside&gt;</c>'s
    /// very first row) -- purely decorative: no click handler, no domain data, so unlike every other
    /// control this file builds it needs neither a <see cref="TrackSidebarFocus{T}"/> registration nor
    /// an <see cref="AutomationProperties"/>/<see cref="SemanticProperties"/> override (a Label with no
    /// interaction and no information beyond its own visible text needs no separate accessible name).
    /// The version string reuses the exact same assembly-version reflection
    /// <see cref="App"/>'s own constructor already calls for the update handshake, so this can never
    /// drift from the real build the way a hand-maintained literal could.</summary>
    private static Grid BuildSidebarBrandRow()
    {
        Grid row = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = ThemeSpace("Space2"),
            Padding = new Thickness(2, 0, 2, ThemeSpace("Space2")),
        };
        Label mark = new()
        {
            Text = IconGlyphs.Sparkle,
            Style = ThemeStyle("IconGlyphStyle"),
            TextColor = ThemeColor("ColorAccent"),
            FontSize = 13,
        };
        Label wordmark = new()
        {
            Text = "FORGE",
            Style = ThemeStyle("HeadingLabelStyle"),
            FontSize = 12,
            CharacterSpacing = 2.5,
            TextColor = ThemeColor("ColorNeutral400"),
        };
        Label version = new()
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"v{typeof(App).Assembly.GetName().Version!.ToString(3)}"),
            Style = ThemeStyle("MonoLabelStyle"),
            HorizontalOptions = LayoutOptions.End,
        };
        Grid.SetColumn(mark, 0);
        Grid.SetColumn(wordmark, 1);
        Grid.SetColumn(version, 2);
        row.Children.Add(mark);
        row.Children.Add(wordmark);
        row.Children.Add(version);
        return row;
    }

    private VerticalStackLayout BuildAddProjectRow()
    {
        Entry pathEntry = new() { Placeholder = text.Resolve(MessageKeys.SidebarAddProjectPathLabel) };
        SemanticProperties.SetDescription(pathEntry, text.Resolve(MessageKeys.SidebarAddProjectPathLabel));
        Button addButton = new()
        {
            Text = text.Resolve(MessageKeys.SidebarAddProjectAction),
            Style = ThemeStyle("PrimaryButtonStyle"),
            HorizontalOptions = LayoutOptions.Fill,
            ImageSource = SidebarIcon(IconGlyphs.Plus, ThemeColor("ColorAccent")),
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, ThemeSpace("Space2")),
        };
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
            Spacing = ThemeSpace("Space2"),
            Children =
            {
                TrackSidebarFocus("add-project-path", pathEntry),
                TrackSidebarFocus("add-project-button", addButton),
            },
        };
    }

    /// <summary>Plan 12.1 final-sweep gap 1's per-project chevron: hides/shows the WHOLE per-project
    /// sprint block -- both <see cref="SidebarProjectItem.ActiveSprints"/> and
    /// <see cref="SidebarProjectItem.History"/> (PR #105 review finding 2; the toggle's own
    /// "Collapse sprints" accessible name promised the whole block, not only the active list) --
    /// mirroring the whole-sidebar rail toggle's own "render straight from the snapshot already in
    /// hand" optimization (PR #103 review finding 3) -- flipping one row's disclosure changes no
    /// domain data, so it never re-fetches <see cref="SidebarViewModel.LoadAsync"/>'s full per-project
    /// workspace summary.</summary>
    private Button BuildProjectSprintsToggleButton(SidebarProjectItem project)
    {
        Button toggle = new()
        {
            Text = IconGlyphs.CaretDown,
            FontFamily = ThemeString("FontIcon"),
            FontSize = 11,
            TextColor = ThemeColor("ColorNeutral600"),
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(2),
            MinimumWidthRequest = SidebarCollapsedToggleMinimumWidth,
            // Nocturne's project header always shows a caret-down glyph (ph-caret-down); this app's
            // own per-project collapse state (plan 12.1 final-sweep gap 1, not in the mockup at all)
            // is conveyed by rotating that same glyph instead of swapping to a different icon --
            // Rotation is a pure render transform, not a HeightRequest/size change, so it does not
            // trip WorkspaceShellAccessibilityTests' text-scaling guards.
            Rotation = project.SprintListExpanded ? 0 : -90,
        };
        SemanticProperties.SetDescription(
            toggle,
            text.Resolve(project.SprintListExpanded
                ? MessageKeys.SidebarProjectCollapseSprintsAction
                : MessageKeys.SidebarProjectExpandSprintsAction));
        toggle.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            bool nowExpanded = !project.SprintListExpanded;
            ProjectCatalogResult result = await sidebar
                .SetProjectSprintsExpandedAsync(project.ProjectId, nowExpanded, CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                sidebarNotice = Message(
                    text.Resolve(MessageKeys.SidebarProjectSprintsSaveFailed), result.DiagnosticCode);
            }

            SidebarSnapshot snapshot =
                lastSidebarSnapshot ?? await sidebar.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            SidebarSnapshot updated = snapshot with
            {
                Projects =
                [
                    .. snapshot.Projects.Select(item => item.ProjectId == project.ProjectId
                        ? item with { SprintListExpanded = nowExpanded }
                        : item),
                ],
            };
            RenderSidebarFromSnapshot(updated);
        });
        return toggle;
    }

    private VerticalStackLayout BuildProjectRow(SidebarProjectItem project)
    {
        VerticalStackLayout column = new() { Spacing = 1 };
        // Nocturne header row: caret | name (fills remaining space) | active-sprint count | settings
        // gear -- a Grid rather than the previous HorizontalStackLayout, since only Grid's Star column
        // lets the name take the remaining width and pushes the count/gear flush right, matching the
        // mockup's flex row. settingsButton moves here from the bottom of the column (PR history below
        // never depended on its position -- TrackSidebarFocus keys are resolved by name, not order) to
        // sit next to the gear icon the mockup itself places in this same header.
        Grid header = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = ThemeSpace("Space2"),
        };
        Button caret = BuildProjectSprintsToggleButton(project);
        Grid.SetColumn(caret, 0);
        header.Children.Add(caret);

        Button projectButton = new()
        {
            Text = project.DisplayName,
            Style = ThemeStyle("GhostButtonStyle"),
            TextColor = ThemeColor("ColorNeutral300"),
            FontFamily = ThemeString("FontHeading"),
            FontSize = 12,
            HorizontalOptions = LayoutOptions.Fill,
        };
        SemanticProperties.SetDescription(projectButton, project.AccessibleName);
        projectButton.Clicked += (_, _) => _ = RunAsync(async () =>
            await workspace
                .NavigateAsync(WorkspaceRoute.ToProjectOverview(project.ProjectId, project.Root), CancellationToken.None)
                .ConfigureAwait(true));
        Grid.SetColumn(projectButton, 1);
        header.Children.Add(TrackSidebarFocus(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"project:{project.ProjectId:D}"),
            projectButton));

        Label sprintCount = new()
        {
            Text = project.ActiveSprints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Style = ThemeStyle("MonoLabelStyle"),
        };
        Grid.SetColumn(sprintCount, 2);
        header.Children.Add(sprintCount);

        // Mockup's per-project gear icon -- unlike every localized action in this file, "..." was
        // already a plain decorative placeholder (never routed through SurfaceTextProvider), so
        // swapping it for a Phosphor gear glyph changes only how it looks, not any copy this file is
        // otherwise forbidden from touching. The accessible name (ProjectSettingsTitle) is unchanged.
        Button settingsButton = new()
        {
            Text = string.Empty,
            Style = ThemeStyle("GhostButtonStyle"),
            ImageSource = SidebarIcon(IconGlyphs.Gear, ThemeColor("ColorNeutral600")),
            MinimumWidthRequest = SidebarCollapsedToggleMinimumWidth,
        };
        SemanticProperties.SetDescription(settingsButton, text.Resolve(MessageKeys.ProjectSettingsTitle));
        settingsButton.Clicked += (_, _) => _ = RunAsync(async () =>
            await workspace
                .NavigateAsync(WorkspaceRoute.ToProjectSettings(project.ProjectId, project.Root), CancellationToken.None)
                .ConfigureAwait(true));
        Grid.SetColumn(settingsButton, 3);
        header.Children.Add(TrackSidebarFocus(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"project-settings:{project.ProjectId:D}"),
            settingsButton));
        column.Children.Add(header);

        // PR #105 review finding 2: both loops below live inside this single gate now -- collapsing a
        // project must hide its whole sprint block (active AND history), matching the toggle's own
        // "Collapse sprints" accessible name and the changelog's "tucked away without hiding the
        // others" claim (see SidebarProjectItem's own remarks).
        if (project.SprintListExpanded)
        {
            foreach (SidebarSprintItem sprint in project.ActiveSprints)
            {
                // Nocturne semantic color (SidebarRowAccentColor): the sub-status color the mockup's
                // rows use, applied to this row's own text since a Button has no room for a second,
                // independently colored line the way the mockup's two-line div does. The active
                // operation additionally gets the mockup's own highlighted-row treatment (tinted
                // background + accent border) via Button.BackgroundColor/BorderColor/BorderWidth,
                // which -- unlike a Grid/Border rewrite -- needs no new control at all.
                bool isSelected = workspace.Route.SprintId == sprint.SprintId;
                Color accent = SidebarRowAccentColor(sprint.StateText);
                Button sprintButton = new()
                {
                    Text = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture, $"  {sprint.CreationSequence}. {sprint.StateText}"),
                    Style = ThemeStyle("GhostButtonStyle"),
                    HorizontalOptions = LayoutOptions.Fill,
                    TextColor = sprint.HasActiveOperation || isSelected ? accent : ThemeColor("ColorNeutral300"),
                    BackgroundColor = sprint.HasActiveOperation ? ThemeColor("ColorAccent900") : Colors.Transparent,
                    BorderColor = sprint.HasActiveOperation ? ThemeColor("ColorAccent") : Colors.Transparent,
                    BorderWidth = sprint.HasActiveOperation ? 1 : 0,
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

            if (project.History.Count > 0)
            {
                // Mockup's "Archived sprints" summary row: an Archive glyph ahead of the exact same
                // localized label text the pre-restyle version already rendered, via
                // Label.FormattedString the same way SidebarStatusLine does below for the bottom
                // status row -- this Label is purely informational (no click handler), so it needs
                // neither TrackSidebarFocus nor an extra AutomationProperties override.
                column.Children.Add(new Label
                {
                    FormattedText = new FormattedString
                    {
                        Spans =
                        {
                            new Span
                            {
                                Text = IconGlyphs.Archive + "  ",
                                FontFamily = ThemeString("FontIcon"),
                                TextColor = ThemeColor("ColorNeutral700"),
                                FontSize = 11,
                            },
                            new Span
                            {
                                // PR #105 review finding 1: HistoryTotalCount is the true, uncapped
                                // count of terminal sprints -- History.Count is capped at
                                // MaxSidebarHistory and would silently under-report once a project
                                // passes that bound.
                                Text = string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"  {text.Resolve(MessageKeys.SidebarHistoryLabel)} ({project.HistoryTotalCount})"),
                                TextColor = ThemeColor("ColorNeutral600"),
                            },
                        },
                    },
                    FontSize = 12,
                    Padding = new Thickness(2, ThemeSpace("Space1")),
                });
                // Plan 12.1 final-sweep gap 3: every history entry is now navigable, the same "open"
                // affordance active sprints get above -- reusing the same sprint-workspace route, which
                // already renders a terminal sprint read-only (no lifecycle/stage-transition action is
                // ever offered for one; plan 13 excludes editing raw sprint state).
                foreach (SidebarHistoryItem historyItem in project.History)
                {
                    bool isHistorySelected = workspace.Route.SprintId == historyItem.SprintId;
                    Button historyButton = new()
                    {
                        Text = string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"  {historyItem.CreationSequence}. {historyItem.StateText}"),
                        Style = ThemeStyle("GhostButtonStyle"),
                        HorizontalOptions = LayoutOptions.Fill,
                        // Terminal sprints read as muted/settled in the mockup (dimmer neutral tone
                        // than an active row) -- SidebarRowAccentColor still distinguishes completed
                        // (green-tinted) from cancelled (neutral) the same way it does for active rows.
                        TextColor = isHistorySelected
                            ? SidebarRowAccentColor(historyItem.StateText)
                            : ThemeColor("ColorNeutral600"),
                    };
                    SemanticProperties.SetDescription(historyButton, historyItem.AccessibleName);
                    historyButton.Clicked += (_, _) => _ = RunAsync(async () =>
                        await workspace
                            .NavigateAsync(
                                WorkspaceRoute.ToSprintWorkspace(project.ProjectId, project.Root, historyItem.SprintId),
                                CancellationToken.None)
                            .ConfigureAwait(true));
                    column.Children.Add(historyButton);
                }
            }
        }

        Button removeButton = new()
        {
            Text = text.Resolve(MessageKeys.SidebarRemoveProjectAction),
            Style = ThemeStyle("GhostButtonStyle"),
            TextColor = ThemeColor("ColorNeutral700"),
            FontSize = 11,
            HorizontalOptions = LayoutOptions.Fill,
        };
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
        // PR #105 review finding 3's flush-on-navigate-away: persists whatever is pending for the
        // sprint workspace being left -- BEFORE scrollTrackedSprintId is reset below -- so a route
        // change away from a sprint workspace never leaves a debounced scroll position unwritten.
        // FlushPendingScrollPositionAsync reads workspace.Route.ProjectId's OLD value captured at
        // render time (scrollTrackedProjectId), not this method's own now-current `route`, which
        // already reflects the destination the user is navigating TO.
        await FlushPendingScrollPositionAsync().ConfigureAwait(true);
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
                ContentHost.Children.Add(new Label
                {
                    Text = text.Resolve(MessageKeys.SidebarNoProjectsHint),
                    Style = ThemeStyle("MutedLabelStyle"),
                });
                break;
        }
    }

    // Nocturne visual pass -- small resource-lookup helpers so every color/style/spacing value below
    // comes from the App.xaml token sheet (this file's assigned palette) instead of a hardcoded hex
    // or magic number scattered through the sidebar-building methods.
    // Fully qualified: within namespace Forge.Desktop, an unqualified "Application" resolves to the
    // sibling namespace Forge.Application (both nest directly under "Forge") ahead of
    // Microsoft.Maui.Controls.Application from the MAUI SDK's global usings -- C#'s "a sibling
    // namespace segment outranks an imported type" rule -- so the bare name would otherwise bind to
    // the wrong "Application" entirely.
    private static Color ThemeColor(string key) => (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private static Style ThemeStyle(string key) => (Style)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private static double ThemeSpace(string key) => (double)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private static string ThemeString(string key) => (string)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    /// <summary>A Phosphor glyph rendered as a <see cref="Button"/>'s <see cref="Button.ImageSource"/>
    /// (paired with <see cref="Button.ContentLayout"/> to sit left of the button's own Text) -- the
    /// only way to put a second, independently colored graphic inside a real, keyboard-focusable
    /// Button in MAUI, since Button (unlike Label) has no FormattedString/child-content model. Never
    /// used as a substitute for Text: every button below still carries its existing localized/plain
    /// Text exactly as before, so removing or failing to load this image would degrade to a
    /// text-only button, never a mystery blank one.</summary>
    private static FontImageSource SidebarIcon(string glyph, Color color) => new()
    {
        FontFamily = ThemeString("FontIcon"),
        Glyph = glyph,
        Color = color,
        Size = 13,
    };

    /// <summary>Maps a sprint/history row's machine-invariant state text
    /// (<c>SurfaceFormatting.Machine(SprintState)</c>, e.g. "running"/"blocked"/"awaiting_human" --
    /// see <see cref="SidebarViewModel"/>'s own <c>ToSprintItem</c>/<c>ToHistoryItem</c> helpers)
    /// to one of the Nocturne status colors, per this pass's spec: blocked/failed to red,
    /// awaiting-human/paused/ready-to-finalize to amber, completed to green, running to the accent
    /// ramp. The switch keys are the <see cref="Forge.Domain.SprintState"/> enum's OWN snake_case
    /// names, never the localized <see cref="SidebarSprintItem.StateText"/> a user actually reads in
    /// a non-English UI language, so this mapping stays correct under every
    /// <c>language.ui</c> setting. A state with no strong semantic color (draft/ready/cancelled)
    /// falls back to a muted neutral -- this is a decorative color only; the row's own Text already
    /// names the state in words (plan 12.6: color is never the only carrier).</summary>
    private static Color SidebarRowAccentColor(string machineStateText) => machineStateText switch
    {
        "running" => ThemeColor("ColorAccent"),
        "blocked" or "failed" => ThemeColor("ColorStatusRedText"),
        "awaiting_human" or "paused" or "ready_to_finalize" => ThemeColor("ColorStatusAmberText"),
        "completed" => ThemeColor("ColorStatusGreenText"),
        _ => ThemeColor("ColorNeutral500"),
    };

    /// <summary>Builds one bottom-status-row line as a single <see cref="Label"/> with a small
    /// colored-dot prefix ahead of the EXACT SAME status text the pre-restyle version already
    /// rendered (callers still call <see cref="SemanticProperties.SetDescription"/> on the returned
    /// instance exactly as before -- this only changes how the line looks). The dot is a plain
    /// Unicode bullet, not a Phosphor glyph: a <see cref="Label"/>'s <see cref="FormattedString"/>
    /// already gives each <see cref="Span"/> its own color for free, and MAUI's text layout silently
    /// falls back to a system font for any single glyph the label's own FontFamily lacks -- a much
    /// safer bet for one lone character than routing it through a custom icon font. Same "color is
    /// never the only carrier" reasoning as <see cref="SidebarRowAccentColor"/>: the word itself is
    /// still the text, the dot is only reinforcement.</summary>
    private static Label SidebarStatusLine(string value, Color dotColor) => new()
    {
        FormattedText = new FormattedString
        {
            Spans =
            {
                new Span { Text = "● ", TextColor = dotColor, FontSize = 9 },
                new Span { Text = value, TextColor = ThemeColor("ColorNeutral500") },
            },
        },
        FontFamily = "Consolas",
        FontSize = 10.5,
    };

    /// <summary>Compares <paramref name="authenticationStatusText"/> against the SAME resolved
    /// <see cref="MessageKeys"/> literal <see cref="SidebarViewModel"/> itself would have resolved it
    /// from (both sides go through this instance's own <c>text</c> field, so they always agree
    /// regardless of the current UI language) rather than pattern-matching the localized string's
    /// shape -- the safe way to recover a severity color for a value this file only ever receives
    /// pre-localized.</summary>
    private Color AuthenticationDotColor(string authenticationStatusText)
    {
        if (authenticationStatusText == text.Resolve(MessageKeys.AuthenticationStatusReady))
        {
            return ThemeColor("ColorStatusGreen");
        }

        if (authenticationStatusText == text.Resolve(MessageKeys.AuthenticationStatusRequired) ||
            authenticationStatusText == text.Resolve(MessageKeys.AuthenticationStatusCheckFailed))
        {
            return ThemeColor("ColorStatusRed");
        }

        return ThemeColor("ColorNeutral600");
    }

    /// <summary>Same reasoning as <see cref="AuthenticationDotColor"/>, for the Host-connectivity
    /// line's four states (connected/stale/disconnected/not-yet-checked).</summary>
    private Color HostConnectivityDotColor(string hostConnectivityText)
    {
        if (hostConnectivityText == text.Resolve(MessageKeys.HostConnectivityConnected))
        {
            return ThemeColor("ColorStatusGreen");
        }

        if (hostConnectivityText == text.Resolve(MessageKeys.HostConnectivityStale))
        {
            return ThemeColor("ColorStatusAmber");
        }

        if (hostConnectivityText == text.Resolve(MessageKeys.HostConnectivityDisconnected))
        {
            return ThemeColor("ColorStatusRed");
        }

        return ThemeColor("ColorNeutral600");
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
