using Forge.Application;
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

/// <summary>Plan section 4.1's bottom status row. <see cref="QuotaStatusText"/> is always a
/// deliberately unknown/"not yet available" placeholder in this slice (Slice 7 owns real quota data;
/// plan: "render... unknown, never inferred") -- never a fabricated number.</summary>
public sealed record SidebarStatusRow(
    string ProviderSummaryText,
    string ProviderAccessibleText,
    bool AnyKnownProviderUnavailable,
    string QuotaStatusText);

public sealed record SidebarSnapshot(
    IReadOnlyList<SidebarProjectItem> Projects,
    SidebarStatusRow Status);

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

        return new(projects, BuildStatusRow(knownProviders.Values));
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
    private SidebarStatusRow BuildStatusRow(IEnumerable<ProviderHealthEntry> providers)
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
        return new(summaryText, accessible, anyUnavailable, text.Resolve(MessageKeys.QuotaStatusUnavailable));
    }
}
