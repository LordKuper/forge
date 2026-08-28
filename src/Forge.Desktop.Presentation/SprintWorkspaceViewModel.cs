using System.Globalization;
using Forge.Application;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 4.3's sprint-workspace route, scoped to a caller-known project/sprint (routing
/// already resolved this before the page is shown, so this never re-asks for the sprint itself).
/// Composes three things: <see cref="MainPageViewModel"/>'s already-tested human-only capabilities
/// (gate/confirm/test-work/finalize/supersede -- ADR 0037; a nodeId of <see langword="null"/> always
/// resolves to the built-in graph's own canonical node, so the caller never needs a manual entry
/// field for the ordinary path), <see cref="Timeline"/> (Slice 6's timeline pane), and
/// <see cref="Actions"/> (Slice 6's stop/stage-transition renderer). Node/attempt ids remain
/// parameters here because the underlying capability itself is generic (the CLI accepts an explicit
/// `--node`/attempt id too); it is <c>WorkspaceShellPage.SprintWorkspace.cs</c>'s job to never
/// collect one from a manual text field, deriving it from context instead (see
/// <see cref="FindActiveAttemptId"/>).
/// </summary>
public sealed class SprintWorkspaceViewModel(
    MainPageViewModel legacy,
    ForgeApplication application,
    ProjectCatalogStore catalog,
    Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
    SurfaceText text)
{
    private readonly MainPageViewModel legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));

    public SprintTimelineViewModel Timeline { get; } = new(application, catalog, text);

    public SprintActionsViewModel Actions { get; } = new(application, resolveMutations, text);

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Plan 4.3's sticky status header, sourced from the same full project-snapshot query
    /// the CLI already uses (<see cref="ForgeApplication.GetProjectSnapshotAsync(string?,SnapshotDetail,Guid?,CancellationToken)"/>)
    /// plus the bounded workspace summary (Slice 4) for current-stage/progress -- never a
    /// locally-recomputed notion of either (see <see cref="SprintStatusHeaderProjector"/>).</summary>
    public async Task<(SprintStatusHeaderData Header, ProjectSnapshot Snapshot)> RefreshHeaderAsync(
        string? projectRoot, string projectDisplayName, Guid sprintId, CancellationToken cancellationToken)
    {
        ProjectSnapshot snapshot = await application
            .GetProjectSnapshotAsync(projectRoot, SnapshotDetail.Full, sprintId, cancellationToken)
            .ConfigureAwait(false);
        // Diff statistics stay opted out until the header actually draws them (ADR 0069's own
        // deferral: slice S13 adds the control together with the field it reads). Flipping this one
        // argument is what S13 does; asking now would cost `git` processes per active sprint on
        // every header refresh for a value nothing renders (PR #126 review finding 2).
        ProjectWorkspaceSummary summary = await application
            .GetWorkspaceSummaryAsync(projectRoot, false, cancellationToken)
            .ConfigureAwait(false);
        return (SprintStatusHeaderProjector.Build(projectDisplayName, snapshot, summary, text), snapshot);
    }

    /// <summary>The sprint's exact currently-running attempt, if any -- the context-derived default
    /// for attempt supersession (plan 12.1/11 Slice 6 item 3: never a manually typed attempt id).
    /// <see langword="null"/> when nothing is running, in which case the caller shows no supersede
    /// control at all rather than one that would fail.</summary>
    public static Guid? FindActiveAttemptId(SprintDetails? details) =>
        details?.Attempts
            .Where(attempt => attempt.State == "running")
            .Select(attempt => Guid.TryParse(attempt.Id, out Guid id) ? (Guid?)id : null)
            .FirstOrDefault(id => id is not null);

    /// <summary>Whether the sprint's current stage has a pending human gate at all -- the
    /// context-derived condition for showing the approve/reject controls (no gate, no controls;
    /// never a manual node id to decide otherwise).</summary>
    public static bool HasPendingGate(SprintDetails? details) =>
        details?.Nodes.Any(node => node.State == "awaiting_human") ?? false;

    public Task<MainPageSnapshot> RefreshAsync(string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        legacy.RefreshAsync(projectRoot, Id(sprintId), cancellationToken);

    public Task<string> PollEventsAsync(string? projectRoot, CancellationToken cancellationToken) =>
        legacy.PollEventsAsync(projectRoot, cancellationToken);

    public string GatePrompt(Guid sprintId, string? nodeId) => legacy.GatePrompt(Id(sprintId), nodeId);

    public Task<string> ResolveGateAsync(
        string? projectRoot, Guid sprintId, string? nodeId, bool approved, bool confirmed,
        CancellationToken cancellationToken) =>
        legacy.ResolveGateAsync(projectRoot, Id(sprintId), nodeId, approved, confirmed, cancellationToken);

    /// <summary>ADR 0054's reserved `sprint.post_message` capability. Not confirmable -- posting a
    /// message is additive.</summary>
    public Task<string> PostMessageAsync(
        string? projectRoot, Guid sprintId, string? messageText, CancellationToken cancellationToken) =>
        legacy.PostMessageAsync(projectRoot, Id(sprintId), messageText, cancellationToken);

    public string AttemptSupersedePrompt(Guid sprintId, string? attemptId) =>
        legacy.AttemptSupersedePrompt(Id(sprintId), attemptId);

    public Task<string> SupersedeAttemptAsync(
        string? projectRoot, Guid sprintId, string? attemptId, string? instruction, bool confirmed,
        CancellationToken cancellationToken) =>
        legacy.SupersedeAttemptAsync(projectRoot, Id(sprintId), attemptId, instruction, confirmed, cancellationToken);

    public string ConfirmPrompt(Guid sprintId, string? nodeId, string? definitionOfDone, string? evidence) =>
        legacy.ConfirmPrompt(Id(sprintId), nodeId, definitionOfDone, evidence);

    public Task<string> ConfirmNodeAsync(
        string? projectRoot,
        Guid sprintId,
        string? nodeId,
        ConfirmationOutcome outcome,
        string? definitionOfDone,
        string? evidenceKind,
        string? evidence,
        bool confirmed,
        CancellationToken cancellationToken) =>
        legacy.ConfirmNodeAsync(
            projectRoot, Id(sprintId), nodeId, outcome, definitionOfDone, evidenceKind, evidence, confirmed,
            cancellationToken);

    public string TestWorkPrompt(Guid sprintId, string? nodeId, string? justification) =>
        legacy.TestWorkPrompt(Id(sprintId), nodeId, justification);

    public Task<string> RecordTestWorkAsync(
        string? projectRoot,
        Guid sprintId,
        string? nodeId,
        TestWorkOutcome outcome,
        string? justification,
        bool confirmed,
        CancellationToken cancellationToken) =>
        legacy.RecordTestWorkAsync(projectRoot, Id(sprintId), nodeId, outcome, justification, confirmed, cancellationToken);

    public string FinalizePrompt(Guid sprintId, string? nodeId) => legacy.FinalizePrompt(Id(sprintId), nodeId);

    public Task<string> FinalizeSprintAsync(
        string? projectRoot, Guid sprintId, string? nodeId, bool confirmed, CancellationToken cancellationToken) =>
        legacy.FinalizeSprintAsync(projectRoot, Id(sprintId), nodeId, confirmed, cancellationToken);

    public Task<string> RunSprintAsync(string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        legacy.RunSprintAsync(projectRoot, Id(sprintId), cancellationToken);

    public Task<string> ResumeSprintAsync(string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        legacy.ResumeSprintAsync(projectRoot, Id(sprintId), cancellationToken);

    public string SprintCancelPrompt(Guid sprintId) => legacy.SprintCancelPrompt(Id(sprintId));

    public Task<string> CancelSprintAsync(
        string? projectRoot, Guid sprintId, bool confirmed, CancellationToken cancellationToken) =>
        legacy.CancelSprintAsync(projectRoot, Id(sprintId), confirmed, cancellationToken);

    /// <summary>Plan 12.1 final-sweep gap 2: the sprint workspace's last scroll offset, restored on
    /// route entry when the page's own in-session cache (<c>WorkspaceShellPage.SprintWorkspace.cs</c>'s
    /// <see cref="Forge.Desktop.Presentation.ScrollPositionPersistCoordinator"/>) has nothing pending
    /// for this sprint yet -- i.e. the first render after an app restart. <see langword="null"/> when
    /// nothing was ever persisted, in which case the caller scrolls to the top exactly like before
    /// this gap was closed.</summary>
    public async Task<double?> LoadScrollPositionAsync(Guid projectId, Guid sprintId, CancellationToken cancellationToken)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        ProjectCatalogEntry? entry = listing.Entries.FirstOrDefault(candidate => candidate.ProjectId == projectId);
        return entry?.SprintScrollPositions?.TryGetValue(Id(sprintId), out double value) == true ? value : null;
    }

    public Task<ProjectCatalogResult> SaveScrollPositionAsync(
        Guid projectId, Guid sprintId, double position, CancellationToken cancellationToken) =>
        catalog.SetSprintScrollPositionAsync(projectId, sprintId, position, cancellationToken);
}
