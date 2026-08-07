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

    private static readonly AttemptState[] AttemptSucceededPath =
    [
        AttemptState.Created, AttemptState.Preparing, AttemptState.Running, AttemptState.Validating,
        AttemptState.Succeeded,
    ];

    private static readonly AttemptState[] AttemptFailedPath =
    [
        AttemptState.Created, AttemptState.Preparing, AttemptState.Running, AttemptState.Validating,
        AttemptState.Failed,
    ];

    private static readonly AttemptState[] AttemptRejectedPath =
        [AttemptState.Created, AttemptState.Preparing, AttemptState.Failed];

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
            AppendOutcome outcome = await store.AppendTransitionAsync(
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
            // A conflict here means this exact node was already initialized by an earlier, retried
            // call to this same method (creation is resumable) — anything else is a real failure
            // that must stop the whole sprint from ever looking initialized.
            if (!outcome.Succeeded && outcome.DiagnosticCode != DiagnosticCodes.WorkflowEventConflict)
            {
                throw new InvalidOperationException(
                    $"Failed to initialize node '{node.Id}': {outcome.DiagnosticCode}");
            }
        }

        await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes every `pending` node whose dependencies are all `succeeded`/`skipped` to `ready`,
    /// pushes any `ready` human gate straight to `awaiting_human` if the sprint is running, and
    /// keeps the sprint's own state synchronized with its gates. Safe to call repeatedly; a node
    /// with unmet dependencies is left untouched.
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
        if (state.Sprint.State == SprintState.Running)
        {
            foreach (NodeDefinition node in definition.Graph.Where(node => byId[node.Id].Kind == NodeKind.HumanGate))
            {
                if (!state.Nodes.TryGetValue(node.Id, out NodeSnapshot? snapshot))
                {
                    continue;
                }

                if (snapshot.State == NodeState.Ready)
                {
                    AppendOutcome runningOutcome = await AppendNodeAsync(
                        projectRoot, sprintId, node.Id, "workflow.node_running", NodeState.Running, snapshot.Version,
                        cancellationToken).ConfigureAwait(false);
                    if (!runningOutcome.Succeeded)
                    {
                        continue;
                    }

                    // A failure here leaves the node stuck at `running` with nothing revisiting it —
                    // the `running` branch below is what actually finishes the promotion, on the
                    // very next `AdvanceGraphAsync` call for this sprint.
                    await AppendNodeAsync(
                        projectRoot, sprintId, node.Id, "workflow.node_awaiting_human", NodeState.AwaitingHuman,
                        runningOutcome.State!.Nodes[node.Id].Version, cancellationToken).ConfigureAwait(false);
                }
                else if (snapshot.State == NodeState.Running)
                {
                    // Resumes a promotion interrupted between its two appends by an earlier call. A
                    // human gate reaches `running` only through this exact promotion or through
                    // `ResolveHumanGateAsync`'s own approve walk; either way, driving it on to
                    // `awaiting_human` here is a safe, idempotent re-assertion — `ResolveHumanGateAsync`
                    // re-derives its own next step from whatever state it next observes, so it still
                    // converges on the correct terminal outcome even if this races ahead of it.
                    await AppendNodeAsync(
                        projectRoot, sprintId, node.Id, "workflow.node_awaiting_human", NodeState.AwaitingHuman,
                        snapshot.Version, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await SynchronizeSprintGateStateAsync(projectRoot, sprintId, definition, cancellationToken)
            .ConfigureAwait(false);
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

        // `running` with the node's *current* attempt number is a legitimate resume point: a prior
        // call already moved the node but the attempt record itself did not land (a crash, or a
        // conflicting append). Basing the deterministic id on the number the node already carries —
        // rather than count + 1 — keeps a retry's id stable across that transition instead of
        // shifting to a new, unrelated one every time it is recomputed.
        bool nodeAlreadyRunning = node.State == NodeState.Running;
        int attemptNumber = nodeAlreadyRunning ? node.AttemptCount : node.AttemptCount + 1;
        AttemptId attemptId = DeterministicAttemptId(
            $"start_attempt|{sprintId.Value:D}|{nodeId}|{attemptNumber.ToString(CultureInfo.InvariantCulture)}");

        if (!nodeAlreadyRunning)
        {
            if (node.Version != expectedNodeVersion || node.State != NodeState.Ready)
            {
                return new(false, null, DiagnosticCodes.WorkflowEventConflict);
            }

            AppendOutcome nodeOutcome = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", "workflow.node_running",
                WorkflowStateNames.ToSnakeCase(NodeState.Running), expectedNodeVersion, Guid.NewGuid(),
                cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [WorkflowEvent.AttemptNumberArgument] = attemptNumber.ToString(CultureInfo.InvariantCulture),
                }).ConfigureAwait(false);
            if (!nodeOutcome.Succeeded)
            {
                return new(false, null, nodeOutcome.DiagnosticCode);
            }
        }

        AppendOutcome attemptOutcome = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_created", WorkflowStateNames.ToSnakeCase(WorkflowStateMachines.AttemptInitial), 0,
            Guid.NewGuid(), cancellationToken,
            new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.NodeIdArgument] = nodeId })
            .ConfigureAwait(false);
        if (!attemptOutcome.Succeeded)
        {
            // A conflict here *could* mean the attempt this exact call already created (the benign,
            // resumed case) — but a conflict proves nothing on its own, so that is verified against
            // durable state rather than assumed; any other failure, or a conflict where the attempt
            // still turns out not to exist, is real.
            bool attemptActuallyExists = attemptOutcome.DiagnosticCode == DiagnosticCodes.WorkflowEventConflict &&
                (await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false))
                    .Attempts.ContainsKey(attemptId.Value.ToString("D"));
            if (!attemptActuallyExists)
            {
                return new(false, null, attemptOutcome.DiagnosticCode);
            }
        }

        return new(true, attemptId, DiagnosticCodes.None);
    }

    /// <summary>
    /// Reports an attempt's conclusion, walking it through the remaining attempt states, settling
    /// the owning node, writing its <see cref="NodeResult"/>, applying the bounded auto-retry
    /// policy on failure, and re-evaluating the graph and the sprint's completion gate. Resumable:
    /// a retry with the same <paramref name="attemptId"/> after a crash picks up wherever the
    /// interrupted call left off instead of re-doing (or losing) durable work.
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
        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        string attemptKey = attemptId.Value.ToString("D");
        if (!state.Attempts.TryGetValue(attemptKey, out AttemptSnapshot? attempt))
        {
            return new(false, node, DiagnosticCodes.WorkflowEventConflict);
        }

        if (attempt.NodeId is not null && attempt.NodeId != nodeId)
        {
            return new(false, node, DiagnosticCodes.AttemptOwnershipMismatch);
        }

        string requestedOutcome = WorkflowStateNames.ToSnakeCase(succeeded ? AttemptState.Succeeded : AttemptState.Failed);
        if (attempt.TargetOutcome is { } persistedOutcome && persistedOutcome != requestedOutcome)
        {
            // A retry that flips `succeeded` from what this exact attempt already durably committed
            // to is a genuine conflict — never a silent reinterpretation of an already-driven walk.
            return new(false, node, DiagnosticCodes.WorkflowEventConflict);
        }

        IReadOnlyList<NodeResult> existingResults =
            await store.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        NodeResult? existingResult = existingResults.FirstOrDefault(item => item.AttemptId == attemptId);
        bool alreadySettledByThisAttempt = existingResult is not null && node.State is NodeState.Succeeded or NodeState.Failed;
        if (node.State != NodeState.Running && !alreadySettledByThisAttempt)
        {
            return new(false, node, DiagnosticCodes.NodeTransitionInvalid);
        }

        NodeResult result;
        if (existingResult is null)
        {
            result = new(
                sprintId,
                new(nodeId),
                attemptId,
                succeeded ? NodeOutcome.Succeeded : NodeOutcome.Failed,
                attempt.UpdatedAt,
                clock.UtcNow,
                inputDigest,
                outputs ?? [],
                diagnostics ?? []);
            // Validated *before* anything below becomes durable: a malformed result (e.g. a caller
            // passing a garbled digest) must never leave the node succeeded/failed with no result to
            // show for it, wedging the sprint with an unrecoverable state and a record that was
            // never written.
            try
            {
                WorkflowRecordCodec.ValidateNodeResult(result);
            }
            catch (InvalidDataException)
            {
                return new(false, node, DiagnosticCodes.WorkflowRecordInvalid);
            }
        }
        else
        {
            result = existingResult;
        }

        AppendOutcome walkOutcome = await DriveAttemptAsync(
            projectRoot, sprintId, attemptId, succeeded ? AttemptSucceededPath : AttemptFailedPath, null,
            cancellationToken,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkflowEvent.TargetOutcomeArgument] = requestedOutcome,
            }).ConfigureAwait(false);
        if (!walkOutcome.Succeeded)
        {
            return new(false, node, walkOutcome.DiagnosticCode);
        }

        // The result is written before the terminal node transition below: a crash between the two
        // leaves a `running` node with a durable result (recoverable — this method resumes and just
        // finishes the node transition), never a terminal node with no result to show for it.
        if (existingResult is null)
        {
            try
            {
                await store.SaveNodeResultAsync(projectRoot, result, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A different result already exists for this attempt id — a genuine conflict, not a
                // replay; the target-outcome check above should normally catch this earlier, so this
                // is a defensive backstop against corruption rather than the expected path here.
                return new(false, node, DiagnosticCodes.WorkflowEventConflict);
            }
        }

        if (node.State == NodeState.Running)
        {
            AppendOutcome nodeOutcome = await AppendNodeAsync(
                projectRoot, sprintId, nodeId, succeeded ? "workflow.node_succeeded" : "workflow.node_failed",
                succeeded ? NodeState.Succeeded : NodeState.Failed, node.Version, cancellationToken)
                .ConfigureAwait(false);
            if (!nodeOutcome.Succeeded)
            {
                return new(false, node, nodeOutcome.DiagnosticCode);
            }
        }

        if (!succeeded)
        {
            SprintWorkflowState settledState = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false);
            NodeSnapshot settled = settledState.Nodes[nodeId];
            if (settled.State == NodeState.Failed && settled.AttemptCount < MaxAutomaticRetries + 1)
            {
                AppendOutcome retryOutcome = await AppendNodeAsync(
                    projectRoot, sprintId, nodeId, "workflow.node_retrying", NodeState.Ready, settled.Version,
                    cancellationToken).ConfigureAwait(false);
                if (!retryOutcome.Succeeded)
                {
                    return new(false, settled, retryOutcome.DiagnosticCode);
                }
            }
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

    /// <summary>
    /// Approves or rejects a human gate (matches the `workflow.review` / `ResolveHumanGate`
    /// capability). Resumable: the underlying attempt id is deterministic in
    /// (sprint, node, node version), so a retry of the same decision after a crash continues the
    /// same attempt instead of abandoning it — a crash mid-sequence can never wedge the gate.
    /// </summary>
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

        // Deterministic on (sprint, node, the node version the decision was made against, and the
        // decision itself): a retried call for the same logical decision resumes the same
        // underlying attempt instead of abandoning it, so a crash mid-sequence can never leave the
        // gate wedged. Approve and reject get *different* ids on purpose — a retry that flips the
        // decision for the same node version must never resume (and silently reinterpret) the
        // other decision's already-in-flight or already-settled attempt; it is refused below
        // instead, as a stable conflict, once the node is no longer in a state either path expects.
        AttemptId attemptId = DeterministicAttemptId(
            $"resolve_human_gate|{sprintId.Value:D}|{nodeId}|{expectedNodeVersion.ToString(CultureInfo.InvariantCulture)}|{approved}");
        bool resuming = state.Attempts.ContainsKey(attemptId.Value.ToString("D"));
        if (!resuming)
        {
            // A brand-new decision must match the gate's current `awaiting_human` version and key
            // exactly. Once resuming (the deterministic attempt above already exists), the node's
            // *current* version has legitimately moved past `expectedNodeVersion` mid-walk — the key
            // was already validated when this decision first started, and re-checking it against
            // the node's now-different version would defeat resumability entirely.
            if (node.Version != expectedNodeVersion || idempotencyKey != ResolveHumanGateKey(sprintId, node))
            {
                return new(false, node, DiagnosticCodes.SuggestionStale);
            }

            if (node.State != NodeState.AwaitingHuman)
            {
                return new(false, node, DiagnosticCodes.NodeTransitionInvalid);
            }
        }
        else if (node.State is not (NodeState.AwaitingHuman or NodeState.Running or NodeState.Succeeded or NodeState.Failed))
        {
            return new(false, node, DiagnosticCodes.NodeTransitionInvalid);
        }

        AppendOutcome walkOutcome = await DriveAttemptAsync(
            projectRoot, sprintId, attemptId, approved ? AttemptSucceededPath : AttemptRejectedPath,
            new Dictionary<string, string?>(StringComparer.Ordinal) { [WorkflowEvent.NodeIdArgument] = nodeId },
            cancellationToken).ConfigureAwait(false);
        if (!walkOutcome.Succeeded)
        {
            return new(false, node, walkOutcome.DiagnosticCode);
        }

        IReadOnlyList<NodeResult> existingResults =
            await store.GetNodeResultsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (existingResults.All(item => item.AttemptId != attemptId))
        {
            DateTimeOffset now = clock.UtcNow;
            try
            {
                await store.SaveNodeResultAsync(
                    projectRoot,
                    new(
                        sprintId, new(nodeId), attemptId, approved ? NodeOutcome.Succeeded : NodeOutcome.Failed, now,
                        now, "sha256:" + new string('0', 64), [], []),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return new(false, node, DiagnosticCodes.WorkflowEventConflict);
            }
        }

        if (node.State == NodeState.AwaitingHuman)
        {
            if (approved)
            {
                AppendOutcome runningOutcome = await AppendNodeAsync(
                    projectRoot, sprintId, nodeId, "workflow.node_running", NodeState.Running, node.Version,
                    cancellationToken).ConfigureAwait(false);
                if (!runningOutcome.Succeeded)
                {
                    return new(false, node, runningOutcome.DiagnosticCode);
                }

                AppendOutcome succeededOutcome = await AppendNodeAsync(
                    projectRoot, sprintId, nodeId, "workflow.node_succeeded", NodeState.Succeeded,
                    runningOutcome.State!.Nodes[nodeId].Version, cancellationToken).ConfigureAwait(false);
                if (!succeededOutcome.Succeeded)
                {
                    return new(false, node, succeededOutcome.DiagnosticCode);
                }
            }
            else
            {
                AppendOutcome rejectedOutcome = await AppendNodeAsync(
                    projectRoot, sprintId, nodeId, "workflow.node_rejected", NodeState.Failed, node.Version,
                    cancellationToken).ConfigureAwait(false);
                if (!rejectedOutcome.Succeeded)
                {
                    return new(false, node, rejectedOutcome.DiagnosticCode);
                }
            }
        }
        else if (node.State == NodeState.Running)
        {
            AppendOutcome succeededOutcome = await AppendNodeAsync(
                projectRoot, sprintId, nodeId, "workflow.node_succeeded", NodeState.Succeeded, node.Version,
                cancellationToken).ConfigureAwait(false);
            if (!succeededOutcome.Succeeded)
            {
                return new(false, node, succeededOutcome.DiagnosticCode);
            }
        }

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

        AppendOutcome outcome = await AppendNodeAsync(
            projectRoot, sprintId, nodeId, "workflow.node_skipped", NodeState.Skipped, expectedNodeVersion,
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return new(false, node, outcome.DiagnosticCode);
        }

        await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        SprintWorkflowState final = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, final.Nodes[nodeId], DiagnosticCodes.None);
    }

    /// <summary>
    /// Every node in `succeeded`/`skipped` with zero open findings promotes the sprint to
    /// `ready_to_finalize`; any node stuck at `failed` with no automatic retries left blocks it. A
    /// no-op while other nodes still have in-flight work, while any finding stays `open`, and if the
    /// sprint is not currently `running` (an internal, best-effort check: a lost race just waits for
    /// the next call that observes the settled state). The two sprint-level appends below are
    /// deliberately not propagated further: this method returns nothing to propagate to, and a
    /// failed append here is provably safe to ignore — it means the sprint's version already moved
    /// (something else changed it first), so applying this stale view would itself be wrong, and
    /// every caller of this method already re-evaluates on its own next call.
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
            IReadOnlyList<Finding> findings =
                await store.GetFindingsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
            if (findings.Any(finding => finding.Status == FindingStatus.Open))
            {
                // Not a distinct sprint state: the sprint just stays `running` until every open
                // finding is resolved, then a later call (see ResolveFindingAsync) re-evaluates.
                return;
            }

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

        // A new open finding invalidates an already-`ready_to_finalize` sprint's readiness — move it
        // back to `blocked` immediately rather than leave completion racing ahead of a finding that
        // arrived just after every node settled. `EvaluateCompletionAsync` itself only runs for a
        // `running` sprint, so it cannot be the one to catch this.
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Sprint.State == SprintState.ReadyToFinalize)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_blocked", WorkflowStateNames.ToSnakeCase(SprintState.Blocked), state.Sprint.Version,
                Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
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
        // The completion gate re-checks findings only when it runs; resolving the last open finding
        // must itself be the trigger that lets an otherwise-settled sprint advance.
        await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
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

    private static AttemptId DeterministicAttemptId(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new(new Guid(hash.AsSpan(0, 16)));
    }

    /// <summary>
    /// Moves an attempt from its current durable state forward through <paramref name="path"/> —
    /// an ordered sequence of legal states starting at `created` — appending only the steps not
    /// already applied. Creates the attempt (at <c>path[0]</c>) if it does not exist yet. If the
    /// attempt's current state is not on this path at all (settled via a different branch by an
    /// earlier call), there is nothing left to drive and this is a no-op success. Callers that
    /// retry with the same attempt id and the same path always converge on the same terminal state,
    /// regardless of where a prior crash interrupted the walk.
    /// </summary>
    private async Task<AppendOutcome> DriveAttemptAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        AttemptState[] path,
        IReadOnlyDictionary<string, string?>? creationArguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? firstAdvanceArguments = null)
    {
        string attemptKey = attemptId.Value.ToString("D");
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Attempts.TryGetValue(attemptKey, out AttemptSnapshot? attempt))
        {
            AppendOutcome created = await WalkAttemptAsync(
                projectRoot, sprintId, attemptId, 0, path[0], cancellationToken, creationArguments)
                .ConfigureAwait(false);
            if (!created.Succeeded)
            {
                return created;
            }

            attempt = created.State!.Attempts[attemptKey];
        }

        int index = Array.IndexOf(path, attempt.State);
        if (index < 0)
        {
            return new(true, state, DiagnosticCodes.None);
        }

        long version = attempt.Version;
        for (int i = index + 1; i < path.Length; i++)
        {
            // Attached only to the true first advance away from `created` in the attempt's whole
            // lifetime (never on a resumed call picking up from a later state) — this is where a
            // caller-supplied fact like the requested outcome becomes durable, once and for good.
            IReadOnlyDictionary<string, string?>? extra =
                i == index + 1 && index == 0 ? firstAdvanceArguments : null;
            AppendOutcome outcome = await WalkAttemptAsync(
                projectRoot, sprintId, attemptId, version, path[i], cancellationToken, extra).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                return outcome;
            }

            version = outcome.State!.Attempts[attemptKey].Version;
        }

        return new(true, await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false), DiagnosticCodes.None);
    }

    /// <summary>
    /// Keeps the sprint aggregate synchronized with its human gates: enters `awaiting_human` the
    /// moment any gate is, returns to `running` once none are (approval path), and moves straight
    /// to `blocked` the moment any gate is rejected. Recomputed from durable node states on every
    /// call, so a restart mid-sequence converges on the same sprint state without extra bookkeeping.
    /// The three appends below are not further propagated for the same reason as
    /// <see cref="EvaluateCompletionAsync"/>'s: a failed one means the sprint's version already
    /// moved, so this stale view no longer applies, and every caller re-invokes this on its own
    /// next graph advance.
    /// </summary>
    private async Task SynchronizeSprintGateStateAsync(
        string projectRoot,
        SprintId sprintId,
        SprintDefinition definition,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Sprint.State is not (SprintState.Running or SprintState.AwaitingHuman))
        {
            return;
        }

        Dictionary<string, NodeKind> kindById = definition.Graph.ToDictionary(
            node => node.Id, node => node.Kind, StringComparer.Ordinal);
        bool anyAwaiting = state.Nodes.Any(entry =>
            kindById.TryGetValue(entry.Key, out NodeKind kind) && kind == NodeKind.HumanGate &&
            entry.Value.State == NodeState.AwaitingHuman);
        bool anyRejected = state.Nodes.Any(entry =>
            kindById.TryGetValue(entry.Key, out NodeKind kind) && kind == NodeKind.HumanGate &&
            entry.Value.State == NodeState.Failed);

        if (state.Sprint.State == SprintState.Running && anyAwaiting)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_awaiting_human", WorkflowStateNames.ToSnakeCase(SprintState.AwaitingHuman),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        }
        else if (state.Sprint.State == SprintState.AwaitingHuman && anyRejected)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_blocked", WorkflowStateNames.ToSnakeCase(SprintState.Blocked),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        }
        else if (state.Sprint.State == SprintState.AwaitingHuman && !anyAwaiting)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_gate_resumed", WorkflowStateNames.ToSnakeCase(SprintState.Running),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        }
    }

    // ponytail: this internal helper assumes the single-process model the whole store already
    // assumes — a version conflict here would mean concurrent scheduling within the same process,
    // which nothing in this engine does. `AdvanceGraphAsync`'s promotion loops treat a failed
    // append as "leave this node for the next call" (each node's append is independent of the
    // others in the same loop); the two user-facing compound verbs that chain a node's version
    // across appends (`CompleteAttemptAsync`, `ResolveHumanGateAsync`) check every outcome
    // themselves and stop on the first failure instead.
    private async Task<AppendOutcome> AppendNodeAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        string messageKey,
        NodeState toState,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Node, nodeId, "NodeChanged", messageKey,
            WorkflowStateNames.ToSnakeCase(toState), expectedVersion, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);

    private async Task<AppendOutcome> WalkAttemptAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        long expectedVersion,
        AttemptState toState,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? extraArguments = null) =>
        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_transitioned", WorkflowStateNames.ToSnakeCase(toState), expectedVersion,
            Guid.NewGuid(), cancellationToken, extraArguments).ConfigureAwait(false);

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
