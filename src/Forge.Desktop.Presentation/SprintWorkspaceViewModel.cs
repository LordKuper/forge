using System.Globalization;
using Forge.Domain;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 4.3's sprint-workspace route, scoped to a caller-known project/sprint (routing
/// already resolved this before the page is shown, so unlike <see cref="MainPageViewModel"/>'s own
/// blank-means-active-sprint entry, this never re-asks for the sprint itself). The status
/// header/timeline/contextual-action renderer plan section 4.3 describes are Slice 6's own
/// deliverable; this view-model exists so every gate/confirm/test-work/finalize/supersede/poll/
/// lifecycle capability <see cref="MainPageViewModel"/> already exposes remains reachable from the
/// new shell in the meantime (plan 12.1) rather than being dropped while Slice 6 is pending. Node and
/// attempt ids are still caller-supplied here -- removing those, too, is explicitly Slice 6's job
/// ("remove manual ID fields from ordinary workflows").
/// </summary>
public sealed class SprintWorkspaceViewModel(MainPageViewModel legacy)
{
    private readonly MainPageViewModel legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    public Task<MainPageSnapshot> RefreshAsync(string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        legacy.RefreshAsync(projectRoot, Id(sprintId), cancellationToken);

    public Task<string> PollEventsAsync(string? projectRoot, CancellationToken cancellationToken) =>
        legacy.PollEventsAsync(projectRoot, cancellationToken);

    public string GatePrompt(Guid sprintId, string? nodeId) => legacy.GatePrompt(Id(sprintId), nodeId);

    public Task<string> ResolveGateAsync(
        string? projectRoot, Guid sprintId, string? nodeId, bool approved, bool confirmed,
        CancellationToken cancellationToken) =>
        legacy.ResolveGateAsync(projectRoot, Id(sprintId), nodeId, approved, confirmed, cancellationToken);

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
}
