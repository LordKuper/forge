using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Domain;

namespace Forge.Application;

public sealed record StartAttemptResult(bool Succeeded, AttemptId? AttemptId, string DiagnosticCode);

public sealed record CompleteAttemptResult(bool Succeeded, NodeSnapshot? Node, string DiagnosticCode);

public sealed record NodeActionResult(bool Succeeded, NodeSnapshot? Node, string DiagnosticCode);

public sealed record RecordFindingResult(bool Succeeded, Finding? Finding, string DiagnosticCode);

public sealed record RecordHandoffResult(bool Succeeded, Handoff? Handoff, string DiagnosticCode);

public sealed record RecordConfirmationResult(bool Succeeded, ConfirmationArtifact? Confirmation, string DiagnosticCode);

public sealed record RecordReviewIterationResult(bool Succeeded, ReviewIterationRecord? Record, string DiagnosticCode);

public sealed record ResolveReviewConvergenceResult(bool Succeeded, string DiagnosticCode);

public sealed record RecordActivityResult(bool Succeeded, AttemptSnapshot? Attempt, string DiagnosticCode);

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

    // Matches `finding.schema.json`'s own `message_key` pattern — validated here too since
    // `RecordReviewIterationAsync` must reject a malformed `ReviewFindingDraft` before persisting
    // anything, not discover the same constraint mid-loop via a schema-validation exception.
    private static readonly Regex MessageKeyPattern = new("^[a-z0-9_.-]+$", RegexOptions.Compiled);

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

        foreach (NodeDefinition node in definition.Graph)
        {
            if (!state.Nodes.TryGetValue(node.Id, out NodeSnapshot? snapshot) || snapshot.State != NodeState.Pending)
            {
                continue;
            }

            bool satisfied = node.DependsOn.All(dependency =>
                state.Nodes.TryGetValue(dependency, out NodeSnapshot? upstream) &&
                upstream.State is NodeState.Succeeded or NodeState.Skipped);
            if (satisfied && node.Role == NodeRole.TestWork)
            {
                // Dependency completion alone is not enough for a test-work node: the plan's own
                // gate ("Host state transitions must reject premature test work") requires a
                // recorded, `Confirmed` artifact from every confirmation-role dependency, not just
                // that dependency having run.
                satisfied = await IsTestWorkEligibleAsync(projectRoot, sprintId, definition, node, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!satisfied)
            {
                continue;
            }

            await AppendNodeAsync(
                projectRoot, sprintId, node.Id, "workflow.node_ready", NodeState.Ready, snapshot.Version,
                cancellationToken).ConfigureAwait(false);
        }

        // Brings the sprint's own state up to date with whatever just settled — e.g. a gate that was
        // resolved by the caller just above this call — *before* deciding whether to push any newly
        // `ready` gate onward below. Otherwise a gate that becomes `ready` only as a side effect of
        // the promotion loop above (it depends on another gate that just resolved in this same
        // sprint) could be promoted to `ready` while the sprint is still mid-transition back to
        // `running`, and the loop below — gated on the sprint already being `running` — would then
        // skip it entirely, with nothing else ever calling `AdvanceGraphAsync` again to catch it.
        await SynchronizeSprintGateStateAsync(projectRoot, sprintId, definition, cancellationToken)
            .ConfigureAwait(false);

        state = await RequireStateAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (state.Sprint.State == SprintState.Running)
        {
            foreach (NodeDefinition node in definition.Graph.Where(node => node.Kind == NodeKind.HumanGate))
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

        // Syncs again: a gate just promoted to `awaiting_human` immediately above must flip the
        // sprint there too, in the same call — a caller must never observe `running` with a gate
        // already `awaiting_human` even momentarily between two separate calls.
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

        if (definedNode.Role == NodeRole.TestWork &&
            !await IsTestWorkEligibleAsync(projectRoot, sprintId, definition, definedNode, cancellationToken)
                .ConfigureAwait(false))
        {
            return new(false, null, DiagnosticCodes.WorkflowBlocked);
        }

        // `running` with the node's *current* attempt number is a legitimate resume point: a prior
        // call already moved the node but the attempt record itself did not land (a crash, or a
        // conflicting append). Basing the deterministic id on the number the node already carries —
        // rather than count + 1 — keeps a retry's id stable across that transition instead of
        // shifting to a new, unrelated one every time it is recomputed.
        // ponytail: on this resume path `expectedNodeVersion` is not re-checked against the node's
        // current version — there is no prior version to check it against once the node has already
        // moved. A caller with an arbitrarily stale version is handed the real, already-in-flight
        // attempt id rather than rejected; that id is inert on its own (every later verb re-validates
        // ownership and state independently), so this trades a slightly looser staleness guarantee
        // on this one path for actually being resumable. Revisit if that trade stops being safe.
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

    /// <summary>
    /// Safely bumps an in-flight attempt's last-activity time — ADR 0006's "safe, throttled activity
    /// events" that reset the idle deadline without persisting any provider content. Never touches
    /// the attempt's state, node state, or version-gated transition history; a caller repeats this
    /// as often as it likes while the attempt is owned. Rejected once the attempt has reached a
    /// terminal state, so a heartbeat racing a real completion never resurrects a settled attempt.
    /// </summary>
    public async Task<RecordActivityResult> RecordAttemptActivityAsync(
        string projectRoot,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        string attemptKey = attemptId.Value.ToString("D");
        if (!state.Attempts.TryGetValue(attemptKey, out AttemptSnapshot? attempt))
        {
            return new(false, null, DiagnosticCodes.WorkflowEventConflict);
        }

        if (WorkflowStateMachines.IsTerminal(attempt.State))
        {
            return new(false, attempt, DiagnosticCodes.AttemptTerminal);
        }

        await store.AppendAttemptActivityAsync(projectRoot, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        SprintWorkflowState updated = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, updated.Attempts[attemptKey], DiagnosticCodes.None);
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
    /// no-op while other nodes still have in-flight work, while any finding stays `open`, or if the
    /// sprint is not currently `running` (an internal, best-effort check: a lost race just waits for
    /// the next call that observes the settled state). Deliberately does *not* also run from
    /// `blocked`: unlike `running`, a sprint can be `blocked` for a reason that has nothing to do
    /// with findings (a genuinely stuck node), and this method cannot tell those apart from
    /// `allSettledGood` alone once an operator has manually retried or skipped the stuck node —
    /// promoting straight to `ready_to_finalize` from there would bypass the explicit
    /// `resume_sprint`/`run_sprint` decision `blocked` exists to require. See
    /// `TryAdvanceFindingsOnlyBlockedSprintAsync` for the one narrow, explicit path that *is* allowed
    /// to leave `blocked` this way. The sprint-level append below is deliberately not propagated
    /// further: this method returns nothing to propagate to, and a failed append here is provably
    /// safe to ignore — it means the sprint's version already moved (something else changed it
    /// first), so applying this stale view would itself be wrong, and every caller of this method
    /// already re-evaluates on its own next call.
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
        // budget to exhaust the way a work node does. `TryGetValue`, not an indexer: a node from an
        // earlier, differently-frozen definition could in principle be durable without a matching
        // graph entry, and this must never throw for that.
        bool anyStuck = state.Nodes.Any(entry => entry.Value.State == NodeState.Cancelled ||
            (entry.Value.State == NodeState.Failed &&
                (!kindById.TryGetValue(entry.Key, out NodeKind kind) || kind == NodeKind.HumanGate ||
                    entry.Value.AttemptCount >= MaxAutomaticRetries + 1)));
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
                state.Sprint.Version, Guid.NewGuid(), cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [WorkflowEvent.BlockedReasonArgument] = BlockedByNode,
                }).ConfigureAwait(false);
        }
    }

    /// <summary>A durable <see cref="WorkflowEvent.BlockedReasonArgument"/> tag for each of the
    /// five sites that can append a sprint's `blocked` transition — a stuck/failed node
    /// (<see cref="EvaluateCompletionAsync"/>), a late-arriving open finding
    /// (<see cref="RecordFindingAsync"/>), a rejected human gate
    /// (<see cref="SynchronizeSprintGateStateAsync"/>), a `NotConfirmed` confirmation
    /// (<see cref="RecordConfirmationAsync"/>), and a review-convergence trigger
    /// (<see cref="RecordReviewIterationAsync"/>). Only <see cref="BlockedByFinding"/> may ever
    /// recover automatically; the other four require the operator's explicit
    /// `resume_sprint`/`run_sprint` decision, and nothing here may blur that distinction by treating
    /// "every node happens to be settled good right now" as proof of *why* it got that way.</summary>
    private const string BlockedByNode = "node";

    private const string BlockedByFinding = "finding";

    private const string BlockedByGate = "gate";

    private const string BlockedByConfirmation = "confirmation";

    private const string BlockedByReviewConvergence = "review_convergence";

    /// <summary>
    /// Appends a `blocked` transition with <paramref name="reason"/> if (and only if) the sprint is
    /// currently in a blockable state (<see cref="SprintState.Running"/> or
    /// <see cref="SprintState.ReadyToFinalize"/>), retrying up to 5 times on a version conflict.
    /// Returns <see langword="true"/> when nothing needed blocking, or the block landed;
    /// <see langword="false"/> only when blocking was needed and never landed after retrying —
    /// callers that already made their own primary effect durable (a confirmation artifact, a
    /// review-iteration record) use that to report a partial failure without losing the artifact
    /// itself, since this append is only ever the sprint's secondary, operator-visible signal.
    /// </summary>
    private async Task<bool> TryBlockSprintAsync(
        string projectRoot,
        SprintId sprintId,
        string reason,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false);
            if (state.Sprint.State is not (SprintState.Running or SprintState.ReadyToFinalize))
            {
                return true;
            }

            AppendOutcome blocked = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_blocked", WorkflowStateNames.ToSnakeCase(SprintState.Blocked),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [WorkflowEvent.BlockedReasonArgument] = reason,
                }).ConfigureAwait(false);
            if (blocked.Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The one narrow, explicit path that may resume a finding-blocked sprint automatically:
    /// called only from <see cref="ResolveFindingAsync"/>, and only advances
    /// when the sprint's durable `blocked_reason` is itself <see cref="BlockedByFinding"/> — not
    /// merely when every node happens to be settled good, which a stuck node's manual retry-and-skip
    /// produces identically to a genuine late-finding block, and would otherwise let resolving an
    /// unrelated finding launder either kind of block past the operator's required decision.
    /// </summary>
    private async Task TryAdvanceFindingsOnlyBlockedSprintAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        bool beginsRecovery = state.Sprint.State == SprintState.Blocked &&
            state.Sprint.BlockedReason == BlockedByFinding;
        bool resumesRecovery = state.Sprint.State == SprintState.Ready &&
            state.Sprint.BlockedReason == BlockedByFinding;
        if ((!beginsRecovery && !resumesRecovery) || state.Nodes.Count == 0)
        {
            return;
        }

        bool allSettledGood = state.Nodes.Values.All(node => node.State is NodeState.Succeeded or NodeState.Skipped);
        if (!allSettledGood)
        {
            return;
        }

        IReadOnlyList<Finding> findings =
            await store.GetFindingsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        if (findings.Any(finding => finding.Status == FindingStatus.Open))
        {
            return;
        }

        if (beginsRecovery)
        {
            AppendOutcome ready = await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
                "workflow.sprint_ready", WorkflowStateNames.ToSnakeCase(SprintState.Ready),
                state.Sprint.Version, Guid.NewGuid(), cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [WorkflowEvent.BlockedReasonArgument] = BlockedByFinding,
                }).ConfigureAwait(false);
            if (!ready.Succeeded)
            {
                return;
            }

            state = ready.State!;
        }

        AppendOutcome running = await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_running", WorkflowStateNames.ToSnakeCase(SprintState.Running),
            state.Sprint.Version, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (running.Succeeded)
        {
            await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
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
                Guid.NewGuid(), cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [WorkflowEvent.BlockedReasonArgument] = BlockedByFinding,
                }).ConfigureAwait(false);
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
        // must itself be the trigger that lets an otherwise-settled sprint advance — whether the
        // sprint is still `running` (the gate itself) or was moved to `blocked` by a finding that
        // arrived after every node had already settled (the narrow recovery path below).
        await EvaluateCompletionAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        await TryAdvanceFindingsOnlyBlockedSprintAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Records a confirmation node's judgment against its definition of done. Unlike a node
    /// result, this carries no attempt id and needs no particular node state — the same
    /// state-independence <see cref="RecordHandoffAsync"/> already has — so it can be replayed or
    /// backfilled without racing the node's own transitions. <paramref name="nodeId"/> must name a
    /// node in the sprint's frozen graph tagged <see cref="NodeRole.Confirmation"/>: an artifact
    /// against an unknown or wrongly-tagged node can gate nothing real and, once a real change
    /// feeds this from provider output, could otherwise block a sprint on an id an attacker
    /// controls. A <see cref="ConfirmationOutcome.NotConfirmed"/> verdict immediately blocks a
    /// running (or already ready-to-finalize) sprint, mirroring how a late open
    /// <see cref="Finding"/> does in <see cref="RecordFindingAsync"/> — the operator must explicitly
    /// resume it, since only the *most recently recorded* artifact for a confirmation node governs
    /// eligibility (see <see cref="IsTestWorkEligibleAsync"/>): a `Confirmed` artifact can be
    /// superseded by a later `NotConfirmed` one for the same node (a confirmation node re-attempted
    /// after its own rejection), and must never keep a dependent test-work node eligible once that
    /// happens. The returned result can report <c>Succeeded: false</c> even though the artifact
    /// itself was durably recorded, if the sprint needed blocking and the append that blocks it
    /// could not be made to land after retrying — the artifact is still returned in that case so a
    /// caller is not left wondering whether anything happened at all.
    /// </summary>
    public async Task<RecordConfirmationResult> RecordConfirmationAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        ConfirmationOutcome outcome,
        string definitionOfDone,
        IReadOnlyList<ConfirmationEvidence> evidence,
        CancellationToken cancellationToken)
    {
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        NodeDefinition? definedNode = definition.Graph.FirstOrDefault(item => item.Id == nodeId);
        if (definedNode is null)
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        if (definedNode.Role != NodeRole.Confirmation)
        {
            return new(false, null, DiagnosticCodes.NodeKindMismatch);
        }

        ConfirmationArtifact confirmation = new(
            Guid.NewGuid(), sprintId, new(nodeId), outcome, definitionOfDone, evidence, clock.UtcNow);
        try
        {
            await store.SaveConfirmationAsync(projectRoot, confirmation, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new(false, null, DiagnosticCodes.WorkflowRecordInvalid);
        }

        if (outcome != ConfirmationOutcome.Confirmed)
        {
            // The artifact itself already blocks eligibility via `IsTestWorkEligibleAsync`
            // regardless of whether this lands — only the secondary, operator-visible block signal
            // can fail here.
            if (!await TryBlockSprintAsync(projectRoot, sprintId, BlockedByConfirmation, cancellationToken)
                .ConfigureAwait(false))
            {
                return new(false, confirmation, DiagnosticCodes.WorkflowEventConflict);
            }
        }
        else
        {
            // A dependent test-work node may already be sitting `pending` on this exact
            // confirmation — see `IsTestWorkEligibleAsync` — so a `Confirmed` verdict must itself
            // re-drive the graph, the same way completing any other node's attempt does.
            await AdvanceGraphAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        }

        return new(true, confirmation, DiagnosticCodes.None);
    }

    public Task<IReadOnlyList<ConfirmationArtifact>> GetConfirmationsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        store.GetConfirmationsAsync(projectRoot, sprintId, cancellationToken);

    /// <summary>
    /// Records one review iteration's combined verdict for <paramref name="nodeId"/> (a
    /// <see cref="NodeRole.Review"/> node) and <paramref name="dimension"/> — ADR 0006's ASD
    /// severity-floor policy (see <see cref="ReviewConvergencePolicy"/>). <see cref="ReviewIterationRecord.Iteration"/>
    /// is derived: the count of prior records for the same (node, dimension) plus one. A
    /// <see cref="ReviewerKind.Internal"/> call with a missing or incomplete
    /// <paramref name="coverage"/> ledger records nothing and does not consume an iteration — ADR
    /// 0006: "An incomplete ledger invalidates that verdict and causes one fresh re-dispatch in the
    /// same iteration." On <see cref="ReviewOutcome.ChangesRequested"/>, every finding at or above
    /// the iteration's severity floor (or the pinned critical floor, once
    /// <see cref="PinReviewFloorAsync"/> has been called for this dimension) is recorded the normal,
    /// blocking way via <see cref="RecordFindingAsync"/>; findings below the floor are still
    /// recorded, but immediately <see cref="FindingStatus.Dismissed"/> — "dropped, not silently
    /// lost." The sprint is blocked (reason <c>review_convergence</c>, matching the
    /// <c>confirmation</c>/<c>node</c>/<c>gate</c> family — an explicit
    /// `resume_sprint`/`run_sprint` is always required afterward, nothing here auto-recovers) when
    /// either: this is a <see cref="ReviewOutcome.ChangesRequested"/> verdict whose iteration would
    /// exceed the cumulative severity-floor budget and the floor is not already pinned
    /// (<see cref="DiagnosticCodes.ReviewIterationLimit"/> — an <see cref="ReviewOutcome.Approved"/>
    /// verdict never trips this, however high its iteration number: nothing is left to converge
    /// on once review has approved), or this is an external reviewer's
    /// <see cref="ReviewOutcome.ChangesRequested"/> repeating the immediately preceding external
    /// iteration's exact normalized finding set (<see cref="DiagnosticCodes.ReviewRepeatedFindings"/>).
    /// </summary>
    public async Task<RecordReviewIterationResult> RecordReviewIterationAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        ReviewDimension dimension,
        ReviewerKind reviewerKind,
        ReviewOutcome outcome,
        IReadOnlyList<ReviewFindingDraft> findings,
        CoverageLedger? coverage,
        CancellationToken cancellationToken)
    {
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        NodeDefinition? definedNode = definition.Graph.FirstOrDefault(item => item.Id == nodeId);
        if (definedNode is null)
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        if (definedNode.Role != NodeRole.Review)
        {
            return new(false, null, DiagnosticCodes.NodeKindMismatch);
        }

        if (reviewerKind == ReviewerKind.Internal &&
            (coverage is null || !ReviewConvergencePolicy.IsCoverageComplete(coverage)))
        {
            return new(false, null, DiagnosticCodes.WorkflowRecordInvalid);
        }

        // Every draft must already satisfy `finding.schema.json`'s own constraints — non-empty
        // evidence, a message key matching its `^[a-z0-9_.-]+$` pattern, and a location line of at
        // least 1 when a location is given — before anything is recorded, checked once, up front,
        // so a malformed draft fails cleanly with no iteration consumed (the same "invalid input,
        // nothing recorded" rule the coverage-ledger check above already enforces), rather than
        // reaching `RecordFindingAsync`/`SaveFindingAsync` mid-loop with some findings already
        // durable and others not.
        if (outcome == ReviewOutcome.ChangesRequested &&
            findings.Any(finding =>
                finding.Evidence.Count == 0 || !MessageKeyPattern.IsMatch(finding.MessageKey) ||
                finding.Location?.Line < 1))
        {
            return new(false, null, DiagnosticCodes.WorkflowRecordInvalid);
        }

        IReadOnlyList<ReviewIterationRecord> priorForDimension = [.. (await store
                .GetReviewIterationsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false))
            .Where(item => item.NodeId.Value == nodeId && item.Dimension == dimension)
            .OrderBy(item => item.Iteration)];
        // ponytail: derived from a plain count, not committed under an expected-version compare
        // like `StartAttemptAsync`'s attempt numbers — two genuinely concurrent calls for the same
        // (node, dimension) could compute the same iteration number, since nothing here claims it
        // atomically. Matches this class's own documented single-process assumption elsewhere
        // (`AppendNodeAsync`'s remarks); add a version-gated claim if this engine ever needs to run
        // outside that assumption.
        int iteration = priorForDimension.Count + 1;
        bool floorPinned = await store
            .IsReviewFloorPinnedAsync(projectRoot, sprintId, nodeId, dimension, cancellationToken)
            .ConfigureAwait(false);

        List<NormalizedFindingKey> externalKeys = reviewerKind == ReviewerKind.External
            ? [.. findings.Select(finding => new NormalizedFindingKey(
                finding.Location?.Path, finding.Location?.Line, finding.MessageKey,
                Fingerprint(sprintId, finding.MessageKey, finding.Evidence)))]
            : [];
        bool repeated = reviewerKind == ReviewerKind.External && outcome == ReviewOutcome.ChangesRequested &&
            ReviewConvergencePolicy.HasRepeatedExternalFindingSet(
                [.. priorForDimension.Where(item => item.ReviewerKind == ReviewerKind.External)], externalKeys);
        // Only an unresolved `ChangesRequested` verdict has anything left to converge on — an
        // *approving* verdict that happens to land on iteration 15 closed the review; gating it
        // the same way as a real budget overrun would force a needless operator decision.
        bool exceedsLimit = outcome == ReviewOutcome.ChangesRequested && !floorPinned &&
            ReviewConvergencePolicy.RequiresConvergenceGate(iteration);

        ReviewIterationRecord record = new(
            Guid.NewGuid(), sprintId, new(nodeId), dimension, reviewerKind, iteration, outcome, externalKeys,
            coverage, clock.UtcNow);
        try
        {
            await store.SaveReviewIterationAsync(projectRoot, record, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new(false, null, DiagnosticCodes.WorkflowRecordInvalid);
        }

        // Blocked *before* any finding is recorded below, deliberately: an at-or-above-floor
        // finding recorded via `RecordFindingAsync` can itself block a `ReadyToFinalize` sprint
        // with reason `finding` — the one reason that recovers automatically once every open
        // finding clears. If that ran first, `TryBlockSprintAsync` below would find the sprint
        // already `Blocked` and no-op, leaving the durable reason `finding` instead of
        // `review_convergence` — silently laundering a gate that requires an explicit operator
        // decision into one that resolving the findings alone would clear.
        bool convergenceBlockLanded = true;
        if (exceedsLimit || repeated)
        {
            // `TryBlockSprintAsync` itself only appends from `Running`/`ReadyToFinalize` — if the
            // sprint is *already* `Blocked` for some other, possibly auto-recovering reason (e.g.
            // a still-open finding from an earlier, unresolved call), it would otherwise return
            // `true` as a harmless-looking no-op, silently discarding this trigger instead of
            // durably recording it. `Blocked -> Blocked` is not a legal sprint transition (this
            // class's `IsLegalTransition` check would reject it), so there is no way to durably
            // re-tag the block reason while already blocked. Checked explicitly here so that case
            // reports a real failure instead of a false success — see ADR 0015's "known
            // limitation" note for what a complete fix would need.
            SprintWorkflowState currentState = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false);
            convergenceBlockLanded = currentState.Sprint.State is SprintState.Running or SprintState.ReadyToFinalize &&
                await TryBlockSprintAsync(projectRoot, sprintId, BlockedByReviewConvergence, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (outcome == ReviewOutcome.ChangesRequested)
        {
            FindingSeverity floor = floorPinned
                ? FindingSeverity.Critical
                : ReviewConvergencePolicy.SeverityFloorFor(iteration);
            foreach (ReviewFindingDraft finding in findings)
            {
                if (ReviewConvergencePolicy.IsAtOrAboveFloor(finding.Severity, floor))
                {
                    RecordFindingResult recorded = await RecordFindingAsync(
                        projectRoot, sprintId, finding.Severity, finding.MessageKey, finding.Arguments,
                        finding.Evidence, finding.Location, cancellationToken).ConfigureAwait(false);
                    if (!recorded.Succeeded)
                    {
                        // The iteration record is already durable (it governs eligibility/counting
                        // regardless), but a finding this verdict was supposed to leave open never
                        // landed — that must not be reported as a clean success.
                        return new(false, record, recorded.DiagnosticCode);
                    }
                }
                else
                {
                    try
                    {
                        await store.SaveFindingAsync(
                            projectRoot,
                            new(
                                Guid.NewGuid(), sprintId, Fingerprint(sprintId, finding.MessageKey, finding.Evidence),
                                finding.Severity, FindingStatus.Dismissed, finding.MessageKey, finding.Arguments,
                                finding.Evidence, finding.Location),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        return new(false, record, DiagnosticCodes.WorkflowRecordInvalid);
                    }
                }
            }
        }

        if (exceedsLimit || repeated)
        {
            if (!convergenceBlockLanded)
            {
                return new(false, record, DiagnosticCodes.WorkflowEventConflict);
            }

            return new(
                true, record, exceedsLimit ? DiagnosticCodes.ReviewIterationLimit : DiagnosticCodes.ReviewRepeatedFindings);
        }

        return new(true, record, DiagnosticCodes.None);
    }

    public Task<IReadOnlyList<ReviewIterationRecord>> GetReviewIterationsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken) =>
        store.GetReviewIterationsAsync(projectRoot, sprintId, cancellationToken);

    /// <summary>
    /// Pins <paramref name="dimension"/>'s severity floor at <see cref="FindingSeverity.Critical"/>
    /// from now on — the operator's "continue" choice at a review-convergence human gate. ADR
    /// 0006: "User-approved continuation keeps the counter and pins the floor at critical; it never
    /// resets or re-admits lower severities" — a one-way marker, never revoked, and deliberately
    /// not itself a sprint resume: the operator still issues an explicit `resume_sprint`/
    /// `run_sprint`, same as every other blocked-sprint recovery. "Accept current findings" needs
    /// no new capability here — a caller already has <see cref="ResolveFindingAsync"/> for that;
    /// "abort" is exactly <c>SprintOrchestrator.CancelSprintAsync</c>.
    /// </summary>
    public async Task<NodeActionResult> PinReviewFloorAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        ReviewDimension dimension,
        CancellationToken cancellationToken)
    {
        SprintDefinition definition = await RequireDefinitionAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        NodeDefinition? definedNode = definition.Graph.FirstOrDefault(item => item.Id == nodeId);
        if (definedNode is null)
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        if (definedNode.Role != NodeRole.Review)
        {
            return new(false, null, DiagnosticCodes.NodeKindMismatch);
        }

        await store.SetReviewFloorPinnedAsync(projectRoot, sprintId, nodeId, dimension, cancellationToken)
            .ConfigureAwait(false);
        SprintWorkflowState state = await RequireStateAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        return new(true, state.Nodes.GetValueOrDefault(nodeId), DiagnosticCodes.None);
    }

    /// <summary>
    /// True once every <see cref="NodeRole.Confirmation"/>-role dependency of <paramref name="node"/>
    /// has every artifact recorded at its own latest <see cref="ConfirmationArtifact.RecordedAt"/>
    /// — there can be more than one on an exact tie — carrying an outcome of
    /// <see cref="ConfirmationOutcome.Confirmed"/>, the plan's "only its valid artifact makes
    /// recorded risk-based test selection and authoring eligible." Using only the latest instant
    /// per node (never "any `Confirmed` artifact ever recorded") is what lets a later
    /// `NotConfirmed` re-close a gate an earlier `Confirmed` opened; requiring *every* artifact at
    /// that instant to be `Confirmed`, rather than picking one arbitrarily, is what keeps a tie
    /// failing closed instead of depending on an unspecified ordering. A node with no
    /// confirmation-role dependency (not the built-in graph's shape, but nothing stops a
    /// caller-supplied one) is vacuously eligible: there is nothing to gate on.
    /// </summary>
    private async Task<bool> IsTestWorkEligibleAsync(
        string projectRoot,
        SprintId sprintId,
        SprintDefinition definition,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> confirmationDependencies = [.. node.DependsOn.Where(dependency =>
            definition.Graph.FirstOrDefault(candidate => candidate.Id == dependency)?.Role == NodeRole.Confirmation)];
        if (confirmationDependencies.Count == 0)
        {
            return true;
        }

        IReadOnlyList<ConfirmationArtifact> confirmations =
            await store.GetConfirmationsAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        return confirmationDependencies.All(dependency =>
        {
            List<ConfirmationArtifact> forNode = [.. confirmations.Where(artifact => artifact.NodeId.Value == dependency)];
            if (forNode.Count == 0)
            {
                return false;
            }

            // Fails closed on a tie: `RecordedAt` comes from `IClock.UtcNow`, whose resolution is
            // not guaranteed finer than two calls made moments apart (and the API is documented as
            // replayable, so a tie is reachable in practice, not just in theory). Picking a single
            // "latest" via ordering alone would make the winner depend on `GetConfirmationsAsync`'s
            // enumeration order for ties — undefined here, since confirmations are stored one file
            // per artifact under random ids. Requiring every artifact at the max `RecordedAt` to be
            // `Confirmed` means a tied `NotConfirmed` always wins the gate, never loses it.
            DateTimeOffset latestRecordedAt = forNode.Max(artifact => artifact.RecordedAt);
            return forNode
                .Where(artifact => artifact.RecordedAt == latestRecordedAt)
                .All(artifact => artifact.Outcome == ConfirmationOutcome.Confirmed);
        });
    }

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
                state.Sprint.Version, Guid.NewGuid(), cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [WorkflowEvent.BlockedReasonArgument] = BlockedByGate,
                }).ConfigureAwait(false);
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
