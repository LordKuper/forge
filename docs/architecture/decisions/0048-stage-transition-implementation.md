# ADR 0048: Stage-transition implementation

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.8.0, state-machines.json 1.3.0

## Context

ADR 0045 recorded `StageRevision`/`SupersededBy` as inert, unattached contract shapes. ADR 0046
recorded the ten prerequisite categories `AssessStageTransition` must evaluate and reserved
`workflow.assess_stage_transition`/`sprint.move_stage` without an evaluator or coordinator. Both
explicitly deferred every real mechanism to Slice 3. This ADR records the decisions that
implementation actually had to make.

## Decisions

### Reopening a node is a new legal edge, not a second aggregate

`Succeeded` had no outgoing edges in the frozen `node` machine — the JSON contract's own `terminal`
list named it explicitly. Reopening it for a rewind therefore required extending
`WorkflowStateMachines.Node` and `state-machines.json` (1.2.0 → 1.3.0) with `succeeded -> ready`,
`succeeded -> pending`, and `failed -> pending`, mirroring ADR 0044's own precedent for
`Paused`/`Validating -> Cancelled`: new edges reached only through `StageTransitionCoordinator`,
never a generic public API. (An `awaiting_human -> pending` edge was considered for the same reason
but dropped — round 1 review of PR #96 found no caller ever needed it, since a rewound
`AwaitingHuman` downstream node is always walked through the already-legal `awaiting_human -> failed
-> pending` two-hop instead.) `Succeeded` is no longer contract-terminal; nothing in this codebase
derives node terminality from this table at all — `WorkflowStateMachines` exposes `IsTerminal` only
for the sprint and attempt machines, and `state-machines.json`'s own `node.terminal` list (unaffected
by this change: it never named `Succeeded`) is the only place node terminality is ever consulted. The
alternative — versioning node identity itself, or cloning the sprint — was
rejected exactly as ADR 0045 anticipated: node identity stays stable, only its `NodeSnapshot.Revision`
changes, tracked as a new folded argument (`WorkflowEvent.RevisionArgument`) on these same
transitions, with `AttemptCount` reset to 0 (a genuinely fresh retry budget for a reopened stage).

Two edges exist for `Succeeded`/`Failed` because two different callers need different targets: the
rewind's own target stage goes straight to `ready` (its upstream is untouched and already
satisfied); every node strictly downstream of it goes to `pending` instead, so
`SprintScheduler.AdvanceGraphAsync`'s ordinary dependency-satisfaction check — not a duplicated one —
decides when it becomes eligible again once the target actually re-succeeds. Jumping straight to
`ready` for a downstream node would let it start before its real (also-reset) predecessor reruns,
violating "never fabricates completion" the same way an unchecked advance would.

### `StageRevisionRecorded` is a non-transition sprint event, folded like `AttemptStopRequested`

A rewind's sprint-level effect (the revision counter) is orthogonal to the sprint's own state
machine — a rewind can commit while the sprint is `running`, `paused`, `blocked`, `failed`,
`awaiting_human`, or `ready_to_finalize`, none of which is itself a state the revision bump should
force. `WorkflowEvent.StageRevisionRecordedType` is therefore a same-shape sibling of
`AttemptStopRequestedType`/`AttemptSupersededType`: no `to_state`, appended on the sprint aggregate at
its *current* version (never incrementing it), and folded directly into `SprintSnapshot.Revision`
(not audit-only) since every prerequisite check and node-role executor must read a sprint's current
revision without re-scanning the raw journal.

### Idempotent replay is reported only once the whole saga converges, not once step 2 lands

`ISprintStore.AppendStageRevisionRecordedAsync` deduplicates its own append against the caller's
idempotency key, reusing the exact durable dictionary `AppendTransitionAsync` already maintains
(`idempotency.json`) — a sprint can be legitimately rewound many times over its life, so the stop
coordinator's own "has this event type ever landed for this aggregate" scan (ADR 0047) would
wrongly block every rewind after the first; keying on the caller's own key instead lets a genuine
replay short-circuit while a distinct later rewind still lands.

Round 1 review of PR #96 (finding 1) found that this ledger entry alone is not a safe *outer* replay
signal: `AppendStageRevisionRecordedAsync` is only step 2 of the six-step rewind saga (evidence
supersession, node reopen/invalidate, graph re-advance, and the sprint-ready walk are steps 3-6), so
a crash in that window left `MoveAsync`'s original outer check — keyed on the same raw ledger —
reporting success for a rewind that had only bumped the revision counter and touched nothing else.
The fix adds a dedicated completion marker, `WorkflowEvent.StageTransitionConvergedType`
(`ISprintStore.AppendStageTransitionConvergedAsync`), appended once as the *last*, unconditional step
of a successful `CommitAdvanceAsync`/`CommitRewindAsync`, mirroring `AttemptStopConvergedType`'s own
role for the stop saga. `MoveAsync`'s outer replay check
(`ISprintStore.TryGetConvergedStageTransitionAsync`) now keys on this marker instead of the raw
ledger, so it reports success only once the entire saga has actually finished. A retry that lands
between step 2 and the last step is not lost: because every one of `CommitRewindAsync`'s own steps
was already designed to be independently idempotent (`AppendStageRevisionRecordedAsync`'s own inner
replay branch resumes into steps 3-6 rather than re-incrementing the revision), a caller that
re-assesses (getting a fresh, non-stale token) and calls `MoveSprintToStage` again with the *same*
idempotency key re-enters the commit and converges correctly; a caller that instead resubmits its
original, now-stale token is safely rejected as stale rather than told the half-finished commit
already succeeded. This also fixed a companion defect (finding 4): `CommitAdvanceAsync` never
recorded the caller's key at all, so a replayed advance returned `suggestion_stale` instead of the
original result — it now appends the same completion marker on success.

`StageTransitionCoordinator.MoveAsync` still checks for this marker first, unconditionally, before
any fresh assessment: by the time a rewind has already committed once, the target node is no longer
at the terminal outcome that made it a rewind, so re-deriving direction from current state on a
replay would no longer even classify the call as the same operation.

### "Current stage" and transition direction are derived from node state, not a topological rank

Computing a fragile total order over a graph the plan explicitly requires to "remain valid if a
future workflow contains parallel nodes" was rejected. Instead: the current stage is the node
currently `running`/`awaiting_human` (the active frontier), or failing that the furthest node already
`succeeded`/`skipped` in the frozen graph's own declaration order, or failing that the graph's first
node. Direction needs no rank comparison at all: a target whose own `NodeState` is already
`succeeded`/`failed`/`skipped`/`cancelled` is `rewind` (it already reached an outcome once); a target
still `pending`/`ready` is `advance`; the frontier node itself is `same`. This generalizes correctly
to a parallel DAG without guessing at an ordering it does not have.

### The finding-severity prerequisite reuses the completion gate's own binary rule, not a new policy

ADR 0046's category 5 ("no unresolved finding violates the target stage's severity policy") reads as
if a per-stage severity threshold exists to check against. None does: the only finding policy this
codebase has ever had is `SprintScheduler.EvaluateCompletionAsync`'s binary rule — any `Open` finding
blocks, regardless of severity; `Resolved`/`Accepted`/`Dismissed` never do.
`StagePrerequisiteIds.NoBlockingFindings` reuses exactly that rule (extended only to exclude
superseded findings), rather than inventing a new per-target-stage severity-threshold policy this
slice would then own alone with no other caller. Introducing a real per-severity gate belongs to
whatever future work first needs one, not to a prerequisite evaluator whose job is to reuse existing
policy, not author new policy.

### Only advance is gated by the advance-shaped prerequisites; rewind is its own escape hatch

Round 1 review of PR #96 (finding 3): `NoBlockingFindings`/`ProviderModelPolicy`/`GitIsolation`/
`RetryBudget` were originally evaluated for both directions, which made a rewind impossible in
exactly the states it exists to recover from — an open finding, a dirty integration worktree, an
exhausted retry budget, or a since-tightened model policy are all conditions a rewind is the remedy
for, not a reason to refuse one. Plan section 8.2's prerequisite list is written for activating an
advance target; section 8.4's rewind list names only a bounded reason, mandatory confirmation, and
the mechanical stop/revision/supersession machinery. `StageTransitionAssessor.AssessAsync` now scopes
all four (joining the already direction-scoped `NoActiveOperation`) to `direction == Advance`; a
rewind's own supersession is what actually resolves a blocking finding, so gating the rewind on it
first would be circular.

### The rewind reason is length-bounded like every other operator-authored artifact

Round 1 review of PR #96 (finding 5): plan section 8.4 calls the rewind reason "bounded", but only
non-empty was enforced. Reuses `SprintScheduler.MaxSupersessionInstructionLength` (4000) and
`DiagnosticCodes.SupersessionInstructionTooLong` — the same limit and diagnostic ADR 0006 already
established for the equivalent human-authored bounded artifact (an attempt-supersession
instruction) — rather than a second, drifting rule.

### Step 1 stops every live downstream operation, not only the first one found

Round 1 review of PR #96 (finding 2): a parallel DAG can have more than one node `Running` at once
(ADR 0048's own "generalizes correctly to a parallel DAG" claim above), but step 1 originally stopped
only `state.Nodes.Values.FirstOrDefault(...)` — anywhere in the sprint, not even scoped to the
rewind's own downstream closure — leaving every other running branch untouched and its node
`ReopenOrInvalidateNodeAsync` never visits (`Running` fell through that method's state walk
silently). Step 1 now computes the downstream closure first and stops every node in it that is still
`Running`. The stop itself is deliberately narrower than
`StopOperationCoordinator.FinishStopAsync`: that method's unconditional re-arm to `Ready` and
sprint-pause are correct for an ad-hoc manual stop, but `Ready` has no legal edge back to `Pending`
in the frozen node machine, so a stopped downstream node instead lands on `Failed` (via the same
`workflow.node_rewind_interrupted` message key the `AwaitingHuman` branch already uses) and falls
through into the existing `Succeeded`/`Failed` reopen/invalidate branch, which already knows how to
reach `Pending` with a fresh revision stamp and reset retry budget. `ReopenOrInvalidateNodeAsync`
also gained its own defensive `Running` branch performing the identical stop-and-fail sequence, so a
node step 1 could not converge before a crash (or a resumed retry) is never left stranded when step
4 reaches it.

### Advance's skip-ahead reuses `SprintScheduler.SkipNodeAsync`, gated on the frozen `Optional` flag

`NodeDefinition.Optional` (new, default `false`, additive) is the plan's own "explicitly optional in
the frozen workflow" (section 8.3) — nothing in the built-in `implementation-critical` graph sets it,
but a custom graph now can. `StageTransitionAssessor.CollectTransitivePredecessors` computes the
*required* (non-optional) transitive closure for the `Allowed` gate, but always keeps traversing
*through* an excluded optional node to its own upstream predecessors — an optional node being
skipped does not excuse whatever came before it. `StageTransitionCoordinator`'s advance commit walks
the *full* closure (optional included) and calls the existing `SkipNodeAsync` for any optional node
still `pending`/`ready`, then lets `AdvanceGraphAsync` — not a duplicated promotion loop — do the
rest. A mandatory node found unmet at commit time (a race since the assessment was read) is refused
outright, never skipped: "never marks a mandatory stage as skipped" is enforced structurally, not by
trusting the pre-read assessment alone.

### Assessment tokens are bound to the sprint's whole journal position, not its own aggregate version

`StageTransitionAssessment.ExpectedStateVersion` is `SprintWorkflowState.LastSequence` (the sprint's
entire event-journal position across sprint/node/attempt aggregates), not
`SprintSnapshot.Version` (which only advances on a *sprint*-aggregate transition). A node or attempt
completing — exactly the kind of change that flips a prerequisite this exact assessment already
evaluated — never bumps the latter, which would have let a genuinely stale assessment token pass the
staleness check. `AppendStageRevisionRecordedAsync`'s own `expectedSprintVersion` parameter still
uses the narrower `SprintSnapshot.Version`, correctly: that one gates a low-level optimistic-
concurrency append on the sprint aggregate specifically, a different concern from "has anything a
caller could have observed changed since."

### Supersession scope: the full downstream closure, plus unattributed findings

A rewind supersedes the target's evidence and every node's evidence reachable *forward* from it
(`StageTransitionAssessor.CollectDownstreamClosure`, the reverse of the predecessor closure) —
`NodeResult`, `Handoff`, `ConfirmationArtifact`, and `TestWorkArtifact` all carry a `NodeId` already,
so this is exact. `Finding` does not: only `SprintScheduler.RecordReviewIterationAsync` currently has
a node in scope when it raises one (threaded through a new optional `Finding.NodeId`); every other
recorded finding carries none. An unattributed finding is superseded unconditionally by any rewind —
the conservative direction, since failing to supersede a finding that genuinely concerned rewound
work would let stale evidence wrongly keep satisfying (or blocking) a prerequisite, while over-
superseding only means a resolved-but-superseded finding stops mattering, which a rewind's own
"start the downstream work over" intent already implies.

### Evidence-kind supersession marking is a dedicated store rewrite, not a reuse of `Save*Async`

`FileSprintEventLog.SaveNodeResultAsync` is write-once by design (a different content for the same
attempt id throws). Marking a result superseded is a real, narrow exception to "never rewritten"
(ADR 0045 itself: "carrying this marker is never deleted or rewritten [beyond adding the marker
itself]") — five new `Mark*SupersededAsync` methods read the persisted file, set two fields if not
already set, and rewrite it, each independently idempotent (a no-op if already marked) so a crash
mid-supersession-loop converges cleanly on retry.

### Deviation from the slice's task description: neither capability enters `CapabilityIds.Implemented`

Following ADR 0047's own precedent for `workflow.stop_operation`: `capabilities.json`'s
`public_requires_both_surfaces` rule and `SurfaceParityTests`' `DesktopControls` dictionary (indexed
unconditionally by `CapabilityIds.Implemented`) would throw for a capability with no Desktop control,
which Desktop parity (Slice 6) intentionally does not ship yet. Both `capabilities.json` entries'
`note` fields were updated to "implemented on Host and CLI... Desktop parity deferred," matching
`workflow.stop_operation`'s own wording exactly. No `CapabilityIds` consts were added either, for the
same reason ADR 0047 added none. Two new `SurfaceParityTests` (mirroring
`StopOperationDocumentedCliOptionsMatchTheirActualRequiredness`) close the CLI-option-requiredness
gap `CliExposesEveryDocumentedCapabilityCommand` cannot reach for a reserved capability.

### `AssessStageTransition` is Host-dispatched but the CLI reads it locally

The query has its own `ControlProtocol` kind and `ControlPlaneHostedService` dispatch case (for a
future Desktop client, which always talks over the wire), but `forge sprint assess-stage`/
`move-stage` call `ForgeApplication.AssessStageTransitionAsync` directly — the same "queries run
against the durable event log directly" convention every other CLI read command in this file already
uses (e.g. `forge sprint inspect`). This is safe because the durable, file-based journal is the sole
source of truth regardless of whether a separate Host process is also running; nothing about a stage
assessment depends on a Host's own in-memory state.

## What stays deferred

- `WorkflowStateMachines.Node`'s `awaiting_human`/`succeeded`/`failed` reopen/invalidate edges never
  resurrect an explicitly `skipped` or `cancelled` downstream node — left as the operator's own prior
  decision, out of scope for an automatic rewind reset.
- Review-iteration counting (`ReviewConvergencePolicy`'s severity-floor progression) and the review-
  floor-pin marker are not revision-scoped: a rewound review node's iteration count keeps climbing
  from where it left off rather than restarting at 1. This is a deliberately conservative
  simplification, not a safety gap — a higher iteration count only ever raises the required severity
  floor, never lowers it, and a floor already pinned at `critical` stays pinned. Revisit only if this
  proves a real UX problem, not a correctness one.
- `HandoffArtifacts`'/digest-resolution prerequisite checks presence and supersession, not real
  content-addressed resolution — `IArtifactStore` remains an empty marker; nothing in this codebase
  produces a real artifact yet.
- Desktop `SprintWorkspace/AssessStageTransition`/`SprintWorkspace/MoveStage` UI and
  `CapabilityIds.WorkflowAssessStageTransition`/`CapabilityIds.SprintMoveStage` (a later slice,
  matching ADR 0037/0047's CLI-first rhythm).
- `AssessStageTransition`'s Host-dispatched protocol path has no exercised caller yet (the CLI reads
  locally); it exists for the Desktop client a later slice adds.

## Consequences

- `NodeResult`, `Finding`, `Handoff`, `ConfirmationArtifact`, and `TestWorkArtifact` all gain
  `Revision`/`Superseded` fields and matching optional wire-schema properties
  (`revision`/`superseded_at_revision`/`superseded_at`); `Finding` additionally gains an optional
  `node_id`. Every schema stays additive (`additionalProperties: false` unchanged for required
  fields) — no migration needed for existing `.forge/` directories, which simply read `revision: 0`
  and no superseded marker.
- `state-machines.json` moved from 1.2.0 to 1.3.0 (three new node edges, `succeeded` no longer
  terminal); `WorkflowContractTests.NodeTransitionsMatchFrozenV1Contract` was extended, not weakened
  — the one previously-`false` case (`succeeded -> ready`) is now `true`, an intentional contract
  change this ADR records. (An `awaiting_human -> pending` edge was added and then removed within
  this same slice, round 1 review of PR #96 — see above.)
- `capabilities.json` moved from 1.7.0 to 1.8.0 (both reserved entries' notes updated to
  "implemented, Desktop deferred").
- `ISprintStore` gains eight members (`TryGetConvergedStageTransitionAsync`,
  `AppendStageTransitionConvergedAsync`, `AppendStageRevisionRecordedAsync`, five
  `Mark*SupersededAsync` methods); `FileSprintEventLog`, `FlakySprintStore`, and every test fake
  implementing the interface directly were updated to match. (The originally-shipped
  `TryGetIdempotentReplayAsync` was replaced by `TryGetConvergedStageTransitionAsync` in round 1
  review — see "Idempotent replay is reported only once the whole saga converges" above.)
- `IForgeMutations` gains `MoveSprintToStageAsync`; `RemoteForgeMutations` and both
  `TestEnvironment.cs` fakes implement it. `AssessStageTransitionAsync` is a plain `ForgeApplication`
  method, not part of `IForgeMutations`, matching every other query in this codebase.

## References

- Plan section 8 (move sprint to another workflow stage), section 12.5 (acceptance criteria)
- ADR 0045 (stage revision model this slice attaches for real)
- ADR 0046 (prerequisite policy this slice's evaluator implements in full)
- ADR 0044/0047 (state-machine extension and idempotent-saga precedents this slice follows)
