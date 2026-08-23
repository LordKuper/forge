using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

/// <summary>One active (non-terminal) sprint's bounded contribution to a project's workspace-summary
/// row (plan section 6.2). <see cref="CurrentStageId"/>/<see cref="StagesCompleted"/>/
/// <see cref="StagesTotal"/> are derived from the same folded node state a full snapshot already
/// computes -- never a fresh timeline or per-node detail load.</summary>
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
    Guid? ActiveOperationAttemptId);

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
    public const string ContractVersion = "1.0.0";
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
    ProviderCatalog providerCatalog)
{
    public async Task<ProjectWorkspaceSummary> CreateAsync(string? projectRoot, CancellationToken cancellationToken)
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
                    activeOperation?.Id.Value));
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
}
