using System.Globalization;
using Forge.Application;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 4.3/6.4's contextual-action renderer for the two genuinely new destructive actions
/// this slice adds a UI for: stop-current-operation (plan 7, ADR 0044/0047) and stage transition
/// (plan 8, ADR 0045/0046/0048). Every other <see cref="AvailableAction"/> row (resume/run/cancel
/// sprint) is rendered by the page directly from the loaded list and executed through the existing,
/// already-tested <see cref="MainPageViewModel"/> lifecycle methods -- this type exists specifically
/// for the two actions that need a fresh, Host-authoritative legality check immediately before their
/// own confirmation dialog, never a value read from an earlier render (plan 8.1/12.5: "the UI must
/// not locally compute or cache whether a target is enabled").
/// </summary>
public sealed class SprintActionsViewModel(
    ForgeApplication application,
    Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
    SurfaceText text)
{
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations =
        resolveMutations ?? throw new ArgumentNullException(nameof(resolveMutations));
    private readonly SurfaceText text = text ?? throw new ArgumentNullException(nameof(text));

    public Task<IReadOnlyList<AvailableAction>> LoadAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        application.GetAvailableActionsAsync(projectRoot, sprintId, cancellationToken);

    public static AvailableAction? Find(IReadOnlyList<AvailableAction> actions, string actionId)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return actions.FirstOrDefault(action => action.ActionId == actionId);
    }

    /// <summary>Re-reads the current action list and returns the stop action only if it is still
    /// present -- the exact "re-fetch/re-validate before committing" guarantee plan 12.4/12.5 asks
    /// for, applied right before showing the confirmation dialog rather than trusting whatever was
    /// on screen from an earlier render.</summary>
    public async Task<AvailableAction?> FindFreshStopTargetAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AvailableAction> fresh = await LoadAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return Find(fresh, AvailableActionProjector.StopCurrentOperationActionId);
    }

    /// <summary>Names the exact node/attempt the stop would act on (plan 7.1/12.4: "the UI never
    /// asks for an ID already known"; the confirmation instead shows it) -- never a generic "are you
    /// sure?".</summary>
    public string StopPrompt(AvailableAction stopAction)
    {
        ArgumentNullException.ThrowIfNull(stopAction);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.AttemptStopTargetLabel)} {stopAction.Target.NodeId}\n" +
                $"{text.Resolve(MessageKeys.AttemptIdLabel)} {stopAction.Target.AttemptId:D}");
    }

    public async Task<string> StopAsync(
        string? projectRoot, AvailableAction stopAction, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stopAction);
        Guid sprintId = stopAction.Target.SprintId ?? Guid.Empty;
        Guid attemptId = stopAction.Target.AttemptId ?? Guid.Empty;
        IForgeMutations mutations = await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false);
        return await UseMutationsAsync(mutations, async () =>
        {
            StopOperationResult result = await mutations
                .StopCurrentOperationAsync(projectRoot, sprintId, attemptId, confirmed, cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result.Succeeded ? MessageKeys.AttemptStopped : MessageKeys.AttemptStopAction),
                result.DiagnosticCode);
        }).ConfigureAwait(false);
    }

    /// <summary>The Host-authoritative, always-fresh assessment plan 8.1/12.5 requires: Desktop
    /// never computes or caches whether a target is enabled -- every render of a stage-move row, and
    /// every confirmation dialog for one, reflects exactly this call's own result.</summary>
    public Task<StageTransitionAssessment> AssessMoveAsync(
        string? projectRoot, Guid sprintId, string targetStageId, CancellationToken cancellationToken) =>
        application.AssessStageTransitionAsync(projectRoot, sprintId, targetStageId, cancellationToken);

    /// <summary>Names source/target/direction, every satisfied/unsatisfied prerequisite, and -- for
    /// a rewind -- exactly what would be superseded (plan 8.4/12.5: consequences shown before
    /// confirming, never a generic prompt). Prerequisite message keys are rendered as the same raw
    /// machine text `forge sprint assess-stage` already prints (parity, plan 12.6): none of the
    /// `stage_transition.*` keys are registered as localized prose today, matching that CLI command's
    /// own established behavior exactly.</summary>
    public string MovePrompt(StageTransitionAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        List<string> lines =
        [
            $"{text.Resolve(MessageKeys.MoveToStageSourceLabel)} {assessment.SourceStageId ?? "-"}",
            $"{text.Resolve(MessageKeys.MoveToStageTargetLabel)} {assessment.TargetStageId}",
            $"{text.Resolve(MessageKeys.MoveToStageDirectionLabel)} {SurfaceFormatting.Machine(assessment.Direction)}",
        ];
        if (assessment.SatisfiedPrerequisites.Count > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.MoveToStageSatisfiedLabel)}: " +
                    $"{string.Join(", ", assessment.SatisfiedPrerequisites.Select(p => p.Id))}"));
        }

        if (assessment.UnsatisfiedPrerequisites.Count > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.MoveToStageUnsatisfiedLabel)}: " +
                    $"{string.Join(", ", assessment.UnsatisfiedPrerequisites.Select(p => $"{p.Id} ({p.MessageKey})"))}"));
        }

        if (assessment.Supersession is { } supersession)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.MoveToStageConsequencesLabel)}: " +
                    $"{supersession.StageIds.Count} {text.Resolve(MessageKeys.MoveToStageConsequencesStagesLabel)}, " +
                    $"{supersession.AttemptIds.Count} {text.Resolve(MessageKeys.MoveToStageConsequencesAttemptsLabel)}, " +
                    $"{supersession.FindingCount} {text.Resolve(MessageKeys.FindingsLabel)}, " +
                    $"{supersession.DecisionCount} decisions, " +
                    $"{supersession.ArtifactCount} {text.Resolve(MessageKeys.MoveToStageConsequencesArtifactsLabel)}"));
        }

        if (!assessment.Allowed)
        {
            lines.Add(text.Resolve(MessageKeys.MoveToStageBlockedCannotProceed));
        }

        return string.Join('\n', lines);
    }

    /// <summary>Commits using exactly the assessment just re-fetched for this confirmation (never an
    /// earlier render's copy) -- the Host still recomputes and rejects a mismatch itself (ADR 0046),
    /// but Desktop's own token/version always comes from the freshest read available, satisfying plan
    /// 12.5's "re-fetch/re-validate before committing" independently of that server-side guarantee.
    /// A fresh idempotency key is minted per call, matching `forge sprint move-stage`'s own
    /// behavior exactly (parity).</summary>
    public async Task<string> MoveAsync(
        string? projectRoot,
        Guid sprintId,
        StageTransitionAssessment assessment,
        string? reason,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        IForgeMutations mutations = await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false);
        return await UseMutationsAsync(mutations, async () =>
        {
            MoveStageResult result = await mutations
                .MoveSprintToStageAsync(
                    projectRoot, sprintId, assessment.TargetStageId!, assessment.ExpectedStateVersion,
                    assessment.AssessmentToken, reason, confirmed, Guid.NewGuid(), cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result.Succeeded ? MessageKeys.SprintStageMoved : MessageKeys.SprintManageFailed),
                result.DiagnosticCode);
        }).ConfigureAwait(false);
    }

    private static async Task<T> UseMutationsAsync<T>(IForgeMutations mutations, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (mutations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Message(string message, string diagnosticCode) =>
        diagnosticCode == DiagnosticCodes.None
            ? message
            : string.Create(CultureInfo.InvariantCulture, $"{message} ({diagnosticCode})");
}
