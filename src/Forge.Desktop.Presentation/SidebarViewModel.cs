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

/// <summary>One navigable row in a project's capped sprint history (plan 12.1 final-sweep gap 3):
/// a terminal (completed/cancelled) sprint, distinct from <see cref="SidebarSprintItem"/> only in
/// that it never carries attention/progress/active-operation fields no terminal sprint can have.
/// </summary>
public sealed record SidebarHistoryItem(Guid SprintId, int CreationSequence, string StateText, string AccessibleName);

/// <summary>One sidebar project row. <see cref="ActiveSprints"/> is already ordered by
/// <see cref="SprintOrderingRank"/> and shown only while <see cref="SprintListExpanded"/> (plan 12.1
/// final-sweep gap 1; default <see langword="true"/> so an upgrading user sees exactly what every
/// prior release always rendered). <see cref="History"/> is every terminal sprint, newest first,
/// capped at <see cref="SidebarViewModel.MaxSidebarHistory"/> -- reachable through this always-visible
/// separate list rather than crowding <see cref="ActiveSprints"/> (plan 12.1 final-sweep gap 3), and
/// never hidden by <see cref="SprintListExpanded"/> since it is not part of the active list that
/// toggle governs.</summary>
public sealed record SidebarProjectItem(
    Guid ProjectId,
    string Root,
    string DisplayName,
    bool Available,
    bool Initialized,
    IReadOnlyList<SidebarSprintItem> ActiveSprints,
    bool SprintListExpanded,
    IReadOnlyList<SidebarHistoryItem> History,
    string AccessibleName);

/// <summary>Plan section 4.1's bottom status row. <see cref="QuotaStatusText"/>/<see cref="QuotaAccessibleText"/>
/// report the worst-case state across every known provider's <see cref="ProviderQuotaSnapshot"/>
/// (<see cref="Forge.Localization.SurfaceFormatting.QuotaStatusSummary"/>). ADR 0052 found no
/// provider integration in this codebase exposes a verified quota signal, so today this always
/// resolves to the "unknown" text -- a truthful report, never a fabricated number (plan: "render...
/// unknown, never inferred").</summary>
public sealed record SidebarStatusRow(
    string ProviderSummaryText,
    string ProviderAccessibleText,
    bool AnyKnownProviderUnavailable,
    string QuotaStatusText,
    string QuotaAccessibleText);

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
    SurfaceTextProvider text)
{
    private readonly ProjectCatalogStore catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly IFolderPickerPort folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
    private readonly SurfaceTextProvider text = text ?? throw new ArgumentNullException(nameof(text));

    /// <summary>Plan 12.1 final-sweep gap 3's history cap -- matches
    /// <see cref="ProjectOverviewViewModel.MaxRecentHistory"/>'s own bound so the sidebar and the
    /// project overview page never disagree about how many recent terminal sprints "reachable
    /// without crowding" means.</summary>
    public const int MaxSidebarHistory = 10;

    public async Task<SidebarSnapshot> LoadAsync(CancellationToken cancellationToken)
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
            string displayName = ProjectDisplayName.Resolve(entry.Root, entry.Alias);
            List<SidebarSprintItem> sprints =
            [
                .. summary.ActiveSprints
                    .OrderBySidebarRule(sprint => sprint.State, sprint => sprint.CreationSequence)
                    .Select(sprint => ToSprintItem(displayName, sprint)),
            ];
            List<SidebarHistoryItem> history =
            [
                .. snapshot.Sprints
                    .Where(sprint => WorkflowStateMachines.IsTerminal(sprint.State))
                    .OrderByDescending(sprint => sprint.CreationSequence)
                    .Take(MaxSidebarHistory)
                    .Select(sprint => ToHistoryItem(displayName, sprint)),
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
        return new(projects, BuildStatusRow(knownProviders.Values, quota), IsCollapsed(userConfiguration));
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

    /// <summary>Plan 12.1 final-sweep gap 3: a terminal sprint's sidebar-history row. Never carries
    /// an attention suffix -- <see cref="SprintOrderingRank.RequiresHumanAttention"/> is only ever
    /// true for a non-terminal state, so a terminal sprint can never need it.</summary>
    private SidebarHistoryItem ToHistoryItem(string projectDisplayName, SprintStatus sprint)
    {
        string stateText = SurfaceFormatting.Machine(sprint.State);
        string accessible = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{projectDisplayName}, {text.Resolve(MessageKeys.SprintIdLabel)} {sprint.CreationSequence}, {stateText}");
        return new(sprint.Id, sprint.CreationSequence, stateText, accessible);
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
        IEnumerable<ProviderHealthEntry> providers, IReadOnlyList<ProviderQuotaSnapshot> quota)
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
        bool anyUnavailable = enabled > ready;
        (string quotaText, string quotaAccessible) = SurfaceFormatting.QuotaStatusSummary(text.Current, quota);
        return new(summaryText, accessible, anyUnavailable, quotaText, quotaAccessible);
    }
}
