using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Configuration;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// Plan section 8.1's read-only `AssessStageTransition(sprintId, targetStageId)` query, over the
/// frozen DAG (<see cref="SprintDefinition.Graph"/>) and current stage revision. Never mutates
/// anything; <see cref="Forge.Application.StageTransitionCoordinator"/> is the only caller allowed
/// to act on its result, and it always recomputes a fresh assessment immediately before committing
/// rather than trusting a caller-supplied one (plan section 8.5).
///
/// ADR 0046: every prerequisite category reuses a fact the codebase already records elsewhere
/// (<see cref="SprintScheduler.IsTestWorkEligibleAsync"/>, <see cref="ModelPolicyGate"/>,
/// <see cref="RoutingLedger"/>, <see cref="IWorktreeManager.IsDirtyAsync"/>) -- this type never
/// recomputes policy that already lives in one of those places, only reads and reports it.
/// </summary>
public sealed class StageTransitionAssessor(
    ISprintStore store,
    SprintScheduler scheduler,
    IConfigurationRegistry registry,
    ScopedConfigurationService configuration,
    IWorktreeManager worktrees,
    IEnvironmentPaths paths,
    IClock clock)
{
    private readonly RoutingLedger routingLedger = new(store, clock);

    public async Task<StageTransitionAssessment> AssessAsync(
        string projectRoot,
        SprintId sprintId,
        string targetStageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStageId);
        SprintWorkflowState? state = await store.LoadAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            return StageTransitionAssessment.NotFound(sprintId, DiagnosticCodes.SprintNotFound);
        }

        SprintDefinition? definition = await store.LoadDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return StageTransitionAssessment.NotFound(sprintId, DiagnosticCodes.SprintNotFound);
        }

        Dictionary<string, NodeDefinition> byId = definition.Graph.ToDictionary(
            node => node.Id, node => node, StringComparer.Ordinal);
        if (!byId.TryGetValue(targetStageId, out NodeDefinition? target))
        {
            return StageTransitionAssessment.NotFound(sprintId, DiagnosticCodes.NodeNotFound);
        }

        string? currentStageId = ResolveCurrentStageId(definition, state);
        NodeState targetState = state.Nodes.TryGetValue(targetStageId, out NodeSnapshot? targetSnapshot)
            ? targetSnapshot.State
            : NodeState.Pending;
        StageTransitionDirection direction = targetStageId == currentStageId
            ? StageTransitionDirection.Same
            : targetState is NodeState.Succeeded or NodeState.Failed or NodeState.Skipped or NodeState.Cancelled
                ? StageTransitionDirection.Rewind
                : StageTransitionDirection.Advance;

        Guid projectId = await ProjectIdentity.ReadProjectIdAsync(projectRoot, registry, cancellationToken)
            .ConfigureAwait(false);
        // LastSequence -- the sprint's whole journal position, not merely the sprint aggregate's own
        // transition count -- is what "expected state version" means here: a node or attempt
        // transition (e.g. a predecessor completing) never bumps SprintSnapshot.Version on its own,
        // but it must still be caught as staleness, since it can flip a prerequisite this exact
        // assessment already evaluated.
        long expectedStateVersion = state.LastSequence;
        string? assessmentToken = ComputeToken(
            projectId, sprintId, targetStageId, state.Sprint.Revision, expectedStateVersion);

        bool terminalSprint = WorkflowStateMachines.IsTerminal(state.Sprint.State);
        ActiveOperationImpact activeOperation = ResolveActiveOperation(state, direction);

        if (direction == StageTransitionDirection.Same || terminalSprint)
        {
            return new(
                true,
                terminalSprint ? DiagnosticCodes.SprintTransitionInvalid : DiagnosticCodes.None,
                sprintId,
                currentStageId,
                targetStageId,
                direction,
                false,
                [],
                [],
                activeOperation,
                null,
                direction == StageTransitionDirection.Rewind,
                expectedStateVersion,
                state.Sprint.Revision,
                assessmentToken);
        }

        HashSet<string> requiredPredecessors = CollectTransitivePredecessors(targetStageId, byId, includeOptional: false);
        List<StagePrerequisite> satisfied = [];
        List<StagePrerequisite> unsatisfied = [];
        void Add(string id, bool ok, string messageKey, IReadOnlyDictionary<string, string?>? arguments = null)
        {
            StagePrerequisite prerequisite = new(id, ok, messageKey, arguments ?? EmptyArguments);
            (ok ? satisfied : unsatisfied).Add(prerequisite);
        }

        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        bool NodeSucceededWithLiveEvidence(string nodeId)
        {
            if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? snapshot))
            {
                return false;
            }

            if (snapshot.State == NodeState.Skipped)
            {
                return true;
            }

            return snapshot.State == NodeState.Succeeded &&
                results.Any(result => result.NodeId.Value == nodeId && result.Superseded is null &&
                    result.State == NodeOutcome.Succeeded);
        }

        if (direction == StageTransitionDirection.Advance)
        {
            foreach (string predecessor in requiredPredecessors)
            {
                Add(
                    StagePrerequisiteIds.PredecessorSuccess,
                    NodeSucceededWithLiveEvidence(predecessor),
                    "stage_transition.predecessor_success",
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["stage_id"] = predecessor });
            }

            foreach (string candidateId in requiredPredecessors.Append(targetStageId))
            {
                if (byId[candidateId].Role != NodeRole.TestWork)
                {
                    continue;
                }

                bool confirmed = await scheduler
                    .IsTestWorkEligibleAsync(projectRoot, sprintId, definition, byId[candidateId], cancellationToken)
                    .ConfigureAwait(false);
                Add(
                    StagePrerequisiteIds.ImplementationConfirmed,
                    confirmed,
                    "stage_transition.implementation_confirmed",
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["stage_id"] = candidateId });
            }

            foreach (string candidateId in requiredPredecessors.Append(targetStageId))
            {
                NodeDefinition candidate = byId[candidateId];
                if (candidate.Role == NodeRole.Review)
                {
                    string? testWorkDependency = candidate.DependsOn
                        .FirstOrDefault(dependency => byId.TryGetValue(dependency, out NodeDefinition? d) &&
                            d.Role == NodeRole.TestWork);
                    if (testWorkDependency is not null)
                    {
                        Add(
                            StagePrerequisiteIds.TestWorkRecorded,
                            NodeSucceededWithLiveEvidence(testWorkDependency),
                            "stage_transition.test_work_recorded",
                            new Dictionary<string, string?>(StringComparer.Ordinal) { ["stage_id"] = candidateId });
                    }
                }

                if (candidate.Role is NodeRole.HumanApproval or NodeRole.Finalization)
                {
                    string? reviewDependency = candidate.DependsOn
                        .FirstOrDefault(dependency => byId.TryGetValue(dependency, out NodeDefinition? d) &&
                            d.Role == NodeRole.Review);
                    if (reviewDependency is not null)
                    {
                        Add(
                            StagePrerequisiteIds.ReviewConverged,
                            NodeSucceededWithLiveEvidence(reviewDependency),
                            "stage_transition.review_converged",
                            new Dictionary<string, string?>(StringComparer.Ordinal) { ["stage_id"] = candidateId });
                    }
                }

                if (candidate.Role == NodeRole.Finalization)
                {
                    string? approvalDependency = candidate.DependsOn
                        .FirstOrDefault(dependency => byId.TryGetValue(dependency, out NodeDefinition? d) &&
                            d.Role == NodeRole.HumanApproval);
                    if (approvalDependency is not null)
                    {
                        Add(
                            StagePrerequisiteIds.HumanApproved,
                            NodeSucceededWithLiveEvidence(approvalDependency),
                            "stage_transition.human_approved",
                            new Dictionary<string, string?>(StringComparer.Ordinal) { ["stage_id"] = candidateId });
                    }
                }
            }

            IReadOnlyList<Handoff> handoffs =
                await store.GetHandoffsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            foreach (string predecessor in requiredPredecessors)
            {
                IReadOnlyList<Handoff> forNode = [.. handoffs.Where(handoff => handoff.NodeId.Value == predecessor)];
                bool handoffOk = forNode.Count == 0 || forNode.Any(handoff => handoff.Superseded is null);
                if (forNode.Count > 0)
                {
                    Add(
                        StagePrerequisiteIds.HandoffArtifacts,
                        handoffOk,
                        "stage_transition.handoff_artifacts",
                        new Dictionary<string, string?>(StringComparer.Ordinal) { ["stage_id"] = predecessor });
                }
            }
        }

        // Round 1 review of PR #96 (finding 3): these four prerequisites -- like `NoActiveOperation`
        // just below, already direction-scoped -- gate whether an advance target may become active;
        // none of them belongs on a rewind. A rewind is deliberately the escape hatch for exactly
        // these conditions (an open finding, a dirty integration worktree, an exhausted retry budget,
        // or a since-tightened model policy are all reasons to go *back*, not reasons to refuse doing
        // so), and a rewind's own supersession is what actually resolves the finding one of them
        // checks -- gating the rewind on it first would be circular. Plan section 8.2's prerequisite
        // list is written for activating an advance target; section 8.4's rewind list names only a
        // bounded reason, mandatory confirmation, and the mechanical stop/revision/supersession
        // machinery (see ADR 0048).
        if (direction == StageTransitionDirection.Advance)
        {
            IReadOnlyList<Finding> findings =
                await store.GetFindingsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            bool noBlockingFindings = findings.All(finding =>
                finding.Status != FindingStatus.Open || finding.Superseded is not null);
            Add(StagePrerequisiteIds.NoBlockingFindings, noBlockingFindings, "stage_transition.no_blocking_findings");

            bool policyOk = await IsProviderModelPolicySatisfiedAsync(projectRoot, definition, target, cancellationToken)
                .ConfigureAwait(false);
            Add(StagePrerequisiteIds.ProviderModelPolicy, policyOk, "stage_transition.provider_model_policy");

            bool gitOk = await IsGitIsolationSatisfiedAsync(projectRoot, projectId, sprintId, cancellationToken)
                .ConfigureAwait(false);
            Add(StagePrerequisiteIds.GitIsolation, gitOk, "stage_transition.git_isolation");

            bool retryOk = await IsRetryBudgetSatisfiedAsync(projectRoot, definition, target, cancellationToken)
                .ConfigureAwait(false);
            Add(StagePrerequisiteIds.RetryBudget, retryOk, "stage_transition.retry_budget");

            Add(
                StagePrerequisiteIds.NoActiveOperation,
                !activeOperation.HasActiveOperation,
                "stage_transition.no_active_operation");
        }

        StageSupersessionSummary? supersession = direction == StageTransitionDirection.Rewind
            ? await BuildSupersessionSummaryAsync(projectRoot, sprintId, targetStageId, byId, cancellationToken)
                .ConfigureAwait(false)
            : null;

        bool allowed = unsatisfied.Count == 0;
        return new(
            true,
            DiagnosticCodes.None,
            sprintId,
            currentStageId,
            targetStageId,
            direction,
            allowed,
            satisfied,
            unsatisfied,
            activeOperation,
            supersession,
            direction == StageTransitionDirection.Rewind,
            expectedStateVersion,
            state.Sprint.Revision,
            assessmentToken);
    }

    /// <summary>Recomputed identically by <c>StageTransitionCoordinator</c> immediately before a
    /// commit and compared against the caller-supplied value (plan section 8.5) -- bound to the
    /// project, sprint, target, current revision, and expected state version, so a client-held
    /// assessment goes stale the moment any of those move.</summary>
    private static string ComputeToken(
        Guid projectId, SprintId sprintId, string targetStageId, StageRevision revision, long stateVersion)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"stage_transition|{projectId:D}|{sprintId.Value:D}|{targetStageId}|{revision.Value}|{stateVersion}")));
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyArguments =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>The frontier node driving the sprint right now (an active Work attempt or a pending
    /// human gate), or -- once nothing is left running -- the furthest node that has already settled
    /// good, or the very first node when nothing has run at all. A heuristic over the frozen graph's
    /// declaration order, matching every other "no real ordering exists yet for a parallel DAG"
    /// simplification this codebase already accepts for its currently-linear built-in workflow.
    /// </summary>
    private static string? ResolveCurrentStageId(SprintDefinition definition, SprintWorkflowState state)
    {
        foreach (NodeDefinition node in definition.Graph)
        {
            if (state.Nodes.TryGetValue(node.Id, out NodeSnapshot? snapshot) &&
                snapshot.State is NodeState.Running or NodeState.AwaitingHuman)
            {
                return node.Id;
            }
        }

        string? lastSettled = null;
        foreach (NodeDefinition node in definition.Graph)
        {
            if (state.Nodes.TryGetValue(node.Id, out NodeSnapshot? snapshot) &&
                snapshot.State is NodeState.Succeeded or NodeState.Skipped)
            {
                lastSettled = node.Id;
            }
        }

        return lastSettled ?? (definition.Graph.Count > 0 ? definition.Graph[0].Id : null);
    }

    internal static HashSet<string> CollectTransitivePredecessors(
        string nodeId, IReadOnlyDictionary<string, NodeDefinition> byId, bool includeOptional)
    {
        HashSet<string> predecessors = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!byId.TryGetValue(id, out NodeDefinition? node))
            {
                return;
            }

            foreach (string dependency in node.DependsOn)
            {
                // Traversal always continues past an optional dependency, even when it is excluded
                // from the returned (required) set -- an optional node's own upstream predecessors
                // are still real prerequisites of whatever the optional node would otherwise gate:
                // skipping it does not also excuse whatever came before it.
                bool isOptional = byId.TryGetValue(dependency, out NodeDefinition? d) && d.Optional;
                if (includeOptional || !isOptional)
                {
                    predecessors.Add(dependency);
                }

                if (visited.Add(dependency))
                {
                    Visit(dependency);
                }
            }
        }

        Visit(nodeId);
        return predecessors;
    }

    /// <summary>Every node reachable *forward* from <paramref name="targetStageId"/> (inclusive) --
    /// the closure a rewind's own evidence supersession targets (plan section 8.4: "marks downstream
    /// results and artifacts"). The reverse of <see cref="CollectTransitivePredecessors"/>.</summary>
    internal static HashSet<string> CollectDownstreamClosure(
        string targetStageId, IReadOnlyList<NodeDefinition> graph)
    {
        Dictionary<string, List<string>> successors = new(StringComparer.Ordinal);
        foreach (NodeDefinition node in graph)
        {
            foreach (string dependency in node.DependsOn)
            {
                (successors.TryGetValue(dependency, out List<string>? list) ? list : successors[dependency] = [])
                    .Add(node.Id);
            }
        }

        HashSet<string> downstream = new(StringComparer.Ordinal) { targetStageId };
        Queue<string> queue = new();
        queue.Enqueue(targetStageId);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!successors.TryGetValue(current, out List<string>? children))
            {
                continue;
            }

            foreach (string child in children)
            {
                if (downstream.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return downstream;
    }

    private static ActiveOperationImpact ResolveActiveOperation(
        SprintWorkflowState state, StageTransitionDirection direction)
    {
        foreach (NodeSnapshot node in state.Nodes.Values)
        {
            if (node.State != NodeState.Running || node.CurrentAttemptId is not { } attemptId)
            {
                continue;
            }

            if (state.Attempts.TryGetValue(attemptId, out AttemptSnapshot? attempt) &&
                !WorkflowStateMachines.IsTerminal(attempt.State))
            {
                return new(
                    true, node.Id.Value, Guid.Parse(attemptId), direction == StageTransitionDirection.Rewind);
            }
        }

        return new(false, null, null, false);
    }

    private async Task<bool> IsProviderModelPolicySatisfiedAsync(
        string projectRoot, SprintDefinition definition, NodeDefinition target, CancellationToken cancellationToken)
    {
        ExecutionPhase? phase = ExecutionProfilePolicy.PhaseFor(target.Role);
        if (phase is not { } modelPhase || !definition.ExecutionProfiles.TryGetValue(modelPhase, out ExecutionProfile? profile))
        {
            return true;
        }

        IReadOnlyList<string> allowedModels = ModelPolicyGate.ParseAllowedModels(
            await configuration.GetProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false));
        return ModelPolicyGate.IsAllowed(profile.Provider, profile.Model, allowedModels);
    }

    /// <summary>Reuses <see cref="IWorktreeManager.IsDirtyAsync"/> against the sprint's own
    /// integration worktree rather than recomputing Git state -- satisfied (nothing to check yet)
    /// when the worktree has not been created, and fails open (satisfied) rather than throwing from
    /// a read-only advisory query if the probe itself cannot complete (e.g. a transient `git`
    /// failure); a real Git problem still surfaces the normal way once an attempt actually tries to
    /// use the worktree.</summary>
    private async Task<bool> IsGitIsolationSatisfiedAsync(
        string projectRoot, Guid projectId, SprintId sprintId, CancellationToken cancellationToken)
    {
        try
        {
            string integrationPath = WorktreeLayout.IntegrationPath(paths, projectId, sprintId);
            if (!await worktrees.ExistsAsync(projectRoot, integrationPath, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return !await worktrees.IsDirtyAsync(projectRoot, integrationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return true;
        }
    }

    private async Task<bool> IsRetryBudgetSatisfiedAsync(
        string projectRoot, SprintDefinition definition, NodeDefinition target, CancellationToken cancellationToken)
    {
        ExecutionPhase? phase = ExecutionProfilePolicy.PhaseFor(target.Role);
        if (phase is not { } modelPhase || !definition.ExecutionProfiles.TryGetValue(modelPhase, out ExecutionProfile? profile))
        {
            return true;
        }

        SprintId sprintId = definition.Id;
        DateTimeOffset? resumeNotBefore = await routingLedger
            .GetResumeNotBeforeAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (resumeNotBefore is { } until && clock.UtcNow < until)
        {
            return false;
        }

        RetryBudgetRecord budget = await routingLedger
            .GetRetryBudgetAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (budget.Remaining <= 0)
        {
            return false;
        }

        HealthKey key = new(profile.Provider, profile.Model, "batch");
        CircuitBreakerRecord? breaker = await routingLedger
            .GetCircuitBreakerAsync(projectRoot, sprintId, key, cancellationToken).ConfigureAwait(false);
        return breaker is not { State: CircuitState.Open, CooldownUntil: { } cooldown } || clock.UtcNow >= cooldown;
    }

    private async Task<StageSupersessionSummary> BuildSupersessionSummaryAsync(
        string projectRoot,
        SprintId sprintId,
        string targetStageId,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        CancellationToken cancellationToken)
    {
        HashSet<string> downstream = CollectDownstreamClosure(targetStageId, [.. byId.Values]);
        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Finding> findings =
            await store.GetFindingsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ConfirmationArtifact> confirmations =
            await store.GetConfirmationsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TestWorkArtifact> testWork =
            await store.GetTestWorkAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Handoff> handoffs =
            await store.GetHandoffsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);

        List<Guid> attemptIds = [.. results
            .Where(result => downstream.Contains(result.NodeId.Value) && result.Superseded is null)
            .Select(result => result.AttemptId.Value)];
        int findingCount = findings.Count(finding => finding.Superseded is null &&
            (finding.NodeId is null || downstream.Contains(finding.NodeId.Value)));
        int decisionCount =
            confirmations.Count(item => downstream.Contains(item.NodeId.Value) && item.Superseded is null) +
            testWork.Count(item => downstream.Contains(item.NodeId.Value) && item.Superseded is null);
        int artifactCount = handoffs.Count(item => downstream.Contains(item.NodeId.Value) && item.Superseded is null);

        return new([.. downstream], attemptIds, findingCount, decisionCount, artifactCount);
    }
}
