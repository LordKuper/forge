using System.ComponentModel;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

/// <summary>ADR 0069: how much one sprint has changed so far, as three totals over a fresh git diff
/// of its integration branch against its own frozen base commit. Deliberately not
/// <see cref="DiffPayload"/>: this travels inside a bounded sidebar/header row, and that type's
/// per-file list (up to <see cref="GitWorktreeManagerDiffStatBudget.MaxFiles"/> entries, per active
/// sprint, per project) is weight no reader of this row needs -- a surface that wants the file list
/// reads it for one sprint on its own. <see cref="Insertions"/>/<see cref="Deletions"/> are totals
/// over every changed file, including any the underlying per-file list elided, so a small number here
/// always means a small change (ADR 0059's honest-totals rule).</summary>
public sealed record SprintDiffStat(int FilesChanged, int Insertions, int Deletions);

/// <summary>One active (non-terminal) sprint's bounded contribution to a project's workspace-summary
/// row (plan section 6.2). <see cref="CurrentStageId"/>/<see cref="StagesCompleted"/>/
/// <see cref="StagesTotal"/> are derived from the same folded node state a full snapshot already
/// computes -- never a fresh timeline or per-node detail load. <see cref="Title"/> carries the
/// sprint's own frozen <see cref="SprintDefinition.Title"/> verbatim, including its
/// <see langword="null"/> (ADR 0057) -- a presentation fallback is never baked into this read
/// model.</summary>
/// <remarks>
/// ADR 0069 adds two live projections. Neither is a frozen sprint-creation value: ADR 0014 governs
/// what <see cref="SprintDefinition"/> freezes, and both of these answer "what is true right now",
/// like <see cref="State"/> and <see cref="StagesCompleted"/> already do.
/// <list type="bullet">
/// <item><see cref="FirstAttemptStartedAt"/> -- the anchor an elapsed-time display measures from
/// (<see cref="SprintJournalEntry.FirstAttemptStartedAt"/>), <see langword="null"/> for a sprint that
/// has never started an attempt. A timestamp, never a duration: the reader owns the clock, so this
/// read model needs none and stays a pure projection over durable data.</item>
/// <item><see cref="DiffStat"/> -- <see langword="null"/> both when the sprint has no integration
/// worktree yet and when the git read failed. Those two are reported identically on purpose: a header
/// can only say "not available" for either, and substituting zeros would assert that a sprint changed
/// nothing. Also <see langword="null"/> whenever the caller did not ask for it: this is the one member
/// of this row that costs `git` processes to compute, so it is opt-in per query
/// (<see cref="WorkspaceSummaryProjector.CreateAsync"/>'s <c>includeDiffStats</c>) rather than paid
/// for by every reader of the row.</item>
/// </list>
/// </remarks>
public sealed record SprintWorkspaceSummary(
    Guid SprintId,
    int CreationSequence,
    SprintState State,
    string? CurrentStageId,
    int StagesCompleted,
    int StagesTotal,
    bool AttentionRequired,
    bool HasActiveOperation,
    string? ActiveOperationNodeId,
    Guid? ActiveOperationAttemptId,
    string? Title,
    DateTimeOffset? FirstAttemptStartedAt,
    SprintDiffStat? DiffStat);

/// <summary>
/// Plan section 6.2's bounded, per-project workspace-summary row: project availability,
/// active-sprint summaries, attention reasons, current stage, progress, active operation, and
/// provider health -- without loading any sprint's full timeline. Catalog-agnostic by design (a
/// project's Host has no notion of the local catalog; see <see cref="ProjectCatalogStore"/>) so the
/// same row is what a Host would answer for its own project over <c>workspace.summary</c>. The CLI's
/// own `forge workspace summary` command pairs this with each <see cref="ProjectCatalogEntry"/> to
/// add the local alias/last-route context the catalog alone knows.
/// </summary>
public sealed record ProjectWorkspaceSummary(
    string SchemaVersion,
    Guid? ProjectId,
    string Root,
    bool Available,
    bool Initialized,
    StartupState StartupState,
    IReadOnlyList<SprintWorkspaceSummary> ActiveSprints,
    IReadOnlyList<Guid> AttentionSprintIds,
    IReadOnlyList<ProviderHealthEntry> Providers,
    string DiagnosticCode)
{
    /// <summary>`1.1.0` adds <see cref="SprintWorkspaceSummary.Title"/> (ADR 0057) -- additive and
    /// nullable, so an older reader that ignores it still sees a valid row. `1.2.0` adds
    /// <see cref="SprintWorkspaceSummary.FirstAttemptStartedAt"/> and
    /// <see cref="SprintWorkspaceSummary.DiffStat"/> (ADR 0069) on the same terms.</summary>
    public const string ContractVersion = "1.2.0";
}

/// <summary>A pure read derivation of "does this sprint have an exact live active operation right
/// now" -- deliberately mirrors, rather than shares code with,
/// <see cref="StopOperationCoordinator.RequestStopAsync"/>'s own inline check: that method's check is
/// tightly coupled to its own per-branch diagnostic codes, and extracting a shared helper there would
/// risk destabilizing an already-reviewed saga for a read-only projection's convenience. Never a
/// second source of truth for what "active" means -- both read the same three durable facts (sprint
/// running, node running with a current attempt, that attempt non-terminal and not already
/// stop-requested).</summary>
internal static class ActiveOperationLookup
{
    public static AttemptSnapshot? FindActive(SprintWorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Sprint.State != SprintState.Running)
        {
            return null;
        }

        foreach (NodeSnapshot node in state.Nodes.Values)
        {
            if (node.State == NodeState.Running &&
                node.CurrentAttemptId is { } attemptKey &&
                state.Attempts.TryGetValue(attemptKey, out AttemptSnapshot? attempt) &&
                !WorkflowStateMachines.IsTerminal(attempt.State) &&
                attempt.StopRequestedAt is null)
            {
                return attempt;
            }
        }

        return null;
    }
}

/// <summary>Builds <see cref="ProjectWorkspaceSummary"/> for one project root -- the bounded query
/// behind both the reserved `workspace.summary` capability and the CLI's own catalog-fanned-out
/// `forge workspace summary`. Reuses <see cref="StartupPipeline"/> for readiness/provider health
/// exactly like <see cref="StatusAdvisor"/> does, but folds only non-terminal sprints (never a
/// sprint's findings, handoffs, or routing detail) so this never approaches the cost of a full
/// per-sprint <see cref="SprintDetails"/> load, however many sprints a project has accumulated.
/// </summary>
public sealed class WorkspaceSummaryProjector(
    StartupPipeline pipeline,
    ISprintStore store,
    IConfigurationRegistry registry,
    ProviderCatalog providerCatalog,
    SprintGitIsolation gitIsolation)
{
    /// <summary>Builds one project's summary row. <paramref name="includeDiffStats"/> is the only
    /// cost-bearing knob here (PR #126 review finding 2): every other member of the row is folded
    /// from data this call already loads, while <see cref="SprintWorkspaceSummary.DiffStat"/> spawns
    /// up to three `git` processes per active sprint. Left <see langword="false"/>, the row is exactly
    /// as cheap as it was before ADR 0069 and <see cref="SprintWorkspaceSummary.DiffStat"/> is
    /// <see langword="null"/> throughout; a caller that actually renders the value asks for it.</summary>
    public async Task<ProjectWorkspaceSummary> CreateAsync(
        string? projectRoot, bool includeDiffStats, CancellationToken cancellationToken)
    {
        (StartupStatus startup, ProviderToolchainStatus providers) =
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderHealthEntry> providerHealth = ProviderHealthProjector.Project(providers, providerCatalog);
        if (!startup.Project.Initialized)
        {
            return new(
                ProjectWorkspaceSummary.ContractVersion,
                null,
                startup.Project.Root,
                startup.Project.Exists,
                false,
                startup.State,
                [],
                [],
                providerHealth,
                startup.Project.DiagnosticCode);
        }

        try
        {
            Guid projectId = await ProjectIdentity
                .ReadProjectIdAsync(startup.Project.Root, registry, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<SprintJournalEntry> journal =
                await SprintJournal.LoadAllAsync(store, startup.Project.Root, cancellationToken).ConfigureAwait(false);
            List<SprintWorkspaceSummary> active = [];
            List<Guid> attention = [];
            for (int index = 0; index < journal.Count; index++)
            {
                SprintJournalEntry entry = journal[index];
                SprintWorkflowState state = entry.Fold();
                // Bounded: terminal sprints (completed/cancelled) never enter the sidebar's active
                // list at all (plan section 4.1's own "completed and cancelled sprints... behind a
                // project history entry"), so this never folds more than the project's currently
                // live work.
                if (WorkflowStateMachines.IsTerminal(state.Sprint.State))
                {
                    continue;
                }

                SprintDefinition? definition = await store
                    .LoadDefinitionAsync(startup.Project.Root, entry.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (definition is null)
                {
                    continue;
                }

                bool needsAttention = state.Sprint.State is
                    SprintState.AwaitingHuman or SprintState.Blocked or SprintState.Failed or
                    SprintState.ReadyToFinalize;
                if (needsAttention)
                {
                    attention.Add(entry.Id.Value);
                }

                AttemptSnapshot? activeOperation = ActiveOperationLookup.FindActive(state);
                active.Add(new(
                    entry.Id.Value,
                    index + 1,
                    state.Sprint.State,
                    StageTransitionAssessor.ResolveCurrentStageId(definition, state),
                    state.Nodes.Values.Count(node => node.State is NodeState.Succeeded or NodeState.Skipped),
                    definition.Graph.Count,
                    needsAttention,
                    activeOperation is not null,
                    activeOperation?.NodeId,
                    activeOperation?.Id.Value,
                    definition.Title,
                    entry.FirstAttemptStartedAt,
                    includeDiffStats
                        ? await ReadDiffStatAsync(
                            startup.Project.Root, projectId, entry.Id, definition.BaseCommit, cancellationToken)
                            .ConfigureAwait(false)
                        : null));
            }

            return new(
                ProjectWorkspaceSummary.ContractVersion,
                projectId,
                startup.Project.Root,
                true,
                true,
                startup.State,
                active,
                attention,
                providerHealth,
                DiagnosticCodes.None);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            // One unreadable sprint file or manifest must never fail the whole workspace-summary
            // fan-out across every cataloged project (the same "omit rather than guess" posture
            // ADR 0005 already applies to diagnostic-bundle collection) -- report this project as
            // unavailable and let every other catalog entry's own row stay unaffected.
            return new(
                ProjectWorkspaceSummary.ContractVersion,
                null,
                startup.Project.Root,
                false,
                true,
                startup.State,
                [],
                [],
                providerHealth,
                DiagnosticCodes.InternalError);
        }
    }

    /// <summary>
    /// ADR 0069 (Q9): the sprint's own working diff, read fresh on every projection rather than
    /// persisted or cached. Nothing durable could answer this question correctly -- the number changes
    /// on every integration, and no aggregation over the per-attempt <c>AttemptDiffRecorded</c>
    /// payloads (ADR 0059) can avoid double-counting a file two attempts both touched -- so the branch
    /// git already maintains is the only honest source, and it is cheap enough to re-read.
    /// </summary>
    /// <remarks>
    /// Reached only when the caller asked for it (<c>includeDiffStats</c>), because this is the one
    /// read on this path that spawns processes and it fans out over `projects x active sprints` for a
    /// caller that iterates a catalog. Once asked for, it is still bounded per sprint: only
    /// non-terminal sprints reach here, and a sprint that has never run has no integration worktree
    /// directory, which <see cref="SprintGitIsolation.ReadIntegrationDiffStatAsync"/> answers without
    /// starting a `git` process at all. A sprint that has run costs three short `git` reads.
    ///
    /// Never throws and never reports a diagnostic: every failure mode -- no worktree yet, a worktree
    /// deleted out from under us, an unresolvable base commit, a `git` failure -- collapses to
    /// <see langword="null"/>, the same "one unreadable input never fails the whole fan-out" posture
    /// <see cref="CreateAsync"/>'s own catch block applies to the project row. The catch below is what
    /// makes that true for the shapes a result code cannot carry (PR #126 review finding 1): a `git`
    /// that cannot be launched at all throws <see cref="Win32Exception"/> out of
    /// <c>Process.Start</c>, which neither <c>SprintGitIsolation</c> nor <see cref="CreateAsync"/>'s
    /// own filter catches, so before this guard one machine without `git` on `PATH` took down the
    /// whole Desktop sidebar rather than blanking one optional field. The filter mirrors
    /// <c>ImplementationExecutionHostedService.TryReadDiffStatAsync</c>'s, which was added for the same
    /// shape (PR #116 finding 1), minus its <c>JsonException</c>: nothing on this path reads JSON.
    /// Only a cancellation of this method's own <paramref name="cancellationToken"/> is a real
    /// shutdown and is rethrown; any other <see cref="OperationCanceledException"/> is one optional
    /// read giving up.
    /// </remarks>
    private async Task<SprintDiffStat?> ReadDiffStatAsync(
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        try
        {
            GitDiffStatResult result = await gitIsolation
                .ReadIntegrationDiffStatAsync(projectRoot, projectId, sprintId, baseCommit, cancellationToken)
                .ConfigureAwait(false);
            return result.Stat is { } stat ? new(stat.FilesChanged, stat.Insertions, stat.Deletions) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or Win32Exception
            or OperationCanceledException)
        {
            return null;
        }
    }
}
