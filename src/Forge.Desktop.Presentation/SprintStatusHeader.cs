using System.Globalization;
using Forge.Application;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 4.3's sticky sprint-workspace status header: everything shown in the main row, plus
/// an expandable <see cref="DetailsText"/> for UUIDs/paths/base commit -- never mixed into the main
/// row itself (plan: "Show UUIDs, paths, base commit, worktrees, and routing detail only in an
/// expandable details view"). <see cref="ActiveProviderModelText"/> renders the sprint's current
/// active-operation attempt's <see cref="Forge.Domain.AttemptSnapshot.Provider"/>/<c>.Model</c>
/// once both are known, and falls back to the "not yet available" placeholder otherwise -- no
/// active operation (nothing currently running), a non-model-bearing role (intake, confirmation,
/// finalization; nothing was ever routed), or a legacy attempt recorded before this field existed,
/// the same honest-omission posture Slice 5/7 already apply to account quota.
/// </summary>
public sealed record SprintStatusHeaderData(
    string ProjectDisplayName,
    int SprintSequence,
    string SprintStateText,
    string? CurrentStageId,
    int StagesCompleted,
    int StagesTotal,
    DateTimeOffset? LastActivityAt,
    string ActiveProviderModelText,
    int OpenFindingsCount,
    int RetryRemaining,
    DateTimeOffset? ResumeNotBefore,
    string DetailsText);

/// <summary>
/// Builds <see cref="SprintStatusHeaderData"/> from data the Host already computed -- never a second,
/// locally-derived notion of "current stage." Stage/progress prefers the bounded
/// <see cref="ProjectWorkspaceSummary"/> row (<see cref="WorkspaceSummaryProjector"/>, which itself
/// reuses <c>StageTransitionAssessor.ResolveCurrentStageId</c>) and falls back to a plain
/// count over <see cref="SprintDetails.Nodes"/> only for a terminal sprint, which
/// <see cref="ProjectWorkspaceSummary.ActiveSprints"/> never includes (plan 4.1: terminal sprints sit
/// behind project history, not the active list) -- that fallback counts, it never re-derives which
/// stage is "current."
/// </summary>
public static class SprintStatusHeaderProjector
{
    public static SprintStatusHeaderData Build(
        string projectDisplayName,
        ProjectSnapshot snapshot,
        ProjectWorkspaceSummary summary,
        SurfaceText text)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);
        SprintDetails? details = snapshot.Details;
        SprintStatus? sprint = details is { } found
            ? snapshot.Sprints.FirstOrDefault(candidate => candidate.Id == found.SprintId)
            : null;
        SprintWorkspaceSummary? active = details is { } withId
            ? summary.ActiveSprints.FirstOrDefault(candidate => candidate.SprintId == withId.SprintId)
            : null;

        string? currentStageId = active?.CurrentStageId;
        int stagesCompleted = active?.StagesCompleted ?? 0;
        int stagesTotal = active?.StagesTotal ?? 0;
        if (active is null && details is { } terminalDetails)
        {
            // Terminal sprint: WorkspaceSummaryProjector never folds it, so fall back to a plain
            // count -- never a re-derivation of "current stage" for a sprint that no longer has one.
            stagesCompleted = terminalDetails.Nodes.Count(node =>
                node.State is "succeeded" or "skipped");
            stagesTotal = terminalDetails.Nodes.Count;
        }

        DateTimeOffset? lastActivity = details?.Attempts
            .Select(attempt => attempt.LastActivityAt)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        if (lastActivity == default)
        {
            lastActivity = null;
        }

        int openFindings = details?.Findings.Count(finding => finding.State == "open") ?? 0;
        int retryRemaining = details?.Routing.RetryRemaining ?? 0;
        DateTimeOffset? resumeNotBefore = details?.Routing.ResumeNotBefore;
        string detailsText = string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}\n" +
                $"{text.Resolve(MessageKeys.SprintIdLabel)} {details?.SprintId.ToString("D", CultureInfo.InvariantCulture)}\n" +
                $"base_commit={sprint?.BaseSha ?? "-"} workflow={sprint?.Workflow ?? "-"}");

        // The active-operation attempt's own recorded provider/model (plan section 12.3) -- looked
        // up by id rather than "the most recently active attempt" so a settled-but-not-yet-cleaned-up
        // attempt row never masquerades as what is currently running. No active operation (terminal
        // sprint, nothing running right now), a non-model-bearing role, or a legacy attempt with
        // neither field recorded all fall back to the same placeholder as before.
        EntityStatus? activeAttempt = active?.ActiveOperationAttemptId is { } activeAttemptId
            ? details?.Attempts.FirstOrDefault(
                attempt => attempt.Id == activeAttemptId.ToString("D", CultureInfo.InvariantCulture))
            : null;
        string providerModelText = activeAttempt is { Provider: { Length: > 0 } provider, Model: { Length: > 0 } model }
            ? string.Format(CultureInfo.InvariantCulture, text.Resolve(MessageKeys.SprintStatusHeaderProviderModelText), provider, model)
            : text.Resolve(MessageKeys.SprintStatusHeaderProviderModelUnavailable);

        return new(
            projectDisplayName,
            sprint?.CreationSequence ?? 0,
            SurfaceFormatting.Machine(sprint?.State),
            currentStageId,
            stagesCompleted,
            stagesTotal,
            lastActivity,
            providerModelText,
            openFindings,
            retryRemaining,
            resumeNotBefore,
            detailsText);
    }
}
