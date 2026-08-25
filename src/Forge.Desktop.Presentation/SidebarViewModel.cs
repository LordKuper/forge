using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop.Presentation;

/// <summary>One sidebar row for a non-terminal sprint (plan section 4.1). <see cref="AccessibleName"/>
/// is a full sentence naming state and (when set) the attention reason -- plan 12.6: "every status has
/// text and an accessible... name," color is never the only carrier.</summary>
public sealed record SidebarSprintItem(
    Guid SprintId,
    int CreationSequence,
    string StateText,
    bool RequiresHumanAttention,
    string? CurrentStageId,
    int StagesCompleted,
    int StagesTotal,
    bool HasActiveOperation,
    string AccessibleName);

/// <summary>One sidebar project row. <see cref="ActiveSprints"/> is already ordered by
/// <see cref="SprintOrderingRank"/>; <see cref="HistoryCount"/> is every terminal sprint, reachable
/// through a separate history entry rather than crowding this list (plan 12.1).</summary>
public sealed record SidebarProjectItem(
    Guid ProjectId,
    string Root,
    string DisplayName,
    bool Available,
    bool Initialized,
    IReadOnlyList<SidebarSprintItem> ActiveSprints,
    int HistoryCount,
    string AccessibleName);

/// <summary>Plan section 4.1's bottom status row, distinguishing every state plan 12.6 requires:
/// provider (toolchain) health, authentication, model availability, quota (including unknown
/// quota), and Host connectivity (including stale connectivity data). Every state is a
/// <c>XxxText</c>/<c>XxxAccessibleText</c> pair, this file's established screen-reader convention --
/// color is never the only carrier (plan 12.6).
/// <see cref="ProviderSummaryText"/>/<see cref="ProviderAccessibleText"/> report the enabled
/// providers whose toolchain install is ready, independent of authentication.
/// <see cref="AuthenticationStatusText"/>/<see cref="AuthenticationAccessibleText"/> report the
/// worst-case authentication readiness across enabled providers.
/// <see cref="ModelAvailabilityText"/>/<see cref="ModelAvailabilityAccessibleText"/> and
/// <see cref="AnyModelUnavailable"/> report how many enabled providers are actually usable for model
/// work right now -- toolchain-ready AND authenticated -- superseding the old
/// <c>AnyKnownProviderUnavailable</c> field (computed but never read by any UI): that field only
/// considered toolchain state, never authentication, so it could not distinguish "installed but not
/// authenticated" from "fully usable." <see cref="QuotaStatusText"/>/<see cref="QuotaAccessibleText"/>
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
/// User scope), never tied to any one project, distinct from a future per-project sprint-list
/// disclosure.</summary>
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

    /// <summary>Loads the sidebar snapshot. <paramref name="selectedProjectId"/> is the workspace
    /// shell's currently routed project (<c>WorkspaceRoute.ProjectId</c>), if any -- PR #106 review
    /// finding 5: <see cref="connectivityMonitor"/> tracks a reading per project (Forge Hosts are
    /// per-project, one pipe per <c>InstanceIdentity.ComputePipeName(instanceId, projectId)</c>), so
    /// the status row's Host-connectivity text must name THIS project's own last-observed reading,
    /// never an arbitrary other cataloged project's. <see langword="null"/> (nothing routed yet, or
    /// the Forge-settings page, which names no single project) reports the same honest "not yet
    /// checked" state as a project with no reading at all.</summary>
    public async Task<SidebarSnapshot> LoadAsync(CancellationToken cancellationToken, Guid? selectedProjectId = null)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        List<SidebarProjectItem> projects = new(listing.Entries.Count);
        Dictionary<string, ProviderHealthEntry> knownProviders = new(StringComparer.Ordinal);
        foreach (ProjectCatalogEntry entry in listing.Entries)
        {
            ProjectWorkspaceSummary summary = await application
                .GetWorkspaceSummaryAsync(entry.Root, cancellationToken)
                .ConfigureAwait(false);
            foreach (ProviderHealthEntry provider in summary.Providers)
            {
                knownProviders[provider.Id] = provider;
            }

            ProjectSnapshot snapshot = await application
                .GetProjectSnapshotAsync(entry.Root, cancellationToken)
                .ConfigureAwait(false);
            int historyCount = snapshot.Sprints.Count(sprint => WorkflowStateMachines.IsTerminal(sprint.State));
            string displayName = ProjectDisplayName.Resolve(entry.Root, entry.Alias);
            List<SidebarSprintItem> sprints =
            [
                .. summary.ActiveSprints
                    .OrderBySidebarRule(sprint => sprint.State, sprint => sprint.CreationSequence)
                    .Select(sprint => ToSprintItem(displayName, sprint)),
            ];
            projects.Add(new(
                entry.ProjectId,
                entry.Root,
                displayName,
                summary.Available,
                summary.Initialized,
                sprints,
                historyCount,
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
            projects, BuildStatusRow(knownProviders.Values, quota, selectedProjectId), IsCollapsed(userConfiguration));
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

    private SidebarSprintItem ToSprintItem(string projectDisplayName, SprintWorkspaceSummary sprint)
    {
        bool humanAttention = SprintOrderingRank.RequiresHumanAttention(sprint.State);
        string stateText = SurfaceFormatting.Machine(sprint.State);
        string attentionSuffix = humanAttention
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $", {text.Resolve(AttentionReasonKey(sprint.State))}")
            : string.Empty;
        string accessible = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{projectDisplayName}, {text.Resolve(MessageKeys.SprintIdLabel)} {sprint.CreationSequence}, {stateText}{attentionSuffix}");
        return new(
            sprint.SprintId,
            sprint.CreationSequence,
            stateText,
            humanAttention,
            sprint.CurrentStageId,
            sprint.StagesCompleted,
            sprint.StagesTotal,
            sprint.HasActiveOperation,
            accessible);
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
        IEnumerable<ProviderHealthEntry> providers, IReadOnlyList<ProviderQuotaSnapshot> quota, Guid? selectedProjectId)
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
        (string authenticationText, string authenticationAccessible) = AuthenticationStatusSummary(known);
        (string modelText, string modelAccessible, bool anyModelUnavailable) = ModelAvailabilitySummary(known, enabled);
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
    /// provider -- disabled providers are never probed and carry no <see cref="ProviderHealthEntry.Authentication"/>
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

    /// <summary>How many enabled providers are actually usable for model work right now --
    /// toolchain-ready AND authenticated (ADR 0008: "every enabled provider must report local
    /// authentication readiness" before it counts as ready for model work; mirrors
    /// <see cref="Forge.Providers.ProviderToolchainStatus.Ready"/>'s own per-provider rule). Distinct
    /// from <see cref="MessageKeys.SidebarProvidersReadyStatus"/> above, which counts toolchain state
    /// alone: a provider can be "installed and current" (counted there) while still blocking real
    /// model work because authentication is missing (not counted here). Supersedes the old, unread
    /// <c>AnyKnownProviderUnavailable</c> field -- see <see cref="SidebarStatusRow"/>'s own
    /// remarks.</summary>
    private (string Text, string Accessible, bool AnyUnavailable) ModelAvailabilitySummary(
        IReadOnlyList<ProviderHealthEntry> known, int enabled)
    {
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
