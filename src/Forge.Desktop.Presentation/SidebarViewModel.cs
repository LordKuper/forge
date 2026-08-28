using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop.Presentation;

/// <summary>One sidebar row for a non-terminal sprint (plan section 4.1). <see cref="DisplayTitle"/>
/// is <see cref="SprintDisplayTitle.ResolveRowTitle"/>'s output, already resolved here so no surface
/// re-derives it: a titled sprint reads <c>"(Sprint 2) Fix login"</c> -- the localized ordinal
/// LEADS the frozen title, disambiguating two sprints that share one (titles are not unique under
/// ADR 0057) and surviving the rail's tail truncation -- while an untitled sprint reads the bare
/// <c>"Sprint 2"</c> fallback. It is therefore NOT equal to <c>SprintDefinition.Title</c>; compare
/// or match on <see cref="CreationSequence"/> or <see cref="SprintId"/>, never on this string.
/// <see cref="AccessibleName"/> is a full sentence naming that same resolved title, the state, the
/// plan progress, and (when set) the attention reason -- plan 12.6: "every status has text and an
/// accessible... name," color is never the only carrier.</summary>
public sealed record SidebarSprintItem(
    Guid SprintId,
    int CreationSequence,
    string DisplayTitle,
    string StateText,
    bool RequiresHumanAttention,
    string? CurrentStageId,
    int StagesCompleted,
    int StagesTotal,
    bool HasActiveOperation,
    string AccessibleName);

/// <summary>One navigable row in a project's capped sprint history (plan 12.1 final-sweep gap 3):
/// a terminal (completed/cancelled) sprint, distinct from <see cref="SidebarSprintItem"/> only in
/// that it never carries attention/progress/active-operation fields no terminal sprint can have.
/// <see cref="DisplayTitle"/> is the same <see cref="SprintDisplayTitle.ResolveRowTitle"/> form its
/// active-sprint counterpart carries, ordinal-first on the titled path.</summary>
public sealed record SidebarHistoryItem(
    Guid SprintId,
    int CreationSequence,
    string DisplayTitle,
    string StateText,
    string AccessibleName);

/// <summary>One sidebar project row. <see cref="ActiveSprints"/> and <see cref="History"/> are both
/// shown only while <see cref="SprintListExpanded"/> (plan 12.1 final-sweep gap 1; default
/// <see langword="true"/> so an upgrading user sees exactly what every prior release always
/// rendered) -- PR #105 review finding 2: the chevron governs the whole per-project sprint block, not
/// only the active list, matching its own "Collapse sprints" accessible name and the changelog's
/// "tucked away without hiding the others" claim (the "others" being other projects' rows, not the
/// same project's own history). <see cref="ActiveSprints"/> is already ordered by
/// <see cref="SprintOrderingRank"/>. <see cref="History"/> is every terminal sprint, newest first,
/// capped at <see cref="SidebarViewModel.MaxSidebarHistory"/> rows so it never crowds
/// <see cref="ActiveSprints"/> (plan 12.1 final-sweep gap 3) -- but <see cref="HistoryTotalCount"/> is
/// the true, uncapped total of terminal sprints (PR #105 review finding 1: the label must report how
/// many terminal sprints the project actually has, not how many rows are reachable).</summary>
public sealed record SidebarProjectItem(
    Guid ProjectId,
    string Root,
    string DisplayName,
    bool Available,
    bool Initialized,
    IReadOnlyList<SidebarSprintItem> ActiveSprints,
    bool SprintListExpanded,
    IReadOnlyList<SidebarHistoryItem> History,
    int HistoryTotalCount,
    string AccessibleName);

/// <summary>Plan section 4.1's bottom status row, distinguishing every state plan 12.6 requires:
/// provider (toolchain) health, authentication, model availability, quota (including unknown
/// quota), and Host connectivity (including stale connectivity data). Every state is a
/// <c>XxxText</c>/<c>XxxAccessibleText</c> pair, this file's established screen-reader convention --
/// color is never the only carrier (plan 12.6).
/// <see cref="ProviderSummaryText"/>/<see cref="ProviderAccessibleText"/> report the enabled
/// providers whose toolchain install is ready, independent of authentication.
/// <see cref="AuthenticationStatusText"/>/<see cref="AuthenticationAccessibleText"/> report the
/// worst-case authentication readiness across the SELECTED project's own enabled providers (PR #106
/// round-2 review finding 1: <see cref="ProviderHealthEntry.Enabled"/>/<see cref="ProviderHealthEntry.Authentication"/>
/// are per-project facts -- see <see cref="SidebarViewModel.LoadAsync"/>'s own remarks -- so this must
/// never be computed from a last-project-wins merge across every cataloged project, the same class of
/// bug <see cref="HostConnectivityText"/> was fixed for in round 1). No project selected falls back to
/// the merged set across every cataloged project, the same "nothing routed yet" shape as the
/// pre-existing <see cref="ProviderSummaryText"/>/quota rows.
/// <see cref="ModelAvailabilityText"/>/<see cref="ModelAvailabilityAccessibleText"/> and
/// <see cref="AnyModelUnavailable"/> report how many of the SAME selected-project-scoped enabled
/// providers are actually usable for model work right now -- toolchain-ready AND authenticated --
/// superseding the old <c>AnyKnownProviderUnavailable</c> field (computed but never read by any UI):
/// that field only considered toolchain state, never authentication, so it could not distinguish
/// "installed but not authenticated" from "fully usable." <see cref="QuotaStatusText"/>/<see cref="QuotaAccessibleText"/>
/// report the worst-case state across every known provider's <see cref="ProviderQuotaSnapshot"/>
/// (<see cref="Forge.Localization.SurfaceFormatting.QuotaStatusSummary"/>). ADR 0052 found no
/// provider integration in this codebase exposes a verified quota signal, so today this always
/// resolves to the "unknown" text -- a truthful report, never a fabricated number (plan: "render...
/// unknown, never inferred"). <see cref="HostConnectivityText"/>/<see cref="HostConnectivityAccessibleText"/>
/// report the selected project's most recently observed
/// <see cref="Forge.Application.IHostConnectivityMonitor.LastObserved(System.Guid)"/> reading (never
/// a fresh probe -- see that type's own remarks), including a distinct "stale" state
/// when that reading is older than <see cref="SidebarViewModel.HostConnectivityStaleAfter"/>.</summary>
public sealed record SidebarStatusRow(
    string ProviderSummaryText,
    string ProviderAccessibleText,
    string AuthenticationStatusText,
    string AuthenticationAccessibleText,
    string ModelAvailabilityText,
    string ModelAvailabilityAccessibleText,
    bool AnyModelUnavailable,
    string QuotaStatusText,
    string QuotaAccessibleText,
    string HostConnectivityText,
    string HostConnectivityAccessibleText);

/// <summary>ADR 0050 addendum: <see cref="Collapsed"/> is the workspace shell's whole-sidebar
/// collapse state -- a Desktop-instance-level UI preference (<see cref="ConfigurationKeys.SidebarCollapsed"/>,
/// User scope), never tied to any one project, distinct from each <see cref="SidebarProjectItem"/>'s
/// own <see cref="SidebarProjectItem.SprintListExpanded"/> (plan 12.1 final-sweep gap 1).</summary>
public sealed record SidebarSnapshot(
    IReadOnlyList<SidebarProjectItem> Projects,
    SidebarStatusRow Status,
    bool Collapsed);

public sealed record AddProjectResult(bool Succeeded, Guid? ProjectId, string? Root, string DiagnosticCode)
{
    /// <summary>The user dismissed the folder picker without choosing a folder -- not a failure, and
    /// distinct from every actual <see cref="DiagnosticCode"/> outcome (nothing to report).</summary>
    public static AddProjectResult Cancelled { get; } = new(false, null, null, DiagnosticCodes.None);
}

/// <summary>
/// Plan section 4.1's sidebar: the user-scoped project catalog, each project's bounded workspace
/// summary, deterministic sprint ordering, and the add-project flow through a neutral folder-picker
/// port. Pure presentation state -- it reads <see cref="ProjectCatalogStore"/> and
/// <see cref="ForgeApplication.GetWorkspaceSummaryAsync"/>, and computes nothing a Host does not
/// already report.
/// </summary>
public sealed class SidebarViewModel(
    ProjectCatalogStore catalog,
    ForgeApplication application,
    IFolderPickerPort folderPicker,
    SurfaceTextProvider text,
    IHostConnectivityMonitor connectivityMonitor,
    IClock? clock = null)
{
    /// <summary>A <see cref="Forge.Application.IHostConnectivityMonitor.LastObserved(System.Guid)"/>
    /// reading older than this is reported as the status row's distinct "stale" Host-connectivity state (plan
    /// 12.6) rather than trusted as current -- the sidebar itself has no fixed refresh cadence (it
    /// reloads on demand: route change, add/remove project, collapse toggle -- see
    /// <c>WorkspaceShellPage</c>'s own remarks), so a reading from well before "now" may no longer
    /// reflect whether the Host is actually reachable.</summary>
    public static readonly TimeSpan HostConnectivityStaleAfter = TimeSpan.FromMinutes(5);

    private readonly ProjectCatalogStore catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly IFolderPickerPort folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
    private readonly SurfaceTextProvider text = text ?? throw new ArgumentNullException(nameof(text));
    // PR #106 review finding 4: this used to default to a silently-constructed private
    // HostConnectivityMonitor when omitted or mis-wired -- unlike catalog/application/folderPicker/
    // text above, all four of which throw on a missing dependency. That silent fallback turned a
    // wiring mistake into a monitor no RemoteForgeMutations instance would ever Report into, so
    // LastObserved stayed null forever and the Host-connectivity indicator rendered "not yet
    // checked" permanently -- indistinguishable from a genuinely healthy, merely-unchecked state,
    // with no exception, log, or test failure to reveal the mistake. Required now, like every other
    // sibling dependency, so a future composition-root refactor that drops this argument is a
    // compile error instead of a silently degraded feature.
    private readonly IHostConnectivityMonitor connectivityMonitor =
        connectivityMonitor ?? throw new ArgumentNullException(nameof(connectivityMonitor));
    private readonly IClock clock = clock ?? new SystemClock();

    /// <summary>Plan 12.1 final-sweep gap 3's history cap -- matches
    /// <see cref="ProjectOverviewViewModel.MaxRecentHistory"/>'s own bound so the sidebar and the
    /// project overview page never disagree about how many recent terminal sprints "reachable
    /// without crowding" means.</summary>
    public const int MaxSidebarHistory = 10;

    /// <summary>Loads the sidebar snapshot. <paramref name="selectedProjectId"/> is the workspace
    /// shell's currently routed project (<c>WorkspaceRoute.ProjectId</c>), if any -- PR #106 review
    /// finding 5: <see cref="connectivityMonitor"/> tracks a reading per project (Forge Hosts are
    /// per-project, one pipe per <c>InstanceIdentity.ComputePipeName(instanceId, projectId)</c>), so
    /// the status row's Host-connectivity text must name THIS project's own last-observed reading,
    /// never an arbitrary other cataloged project's. <see langword="null"/> (nothing routed yet, or
    /// the Forge-settings page, which names no single project) reports the same honest "not yet
    /// checked" state as a project with no reading at all. PR #106 round-2 review finding 1: the same
    /// reasoning applies to <c>summary.Providers</c> -- each cataloged project's own
    /// <see cref="ForgeApplication.GetWorkspaceSummaryAsync"/> call reports THAT project's own
    /// <see cref="ProviderHealthEntry.Enabled"/>/<see cref="ProviderHealthEntry.Authentication"/>
    /// facts, so the authentication and model-availability indicators must read the selected
    /// project's own entry (<see cref="BuildStatusRow"/>), never a last-project-wins merge across
    /// every cataloged project.</summary>
    public async Task<SidebarSnapshot> LoadAsync(CancellationToken cancellationToken, Guid? selectedProjectId = null)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        List<SidebarProjectItem> projects = new(listing.Entries.Count);
        Dictionary<string, ProviderHealthEntry> knownProviders = new(StringComparer.Ordinal);
        IReadOnlyList<ProviderHealthEntry>? selectedProjectProviders = null;
        foreach (ProjectCatalogEntry entry in listing.Entries)
        {
            ProjectWorkspaceSummary summary = await application
                .GetWorkspaceSummaryAsync(entry.Root, cancellationToken)
                .ConfigureAwait(false);
            foreach (ProviderHealthEntry provider in summary.Providers)
            {
                knownProviders[provider.Id] = provider;
            }

            if (selectedProjectId is { } selected && entry.ProjectId == selected)
            {
                selectedProjectProviders = summary.Providers;
            }

            ProjectSnapshot snapshot = await application
                .GetProjectSnapshotAsync(entry.Root, cancellationToken)
                .ConfigureAwait(false);
            string displayName = ProjectDisplayName.Resolve(entry.Root, entry.Alias);
            List<SidebarSprintItem> sprints =
            [
                .. summary.ActiveSprints
                    .OrderBySidebarRule(sprint => sprint.State, sprint => sprint.CreationSequence)
                    .Select(sprint => ToSprintItem(displayName, sprint)),
            ];
            List<SprintStatus> terminalSprints =
            [
                .. snapshot.Sprints
                    .Where(sprint => WorkflowStateMachines.IsTerminal(sprint.State))
                    .OrderByDescending(sprint => sprint.CreationSequence),
            ];
            List<SidebarHistoryItem> history =
            [
                .. terminalSprints.Take(MaxSidebarHistory).Select(sprint => ToHistoryItem(displayName, sprint)),
            ];
            projects.Add(new(
                entry.ProjectId,
                entry.Root,
                displayName,
                summary.Available,
                summary.Initialized,
                sprints,
                !entry.SprintListCollapsed,
                history,
                terminalSprints.Count,
                AccessibleProjectName(displayName, summary)));
        }

        // Projects the ProviderHealthEntry set the loop above already collected (each from that
        // project's own GetWorkspaceSummaryAsync toolchain check) rather than calling
        // GetProviderQuotaStatusAsync, which would issue a second, uncached, redundant
        // ProviderToolchainManager.CheckAsync probe on every render for a value ADR 0052 guarantees
        // is constant regardless (PR #100 review finding 1).
        IReadOnlyList<ProviderQuotaSnapshot> quota = application.ProjectProviderQuota(knownProviders.Values);
        ConfigurationView userConfiguration =
            await application.GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return new(
            projects,
            BuildStatusRow(knownProviders.Values, selectedProjectProviders, quota, selectedProjectId),
            IsCollapsed(userConfiguration));
    }

    /// <summary>Persists the workspace shell's whole-sidebar collapse state (ADR 0050 addendum) so
    /// it survives an app restart -- the same local, direct <see cref="ForgeApplication"/> write
    /// every other user-scope key here already uses (never a project Host round-trip).</summary>
    public Task<ConfigurationWriteResult> SetCollapsedAsync(bool collapsed, CancellationToken cancellationToken) =>
        application.SetConfigurationAsync(
            ConfigurationScope.User, null, ConfigurationKeys.SidebarCollapsed, collapsed ? "true" : "false", cancellationToken);

    private static bool IsCollapsed(ConfigurationView view)
    {
        EffectiveConfigurationValue? value =
            view.Values.FirstOrDefault(item => item.Key == ConfigurationKeys.SidebarCollapsed);
        return value?.Value.ValueKind == JsonValueKind.True;
    }

    /// <summary>Plan 12.1 final-sweep gap 1: persists one project's active-sprint-list disclosure
    /// state through the local catalog (never a project Host round-trip -- see
    /// <see cref="ProjectCatalogEntry.SprintListCollapsed"/>'s own remarks for why this is catalog
    /// state rather than a configuration key like <see cref="SetCollapsedAsync"/>'s whole-sidebar
    /// rail).</summary>
    public Task<ProjectCatalogResult> SetProjectSprintsExpandedAsync(
        Guid projectId, bool expanded, CancellationToken cancellationToken) =>
        catalog.SetSprintListCollapsedAsync(projectId, !expanded, cancellationToken);

    public async Task<AddProjectResult> AddProjectAsync(string? manualPath, CancellationToken cancellationToken)
    {
        string? root = manualPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = await folderPicker.PickFolderAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                return AddProjectResult.Cancelled;
            }
        }

        ProjectCatalogResult result = await catalog.AddAsync(root, cancellationToken).ConfigureAwait(false);
        return new(result.Succeeded, result.Entry?.ProjectId, result.Entry?.Root, result.DiagnosticCode);
    }

    public async Task<string> RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        ProjectCatalogResult result = await catalog.RemoveAsync(projectId, cancellationToken).ConfigureAwait(false);
        return result.DiagnosticCode;
    }

    /// <summary>Finding B1: the row names the sprint, not merely its ordinal. The accessible name
    /// leads with <see cref="SprintDisplayTitle.ResolveRowTitle"/>'s resolved title instead of the
    /// former <c>"{SprintIdLabel} {CreationSequence}"</c> prefix, matching the same ADR 0057
    /// reasoning the Project Overview sprint card already applies ("prefixing rendered '2. Sprint 2'
    /// for every untitled sprint"). <see cref="MessageKeys.SprintIdLabel"/> is dropped with it: it
    /// resolves to the CLI's own "Sprint id (empty: active sprint):" prompt, which never read as a
    /// label in a spoken row name. The ordinal itself is kept as a parenthesized disambiguator by
    /// <see cref="SprintDisplayTitle.ResolveRowTitle"/> -- titles are not unique, so the title
    /// alone would leave two same-titled sprints indistinguishable (PR #122 review finding 1).
    /// That one resolved string is both drawn and spoken (round 2 finding 2): round 1 disambiguated
    /// the spoken name only, leaving <see cref="SidebarSprintItem.DisplayTitle"/> -- what the row's
    /// button actually renders -- still colliding, so two same-titled sprints drew identical rows.
    /// Round 3 finding 1 anchored that ordinal at the FRONT of the one string, for the rendering
    /// reason <see cref="SprintDisplayTitle.ResolveRowTitle"/> records. The spoken name inherits the
    /// order rather than keeping a title-first phrasing of its own: this sentence is already a
    /// comma-separated list led by the project name, so "(Sprint 2) Fix login" reads as one more
    /// item in it, and a second ordering would be a second string that can drift from the drawn one
    /// -- exactly the divergence round 2 collapsed.
    /// The plan progress the sidebar row now renders visually is spoken here too,
    /// through the existing <see cref="MessageKeys.SprintStatusHeaderProgressLabel"/> copy the sprint
    /// workspace header already uses for the same fraction -- so the second line the row draws can
    /// stay decorative (see <c>WorkspaceShellPage.BuildSprintRow</c>) rather than becoming a second,
    /// redundant screen-reader stop.</summary>
    private SidebarSprintItem ToSprintItem(string projectDisplayName, SprintWorkspaceSummary sprint)
    {
        bool humanAttention = SprintOrderingRank.RequiresHumanAttention(sprint.State);
        string stateText = SurfaceFormatting.Machine(sprint.State);
        string displayTitle = SprintDisplayTitle.ResolveRowTitle(sprint.Title, sprint.CreationSequence, text.Current);
        string attentionSuffix = humanAttention
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $", {text.Resolve(AttentionReasonKey(sprint.State))}")
            : string.Empty;
        string accessible = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{projectDisplayName}, {displayTitle}, {stateText}, " +
                $"{text.Resolve(MessageKeys.SprintStatusHeaderProgressLabel)} " +
                $"{sprint.StagesCompleted}/{sprint.StagesTotal}{attentionSuffix}");
        return new(
            sprint.SprintId,
            sprint.CreationSequence,
            displayTitle,
            stateText,
            humanAttention,
            sprint.CurrentStageId,
            sprint.StagesCompleted,
            sprint.StagesTotal,
            sprint.HasActiveOperation,
            accessible);
    }

    /// <summary>Plan 12.1 final-sweep gap 3: a terminal sprint's sidebar-history row. Never carries
    /// an attention suffix -- <see cref="SprintOrderingRank.RequiresHumanAttention"/> is only ever
    /// true for a non-terminal state, so a terminal sprint can never need it -- and never a progress
    /// fraction either: <see cref="SprintStatus"/> carries no stage counts at all, which is exactly
    /// why <see cref="SidebarHistoryItem"/> has no progress fields to render. Carries the same
    /// <see cref="SprintDisplayTitle.ResolveRowTitle"/> ordinal disambiguator as the active row
    /// (PR #122 review finding 1), and needs it more: with no state fraction or attention suffix to
    /// vary, the title is nearly all this row has, so two same-titled terminal sprints would
    /// otherwise be wholly indistinguishable in the archived list -- in the drawn row as much as in
    /// the spoken name, which is why one resolved string now serves both (round 2 finding 2).
    /// </summary>
    private SidebarHistoryItem ToHistoryItem(string projectDisplayName, SprintStatus sprint)
    {
        string stateText = SurfaceFormatting.Machine(sprint.State);
        string displayTitle = SprintDisplayTitle.ResolveRowTitle(sprint.Title, sprint.CreationSequence, text.Current);
        string accessible = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{projectDisplayName}, {displayTitle}, {stateText}");
        return new(sprint.Id, sprint.CreationSequence, displayTitle, stateText, accessible);
    }

    internal static string AttentionReasonKey(SprintState state) => state switch
    {
        SprintState.AwaitingHuman => MessageKeys.NotificationAwaitingHumanTitle,
        SprintState.Blocked => MessageKeys.NotificationBlockedTitle,
        SprintState.Failed => MessageKeys.NotificationFailedTitle,
        SprintState.ReadyToFinalize => MessageKeys.SprintReadyToFinalizeReason,
        _ => MessageKeys.NotificationAwaitingHumanTitle,
    };

    /// <summary>PR #98 review finding 8: every word here now routes through
    /// <see cref="SurfaceTextProvider"/>/<see cref="MessageKeys"/> like the rest of this class --
    /// "available"/"unavailable", "active sprints", and "need attention" were hardcoded English,
    /// breaking localization for this accessible name under <c>language.ui = ru</c>.</summary>
    private string AccessibleProjectName(string displayName, ProjectWorkspaceSummary summary) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{displayName}, " +
                $"{text.Resolve(summary.Available ? MessageKeys.SidebarProjectAvailable : MessageKeys.SidebarProjectUnavailable)}, " +
                $"{summary.ActiveSprints.Count} {text.Resolve(MessageKeys.SidebarActiveSprintsLabel)}, " +
                $"{summary.AttentionSprintIds.Count} {text.Resolve(MessageKeys.SidebarAttentionNeededLabel)}");

    /// <summary>Same finding-8 reasoning as <see cref="AccessibleProjectName"/>: both the visible
    /// status-row text and its accessible name are now resolved templates instead of hardcoded
    /// English literals.</summary>
    private SidebarStatusRow BuildStatusRow(
        IEnumerable<ProviderHealthEntry> providers,
        IReadOnlyList<ProviderHealthEntry>? selectedProjectProviders,
        IReadOnlyList<ProviderQuotaSnapshot> quota,
        Guid? selectedProjectId)
    {
        List<ProviderHealthEntry> known = [.. providers];
        int ready = known.Count(provider => provider.Enabled && provider.State == ProviderState.Ready);
        int enabled = known.Count(provider => provider.Enabled);
        string summaryText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            text.Resolve(MessageKeys.SidebarProvidersReadyStatus),
            ready,
            enabled);
        string accessible = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            text.Resolve(MessageKeys.SidebarProvidersReadyAccessible),
            ready,
            enabled);
        // PR #106 round-2 review finding 1: authentication and model availability are computed from
        // the SELECTED project's own provider set, never the merged, last-project-wins `known` above
        // -- ProviderHealthEntry.Enabled/Authentication are per-project facts (see LoadAsync's own
        // remarks). No project selected falls back to the merged set, matching every other indicator
        // here when nothing is routed yet.
        IReadOnlyList<ProviderHealthEntry> scoped = selectedProjectProviders ?? known;
        (string authenticationText, string authenticationAccessible) = AuthenticationStatusSummary(scoped);
        (string modelText, string modelAccessible, bool anyModelUnavailable) = ModelAvailabilitySummary(scoped);
        (string quotaText, string quotaAccessible) = SurfaceFormatting.QuotaStatusSummary(text.Current, quota);
        (string hostText, string hostAccessible) = HostConnectivitySummary(selectedProjectId);
        return new(
            summaryText,
            accessible,
            authenticationText,
            authenticationAccessible,
            modelText,
            modelAccessible,
            anyModelUnavailable,
            quotaText,
            quotaAccessible,
            hostText,
            hostAccessible);
    }

    /// <summary>The worst-case local authentication readiness (ADR 0008) across every ENABLED
    /// provider IN THE GIVEN SCOPE (the selected project's own provider set, or the merged set across
    /// every cataloged project when none is selected -- see <see cref="BuildStatusRow"/>) -- disabled
    /// providers are never probed and carry no <see cref="ProviderHealthEntry.Authentication"/>
    /// signal at all (see <see cref="ProviderHealthProjector.Project"/>), so including them here
    /// would misreport "authentication required" for a provider the user never asked to use.
    /// Mirrors <see cref="Forge.Localization.SurfaceFormatting.QuotaStatusSummary"/>'s own
    /// worst-case-across-many shape: <see cref="ProviderHealthAuthentication.CheckFailed"/> outranks
    /// <see cref="ProviderHealthAuthentication.Required"/> (a broken probe hides whether login would
    /// even fix it), which outranks <see langword="null"/> ("not yet checked" -- e.g. the toolchain
    /// probe itself is still pending), which outranks <see cref="ProviderHealthAuthentication.Ready"/>.
    /// No enabled provider at all reports the same "not yet checked" text as a null reading, matching
    /// <see cref="Forge.Providers.ProviderQuotaAggregation.Worst"/>'s own empty-list convention.</summary>
    private (string Text, string Accessible) AuthenticationStatusSummary(IReadOnlyList<ProviderHealthEntry> known)
    {
        ProviderHealthAuthentication?[] enabledAuthentication =
            [.. known.Where(provider => provider.Enabled).Select(provider => provider.Authentication)];
        ProviderHealthAuthentication? worst = enabledAuthentication.Length == 0
            ? null
            : enabledAuthentication.MaxBy(authentication => AuthenticationSeverity(authentication));
        (string textKey, string accessibleKey) = worst switch
        {
            ProviderHealthAuthentication.CheckFailed =>
                (MessageKeys.AuthenticationStatusCheckFailed, MessageKeys.AuthenticationStatusCheckFailedAccessible),
            ProviderHealthAuthentication.Required =>
                (MessageKeys.AuthenticationStatusRequired, MessageKeys.AuthenticationStatusRequiredAccessible),
            ProviderHealthAuthentication.Ready =>
                (MessageKeys.AuthenticationStatusReady, MessageKeys.AuthenticationStatusReadyAccessible),
            null => (MessageKeys.AuthenticationStatusUnknown, MessageKeys.AuthenticationStatusUnknownAccessible),
            _ => throw new ArgumentOutOfRangeException(
                nameof(known), worst, "Unmapped ProviderHealthAuthentication value."),
        };
        return (text.Resolve(textKey), text.Resolve(accessibleKey));
    }

    private static int AuthenticationSeverity(ProviderHealthAuthentication? authentication) => authentication switch
    {
        ProviderHealthAuthentication.CheckFailed => 3,
        ProviderHealthAuthentication.Required => 2,
        null => 1,
        ProviderHealthAuthentication.Ready => 0,
        _ => throw new ArgumentOutOfRangeException(
            nameof(authentication), authentication, "Unmapped ProviderHealthAuthentication value."),
    };

    /// <summary>How many enabled providers, IN THE GIVEN SCOPE (see <see cref="AuthenticationStatusSummary"/>'s
    /// own remarks), are actually usable for model work right now -- toolchain-ready AND
    /// authenticated (ADR 0008: "every enabled provider must report local authentication readiness"
    /// before it counts as ready for model work; mirrors <see cref="Forge.Providers.ProviderToolchainStatus.Ready"/>'s
    /// own per-provider rule). Distinct from <see cref="MessageKeys.SidebarProvidersReadyStatus"/>
    /// above, which counts toolchain state alone (and is never re-scoped -- see
    /// <see cref="BuildStatusRow"/>'s own remarks): a provider can be "installed and current" (counted
    /// there) while still blocking real model work because authentication is missing (not counted
    /// here). Both <paramref name="known"/>'s own enabled count and the available count are computed
    /// from the SAME scope, so the rendered "X/Y" ratio never mixes a selected project's numerator
    /// with a merged-across-every-project denominator. Supersedes the old, unread
    /// <c>AnyKnownProviderUnavailable</c> field -- see <see cref="SidebarStatusRow"/>'s own
    /// remarks.</summary>
    private (string Text, string Accessible, bool AnyUnavailable) ModelAvailabilitySummary(
        IReadOnlyList<ProviderHealthEntry> known)
    {
        int enabled = known.Count(provider => provider.Enabled);
        int available = known.Count(provider =>
            provider.Enabled &&
            provider.State == ProviderState.Ready &&
            provider.Authentication == ProviderHealthAuthentication.Ready);
        string modelText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            text.Resolve(MessageKeys.SidebarModelsAvailableStatus),
            available,
            enabled);
        string modelAccessible = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            text.Resolve(MessageKeys.SidebarModelsAvailableAccessible),
            available,
            enabled);
        return (modelText, modelAccessible, enabled > available);
    }

    /// <summary>Public entry point for <see cref="HostConnectivitySummary"/> (PR #106 round-2 review
    /// finding 2): a route change only ever changes WHICH project is selected -- it never changes any
    /// cataloged project's own workspace/provider/quota data -- so <c>WorkspaceShellPage</c> re-renders
    /// its already-loaded <c>SidebarSnapshot</c> with just this pair recomputed for the newly selected
    /// project, instead of paying for <see cref="LoadAsync"/>'s full per-project catalog scan on every
    /// navigation click (the same cost <see cref="LoadAsync"/>'s own remarks already document, and the
    /// same "no refetch for a render that changes no domain data" shape PR #99/#100/#103 already
    /// established for the sidebar-collapse toggle in this file's own history). Reads
    /// <see cref="connectivityMonitor"/> directly -- like <see cref="HostConnectivitySummary"/> itself,
    /// this is a synchronous, in-memory read, never a fresh Host probe.</summary>
    public (string Text, string Accessible) HostConnectivityFor(Guid? selectedProjectId) =>
        HostConnectivitySummary(selectedProjectId);

    /// <summary>The Host-connectivity status-row indicator (plan 12.6), read from
    /// <see cref="connectivityMonitor"/>'s last actually-observed reading for
    /// <paramref name="selectedProjectId"/> -- never a fresh probe (see
    /// <see cref="IHostConnectivityMonitor"/>'s own remarks) and never another project's reading
    /// (PR #106 review finding 5): Forge Hosts are per-project, so a reading from a different
    /// cataloged project would misreport THIS one's actual reachability. No project selected (nothing
    /// routed yet, or the Forge-settings page) reports the same honest "not yet checked" state as a
    /// selected project with no reading at all. A reading older than
    /// <see cref="HostConnectivityStaleAfter"/> is reported as the distinct "stale" state rather than
    /// trusted as current, satisfying plan 12.6's "stale data" indicator honestly instead of
    /// fabricating freshness for a value this codebase cannot actually keep live without forcing a
    /// Host launch just to render a status row.</summary>
    private (string Text, string Accessible) HostConnectivitySummary(Guid? selectedProjectId)
    {
        HostConnectivityReading? observed =
            selectedProjectId is { } projectId ? connectivityMonitor.LastObserved(projectId) : null;
        (string textKey, string accessibleKey) = observed switch
        {
            null => (MessageKeys.HostConnectivityUnknown, MessageKeys.HostConnectivityUnknownAccessible),
            { } reading when clock.UtcNow - reading.ObservedAt > HostConnectivityStaleAfter =>
                (MessageKeys.HostConnectivityStale, MessageKeys.HostConnectivityStaleAccessible),
            { Connected: true } => (MessageKeys.HostConnectivityConnected, MessageKeys.HostConnectivityConnectedAccessible),
            { Connected: false } =>
                (MessageKeys.HostConnectivityDisconnected, MessageKeys.HostConnectivityDisconnectedAccessible),
        };
        return (text.Resolve(textKey), text.Resolve(accessibleKey));
    }
}
