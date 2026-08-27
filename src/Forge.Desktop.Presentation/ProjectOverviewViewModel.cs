using Forge.Application;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop.Presentation;

/// <summary>One sprint card on the project overview (plan section 4.2): active or recent-history,
/// distinguished by <see cref="Terminal"/>. <see cref="DisplayTitle"/> is the sprint's own frozen
/// title when it has one, and <see cref="SprintDisplayTitle"/>'s localized "Sprint {N}" fallback
/// when it does not (ADR 0057) -- always renderable, never a synthesized value in any durable
/// contract.</summary>
public sealed record ProjectOverviewSprintCard(
    Guid SprintId,
    int CreationSequence,
    string StateText,
    bool Terminal,
    bool RequiresHumanAttention,
    string? AttentionReasonKey,
    string DisplayTitle);

public sealed record ProjectOverviewSnapshot(
    string DisplayName,
    string Root,
    bool Initialized,
    bool StartupReady,
    bool InitializeEnabled,
    bool RecoverEnabled,
    IReadOnlyList<ProjectOverviewSprintCard> ActiveSprints,
    IReadOnlyList<ProjectOverviewSprintCard> RecentHistory,
    IReadOnlyList<AvailableAction> SuggestedActions,
    IReadOnlyList<ProviderHealthEntry> Providers);

/// <summary>
/// Plan section 4.2's project overview: startup/repository readiness, active sprint cards with
/// attention reasons, recent completed/cancelled sprints, ranked suggested actions, and the
/// create-sprint/initialize/recover/project-settings entry points. Every lifecycle mutation here
/// delegates to <see cref="MainPageViewModel"/> -- the same methods `Forge.Desktop`'s previous
/// monolithic page called -- so no existing capability or its already-reviewed behavior is
/// reimplemented (plan 12.1).
/// </summary>
public sealed class ProjectOverviewViewModel(
    ForgeApplication application, MainPageViewModel legacy, SurfaceText text)
{
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly MainPageViewModel legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));

    // ADR 0057: needed only to resolve the untitled-sprint display fallback. Bound to one culture
    // like every other view model here -- the shell rebuilds this instance on a language change.
    private readonly SurfaceText text = text ?? throw new ArgumentNullException(nameof(text));

    private const int MaxRecentHistory = 10;

    public async Task<ProjectOverviewSnapshot> LoadAsync(
        string root, string? alias, CancellationToken cancellationToken)
    {
        ProjectOverview overview = await application
            .GetOverviewAsync(root, SnapshotDetail.Summary, null, cancellationToken)
            .ConfigureAwait(false);
        ProjectSnapshot snapshot = overview.Snapshot;
        IReadOnlyList<AvailableAction> actions = await application
            .GetAvailableActionsAsync(root, null, cancellationToken)
            .ConfigureAwait(false);

        List<ProjectOverviewSprintCard> orderedActive =
        [
            .. snapshot.Sprints
                .Where(sprint => !WorkflowStateMachines.IsTerminal(sprint.State))
                .OrderBySidebarRule(sprint => sprint.State, sprint => sprint.CreationSequence)
                .Select(sprint => ToCard(sprint, terminal: false)),
        ];
        List<ProjectOverviewSprintCard> orderedHistory =
        [
            .. snapshot.Sprints
                .Where(sprint => WorkflowStateMachines.IsTerminal(sprint.State))
                .OrderByDescending(sprint => sprint.CreationSequence)
                .Take(MaxRecentHistory)
                .Select(sprint => ToCard(sprint, terminal: true)),
        ];
        return new(
            ProjectDisplayName.Resolve(root, alias),
            root,
            snapshot.Project.Initialized,
            overview.Startup.FirstFailure is null,
            !snapshot.Project.Initialized && overview.Startup.AllowsProjectMutation,
            overview.Startup.FirstFailure is not null,
            orderedActive,
            orderedHistory,
            actions,
            snapshot.Providers);
    }

    private ProjectOverviewSprintCard ToCard(SprintStatus sprint, bool terminal)
    {
        bool humanAttention = !terminal && SprintOrderingRank.RequiresHumanAttention(sprint.State);
        return new(
            sprint.Id,
            sprint.CreationSequence,
            SurfaceFormatting.Machine(sprint.State),
            terminal,
            humanAttention,
            humanAttention ? SidebarViewModel.AttentionReasonKey(sprint.State) : null,
            SprintDisplayTitle.Resolve(sprint.Title, sprint.CreationSequence, text));
    }

    public Task<ProjectSnapshot> GetProjectSnapshotAsync(string? root, CancellationToken cancellationToken) =>
        legacy.GetProjectSnapshotAsync(root, cancellationToken);

    public string InitializePrompt(ProjectSnapshot snapshot) => legacy.InitializePrompt(snapshot);

    public Task<string> InitializeAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken) =>
        legacy.InitializeAsync(snapshot, cancellationToken);

    public Task<string> RecoverAsync(string? root, bool confirmed, CancellationToken cancellationToken) =>
        legacy.RecoverAsync(root, confirmed, cancellationToken);

    public Task<string> CreateSprintAsync(string? root, string? title, CancellationToken cancellationToken) =>
        legacy.CreateSprintAsync(root, title, cancellationToken);

    public Task<string> RunSprintAsync(string? root, string? sprintId, CancellationToken cancellationToken) =>
        legacy.RunSprintAsync(root, sprintId, cancellationToken);

    public Task<string> ResumeSprintAsync(string? root, string? sprintId, CancellationToken cancellationToken) =>
        legacy.ResumeSprintAsync(root, sprintId, cancellationToken);

    public Task<string> CancelSprintAsync(
        string? root, string? sprintId, bool confirmed, CancellationToken cancellationToken) =>
        legacy.CancelSprintAsync(root, sprintId, confirmed, cancellationToken);

    public string SprintCancelPrompt(string? sprintId) => legacy.SprintCancelPrompt(sprintId);
}
