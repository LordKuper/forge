using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Domain;

namespace Forge.Application;

public sealed record StartAttemptResult(bool Succeeded, AttemptId? AttemptId, string DiagnosticCode);

public sealed record CompleteAttemptResult(bool Succeeded, NodeSnapshot? Node, string DiagnosticCode);

public sealed record NodeActionResult(bool Succeeded, NodeSnapshot? Node, string DiagnosticCode);

public sealed record RecordFindingResult(bool Succeeded, Finding? Finding, string DiagnosticCode);

public sealed record RecordHandoffResult(bool Succeeded, Handoff? Handoff, string DiagnosticCode);

/// <summary>
/// Drives a sprint's frozen node graph: dependency-based readiness, bounded automatic retries,
/// human gates, findings, handoffs, node results, and the sprint-level completion gate. This is
/// the deterministic engine only — what a `Work` node's attempt actually does (invoke a provider,
/// touch a worktree) is not implemented here; that needs the isolated execution Stage 7 builds.
/// Every method here is safe to call against an abstracted or test executor today and a real one
/// later without changing this contract.
/// </summary>
/// <remarks>
/// ponytail: automatic retries are a fixed constant (<see cref="MaxAutomaticRetries"/>), not a
/// per-workflow policy. Add per-node/per-workflow retry budgets when a workflow actually needs one.
/// </remarks>
public sealed class SprintScheduler(ISprintStore store, IClock clock)
{
    public const int MaxAutomaticRetries = 2;

    public static Guid RetryNodeKey(SprintId sprintId, NodeSnapshot node) => NodeActionKey("retry_failed_node", sprintId, node);

    public static Guid ResolveHumanGateKey(SprintId sprintId, NodeSnapshot node) =>
        NodeActionKey("resolve_human_gate", sprintId, node);

    /// <summary>Registers every node in the graph as `pending`, then promotes what is already runnable.</summary>
    public async Task InitializeGraphAsync(
        string projectRoot,
        SprintId sprintId,
        IReadOnlyList<NodeDefinition> graph,
        CancellationToken cancellationToken)
    {
        foreach (NodeDefinition node in graph)
        {
            await store.AppendTransitionAsync(
                projectRoot,
                sprintId,
                AggregateKind.Node,
                node.Id,
                "NodeChanged",
                "workflow.node_created",
                WorkflowStateNames.ToSnakeCase(WorkflowStateMachines.NodeInitial),
                0,
                Guid.NewGuid(),
                cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.AttemptNumberArgument] = "0" })
                .ConfigureAwait(false);
        }

        await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes every `pending` node whose dependencies are all `succeeded`/`skipped` to `ready`,
    /// then pushes any `ready` human gate straight to `awaiting_human` if the sprint is running.
    /// Safe to call repeatedly; a node with unmet dependencies is left untouched.
    /// </summary>
    public async Task<SprintWorkflowState> AdvanceGraphAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, NodeDefinition> byId = definition.Graph.ToDictionary(
            node => node.Id,
            node => node,
            StringComparer.Ordinal);

        foreach (NodeDefinition node in definition.Graph)
        {
            if (!state.Nodes.TryGetValue(node.Id, out NodeSnapshot? snapshot) || snapshot.State != NodeState.Pending)
            {
                continue;
            }

            bool satisfied = node.DependsOn.All(dependency =>
                state.Nodes.TryGetValue(dependency, out NodeSnapshot? upstream) &&
                upstream.State is NodeState.Succeeded or NodeState.Skipped);
            if (!satisfied)
            {
                continue;
            }

            await AppendNodeAsync(
                projectRoot, sprintId, node.Id, "workflow.node_ready", NodeState.Ready, snapshot.Version,
                cancellationToken).ConfigureAwait(false);
        }

        state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (state.Sprint.State != SprintState.Running)
        {
            return state;
        }

        foreach (NodeDefinition node in definition.Graph.Where(node => byId[node.Id].Kind == NodeKind.HumanGate))
        {
            if (!state.Nodes.TryGetValue(node.Id, out NodeSnapshot? snapshot) || snapshot.State != NodeState.Ready)
            {
                continue;
            }

            long version = await AppendNodeAsync(
                projectRoot, sprintId, node.Id, "workflow.node_running", NodeState.Running, snapshot.Version,
                cancellationToken).ConfigureAwait(false);
            await AppendNodeAsync(
                projectRoot, sprintId, node.Id, "workflow.node_awaiting_human", NodeState.AwaitingHuman, version,
                cancellationToken).ConfigureAwait(false);
        }

        return await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StartAttemptResult> StartAttemptAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        long expectedNodeVersion,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Sprint.State != SprintState.Running)
        {
            return new(false, null, DiagnosticCodes.SprintNotRunning);
        }

        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        NodeDefinition? definedNode = definition.Graph.FirstOrDefault(item => item.Id == nodeId);
        if (definedNode is null || definedNode.Kind != NodeKind.Work)
        {
            return new(false, null, DiagnosticCodes.NodeKindMismatch);
        }

        if (node.Version != expectedNodeVersion || node.State != NodeState.Ready)
        {
            return new(false, null, DiagnosticCodes.WorkflowEventConflict);
        }

        int attemptNumber = node.AttemptCount + 1;
        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_running",
            WorkflowStateNames.ToSnakeCase(NodeState.Running), expectedNodeVersion, Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkflowEvent.AttemptNumberArgument] = attemptNumber.ToString(CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);

        AttemptId attemptId = AttemptId.New();
        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_created", WorkflowStateNames.ToSnakeCase(WorkflowStateMachines.AttemptInitial), 0,
            Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        return new(true, attemptId, DiagnosticCodes.None);
    }

    /// <summary>
    /// Reports an attempt's conclusion, walking it through the remaining attempt states, settling
    /// the owning node, writing its <see cref="NodeResult"/>, applying the bounded auto-retry
    /// policy on failure, and re-evaluating the graph and the sprint's completion gate.
    /// </summary>
    public async Task<CompleteAttemptResult> CompleteAttemptAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        AttemptId attemptId,
        bool succeeded,
        string inputDigest,
        IReadOnlyList<string>? outputs,
        IReadOnlyList<NodeDiagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node) || node.State != NodeState.Running)
        {
            return new(false, null, DiagnosticCodes.NodeTransitionInvalid);
        }

        if (!state.Attempts.TryGetValue(attemptId.Value.ToString("D"), out AttemptSnapshot? attempt) ||
            attempt.State != AttemptState.Created)
        {
            return new(false, null, DiagnosticCodes.WorkflowEventConflict);
        }

        DateTimeOffset startedAt = attempt.UpdatedAt;
        NodeResult result = new(
            sprintId,
            new(nodeId),
            attemptId,
            succeeded ? NodeOutcome.Succeeded : NodeOutcome.Failed,
            startedAt,
            clock.UtcNow,
            inputDigest,
            outputs ?? [],
            diagnostics ?? []);
        // Validated *before* anything below becomes durable: a malformed result (e.g. a caller
        // passing a garbled digest) must never leave the node succeeded/failed with no result to
        // show for it, wedging the sprint with an unrecoverable state and a record that was never
        // written.
        try
        {
            WorkflowRecordCodec.ValidateNodeResult(result);
        }
        catch (InvalidDataException)
        {
            return new(false, node, DiagnosticCodes.WorkflowRecordInvalid);
        }

        long attemptVersion = await WalkAttemptAsync(
            projectRoot, sprintId, attemptId, attempt.Version, AttemptState.Preparing, cancellationToken)
            .ConfigureAwait(false);
        attemptVersion = await WalkAttemptAsync(
            projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Running, cancellationToken)
            .ConfigureAwait(false);
        attemptVersion = await WalkAttemptAsync(
            projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Validating, cancellationToken)
            .ConfigureAwait(false);
        await WalkAttemptAsync(
            projectRoot, sprintId, attemptId, attemptVersion,
            succeeded ? AttemptState.Succeeded : AttemptState.Failed, cancellationToken).ConfigureAwait(false);

        NodeState nodeOutcome = succeeded ? NodeState.Succeeded : NodeState.Failed;
        long nodeVersion = await AppendNodeAsync(
            projectRoot, sprintId, nodeId, succeeded ? "workflow.node_succeeded" : "workflow.node_failed",
            nodeOutcome, node.Version, cancellationToken).ConfigureAwait(false);

        await store.SaveNodeResultAsync(projectRoot, result, cancellationToken).ConfigureAwait(false);

        if (!succeeded && node.AttemptCount < MaxAutomaticRetries + 1)
        {
            nodeVersion = await AppendNodeAsync(
                projectRoot, sprintId, nodeId, "workflow.node_retrying", NodeState.Ready, nodeVersion,
                cancellationToken).ConfigureAwait(false);
        }

        await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Nodes[nodeId], DiagnosticCodes.None);
    }

    /// <summary>Manually re-arms a node whose automatic retries were exhausted (matches `RetryNode`).</summary>
    public async Task<NodeActionResult> RetryNodeAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        long expectedNodeVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        if (node.Version != expectedNodeVersion || idempotencyKey != RetryNodeKey(sprintId, node))
        {
            return new(false, node, DiagnosticCodes.SuggestionStale);
        }

        if (node.State != NodeState.Failed)
        {
            return new(false, node, DiagnosticCodes.NodeTransitionInvalid);
        }

        // The caller's own validated key drives this append directly (not a fresh internal GUID):
        // a single-step verb like this one can be made genuinely idempotent for free, so a client
        // retry after a lost response replays instead of failing the version check it would
        // otherwise hit on the second, no-op-intended call.
        AppendOutcome outcome = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_retried",
            WorkflowStateNames.ToSnakeCase(NodeState.Ready), expectedNodeVersion, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return new(false, node, outcome.DiagnosticCode);
        }

        if (!outcome.Replayed)
        {
            // A human gate re-armed to `ready` must be re-promoted to `awaiting_human`; a retried
            // work node just becomes startable again. Either way nothing else advances on its own.
            await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        }

        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Nodes[nodeId], DiagnosticCodes.None);
    }

    /// <summary>Approves or rejects a human gate (matches the `workflow.review` / `ResolveHumanGate` capability).</summary>
    public async Task<NodeActionResult> ResolveHumanGateAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        bool approved,
        long expectedNodeVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        if (node.Version != expectedNodeVersion || idempotencyKey != ResolveHumanGateKey(sprintId, node))
        {
            return new(false, node, DiagnosticCodes.SuggestionStale);
        }

        if (node.State != NodeState.AwaitingHuman)
        {
            return new(false, node, DiagnosticCodes.NodeTransitionInvalid);
        }

        AttemptId attemptId = AttemptId.New();
        DateTimeOffset startedAt = clock.UtcNow;
        // Deliberately NOT the caller's own idempotency key: this is a multi-step sequence (up to
        // six appends), and forwarding the real key to only the first step was tried and reverted
        // — it made a crash between that first append and the rest of the sequence into a
        // permanent wedge (the ledger hit on retry returned `Succeeded=true` with the gate still
        // stuck at `awaiting_human` forever, since nothing re-drove the remaining steps). A fresh
        // GUID per call means a retry after a partial crash simply starts a new attempt and walks
        // it to completion instead — the interrupted one is left behind, inert and harmless (its
        // node was never reached), but the gate always reaches a real terminal state. True
        // idempotent replay for a compound operation like this needs a resumable walk keyed by a
        // deterministic attempt id, not a single boolean short-circuit; that redesign is deferred.
        long attemptVersion = await WalkAttemptAsync(
            projectRoot, sprintId, attemptId, 0, AttemptState.Created, cancellationToken).ConfigureAwait(false);
        attemptVersion = await WalkAttemptAsync(
            projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Preparing, cancellationToken)
            .ConfigureAwait(false);

        NodeOutcome outcome;
        if (approved)
        {
            attemptVersion = await WalkAttemptAsync(
                projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Running, cancellationToken)
                .ConfigureAwait(false);
            attemptVersion = await WalkAttemptAsync(
                projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Validating, cancellationToken)
                .ConfigureAwait(false);
            await WalkAttemptAsync(
                projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Succeeded, cancellationToken)
                .ConfigureAwait(false);
            long nodeVersion = await AppendNodeAsync(
                projectRoot, sprintId, nodeId, "workflow.node_running", NodeState.Running, expectedNodeVersion,
                cancellationToken).ConfigureAwait(false);
            await AppendNodeAsync(
                projectRoot, sprintId, nodeId, "workflow.node_succeeded", NodeState.Succeeded, nodeVersion,
                cancellationToken).ConfigureAwait(false);
            outcome = NodeOutcome.Succeeded;
        }
        else
        {
            await WalkAttemptAsync(
                projectRoot, sprintId, attemptId, attemptVersion, AttemptState.Failed, cancellationToken)
                .ConfigureAwait(false);
            await AppendNodeAsync(
                projectRoot, sprintId, nodeId, "workflow.node_rejected", NodeState.Failed, expectedNodeVersion,
                cancellationToken).ConfigureAwait(false);
            outcome = NodeOutcome.Failed;
        }

        await store.SaveNodeResultAsync(
            projectRoot,
            new(sprintId, new(nodeId), attemptId, outcome, startedAt, clock.UtcNow, "sha256:" + new string('0', 64), [], []),
            cancellationToken).ConfigureAwait(false);

        await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Nodes[nodeId], DiagnosticCodes.None);
    }

    public async Task<NodeActionResult> SkipNodeAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        long expectedNodeVersion,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        if (node.Version != expectedNodeVersion || node.State is not (NodeState.Pending or NodeState.Ready))
        {
            return new(false, node, DiagnosticCodes.NodeTransitionInvalid);
        }

        await AppendNodeAsync(
            projectRoot, sprintId, nodeId, "workflow.node_skipped", NodeState.Skipped, expectedNodeVersion,
            cancellationToken).ConfigureAwait(false);
        await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Nodes[nodeId], DiagnosticCodes.None);
    }

    /// <summary>
    /// Every node in `succeeded`/`skipped` promotes the sprint to `ready_to_finalize`; any node
    /// stuck at `failed` with no automatic retries left blocks it. A no-op while other nodes still
    /// have in-flight work, and a no-op if the sprint is not currently `running` (an internal,
    /// best-effort check: a lost race just waits for the next call that observes the settled state).
    /// </summary>
    public async Task EvaluateCompletionAsync(string projectRoot, SprintId sprintId, CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Sprint.State != SprintState.Running || state.Nodes.Count == 0)
        {
            return;
        }

        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, NodeKind> kindById = definition.Graph.ToDictionary(
            node => node.Id,
            node => node.Kind,
            StringComparer.Ordinal);

        bool allSettledGood = state.Nodes.Values.All(node => node.State is NodeState.Succeeded or NodeState.Skipped);
        // A human gate never auto-retries (a rejection is a final human decision, not a transient
        // failure), so any `failed` gate is stuck the moment it is rejected — it has no attempt
        // budget to exhaust the way a work node does.
        bool anyStuck = state.Nodes.Any(entry => entry.Value.State == NodeState.Cancelled ||
            (entry.Value.State == NodeState.Failed &&
                (kindById[entry.Key] == NodeKind.HumanGate || entry.Value.AttemptCount >= MaxAutomaticRetries + 1)));
        if (allSettledGood)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_ready_to_finalize", WorkflowStateNames.ToSnakeCase(SprintState.ReadyToFinalize),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        }
        else if (anyStuck)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_blocked", WorkflowStateNames.ToSnakeCase(SprintState.Blocked),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RecordFindingResult> RecordFindingAsync(
        string projectRoot,
        SprintId sprintId,
        FindingSeverity severity,
        string messageKey,
        IReadOnlyDictionary<string, string?> arguments,
        IReadOnlyList<string> evidence,
        FindingLocation? location,
        CancellationToken cancellationToken)
    {
        Finding finding = new(
            Guid.NewGuid(),
            sprintId,
            Fingerprint(sprintId, messageKey, evidence),
            severity,
            FindingStatus.Open,
            messageKey,
            arguments,
            evidence,
            location);
        try
        {
            await store.SaveFindingAsync(projectRoot, finding, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new(false, null, DiagnosticCodes.WorkflowRecordInvalid);
        }

        return new(true, finding, DiagnosticCodes.None);
    }

    public async Task<RecordFindingResult> ResolveFindingAsync(
        string projectRoot,
        SprintId sprintId,
        Guid findingId,
        FindingStatus status,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Finding> findings = await store.GetFindingsAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        Finding? existing = findings.FirstOrDefault(item => item.FindingId == findingId);
        if (existing is null)
        {
            return new(false, null, DiagnosticCodes.FindingNotFound);
        }

        Finding updated = existing with { Status = status };
        await store.SaveFindingAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
        return new(true, updated, DiagnosticCodes.None);
    }

    public Task<IReadOnlyList<Finding>> GetFindingsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        store.GetFindingsAsync(projectRoot, sprintId, cancellationToken);

    public async Task<RecordHandoffResult> RecordHandoffAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        string baseSha,
        string summary,
        IReadOnlyList<string> decisions,
        IReadOnlyList<string> openRisks,
        IReadOnlyList<string>? nextNodeIds,
        CancellationToken cancellationToken)
    {
        Handoff handoff = new(
            Guid.NewGuid(), sprintId, new(nodeId), baseSha, summary, decisions, [], openRisks, nextNodeIds);
        try
        {
            await store.SaveHandoffAsync(projectRoot, handoff, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new(false, null, DiagnosticCodes.WorkflowRecordInvalid);
        }

        return new(true, handoff, DiagnosticCodes.None);
    }

    public Task<IReadOnlyList<Handoff>> GetHandoffsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        store.GetHandoffsAsync(projectRoot, sprintId, cancellationToken);

    private static Guid NodeActionKey(string actionId, SprintId sprintId, NodeSnapshot node) =>
        StatusAdvisor.IdempotencyKey(
            actionId,
            new("node", $"{sprintId.Value:D}:{node.Id.Value}"),
            node.Version);

    private static string Fingerprint(SprintId sprintId, string messageKey, IReadOnlyList<string> evidence)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{sprintId.Value:D}|{messageKey}|{string.Join('|', evidence)}"));
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    // ponytail: these two internal helpers assume the single-process model the whole store already
    // assumes — a version conflict here would mean concurrent scheduling within the same process,
    // which nothing in this engine does. They resolve a conflict by returning the version
    // unchanged rather than throwing; callers that need a hard guarantee (the two user-facing verbs,
    // RetryNode/ResolveHumanGate) already re-check state themselves before calling in.
    private async Task<long> AppendNodeAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        string messageKey,
        NodeState toState,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        AppendOutcome outcome = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", messageKey,
            WorkflowStateNames.ToSnakeCase(toState), expectedVersion, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);
        return outcome.Succeeded ? outcome.State!.Nodes[nodeId].Version : expectedVersion;
    }

    private async Task<long> WalkAttemptAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        long expectedVersion,
        AttemptState toState,
        CancellationToken cancellationToken)
    {
        AppendOutcome outcome = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_transitioned", WorkflowStateNames.ToSnakeCase(toState), expectedVersion,
            Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded
            ? outcome.State!.Attempts[attemptId.Value.ToString("D")].Version
            : expectedVersion;
    }

    private async Task<SprintWorkflowState> RequireStateAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        await store.LoadAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Sprint '{sprintId.Value}' has no durable state.");

    private async Task<SprintDefinition> RequireDefinitionAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        await store.LoadDefinitionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Sprint '{sprintId.Value}' has no frozen definition.");
}
