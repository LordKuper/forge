# ADR 0018: Rate-limit deferral and attempt supersession

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.48-P11.55) must implement
durable rate-limit deferral and human-only attempt supersession, with
confirmation, version, idempotency, bounded instruction, cancellation,
worktree discard, linkage, and clean replacement.

ADR 0006, quoted for the parts this item makes concrete:

> "A retryable rate limit abandons the failed attempt, records a safe
> `resume_not_before` from structured provider metadata or the frozen
> fallback policy, releases its executor slot, and leaves the node ready but
> routing-deferred... Repeated deferral cannot spin or bypass the sprint
> retry budget."
>
> "An operator may explicitly supersede a non-terminal attempt. The command
> requires confirmation, expected state version, idempotency key, target
> attempt id, and a bounded instruction artifact. Forge cancels the process
> tree, discards the owned worktree, records `AttemptSuperseded`, and
> creates a fresh attempt for the same node from the superseded attempt's
> recorded base... Agents and generated integrations cannot invoke this
> human-only command."

`RoutingLedger` (built in Stage 8, P8.42-P8.47) already computes
`resume_not_before` and records `RouteDecision`s, but had zero production
callers before this item. `docs/contracts/v1/capabilities.json` already
reserves `attempt.supersede` with events `["AttemptSuperseded",
"AttemptChanged"]`. As with every prior Stage 11 item, no node executor
exists yet, so both features are built as scheduler-level primitives for a
future executor and a future CLI/TUI command to call.

## Decisions

### `StartAttemptAsync` consults the routing ledger before arming a node

For roles with an `ExecutionProfile` (`ExecutionProfilePolicy.PhaseFor`
returns non-null for Planning/Implementation/Review only), `StartAttemptAsync`
calls `RoutingLedger.DecideAsync` keyed by `(Provider, Model, "batch")`
before transitioning the node to `Running`. A non-`Routed` outcome
(`Deferred`/`BudgetExhausted`/`CircuitOpen`) fails the call with the matching
new `DiagnosticCodes.Routing*` code instead of starting the attempt; nodes
with no execution profile (Intake, Confirmation, TestWork, Finalization,
HumanApproval, Generic) skip the check entirely, matching how they were
already exempt from every other profile-driven behavior.

### `DeferAttemptAsync` is the rate-limit-abandonment path

New `SprintScheduler.DeferAttemptAsync(projectRoot, sprintId, nodeId,
attemptId, inputDigest, cancellationToken)`: finds the `Routed`
`RouteDecision` this attempt actually consumed (via
`RoutingLedger.GetRouteDecisionsAsync` + last-match on attempt id — no
decision found means the caller is deferring an attempt that was never
routed through the ledger, a caller error surfaced as
`WorkflowEventConflict`), calls `RoutingLedger.RecordDeferralAsync` with
`clock.UtcNow + DefaultRateLimitBackoff` (one minute — see "no structured
provider metadata" below), then delegates to the existing
`CompleteAttemptAsync(succeeded: false, ...)` with a
`ProviderDiagnosticCodes.RateLimited` diagnostic. It does not invent new
node/attempt transition logic: "abandons the failed attempt... leaves the
node ready but routing-deferred" is exactly what an ordinary failed
completion already does; deferral only adds the ledger bookkeeping ordinary
failure does not need. `RecordDeferralAsync` only accepts a decision whose
`Outcome == Routed` and never refunds the shared retry budget it already
consumed — "repeated deferral cannot spin or bypass the sprint retry budget"
holds because a deferred attempt consumes the same shared per-sprint budget
`DecideAsync` already enforces for every other failure, with no separate
allowance.

No structured provider metadata parser was added: no vendor JSON field name
for a rate-limit reset time is verified anywhere in this codebase (the same
discipline already applied to `DefaultModel` and provider authentication
variable names earlier in Stage 11), so this item ships only the "frozen
fallback policy" half of ADR 0006's `resume_not_before` sentence —
`SprintScheduler.DefaultRateLimitBackoff = TimeSpan.FromMinutes(1)`. Reading
a real per-provider reset time from provider output is deferred to whichever
future executor actually parses provider responses.

### `SupersedeAttemptAsync` cancels, links, and re-arms in one call

New `SprintScheduler.SupersedeAttemptAsync(projectRoot, sprintId, attemptId,
expectedAttemptVersion, idempotencyKey, confirmed, instruction,
cancellationToken)` mirrors the four required inputs literally:
`confirmed` must be `true` (`DiagnosticCodes.ConfirmationRequired`
otherwise), `expectedAttemptVersion`/`idempotencyKey` are checked against the
target attempt exactly like every other scheduler mutation, and `instruction`
is bounded to `MaxSupersessionInstructionLength` (4000 characters —
`DiagnosticCodes.SupersessionInstructionTooLong` otherwise). A terminal
target attempt is rejected (`DiagnosticCodes.AttemptTerminal`); the target
must resolve to a known node (`DiagnosticCodes.NodeNotFound` otherwise).

On success it: transitions the attempt to `Cancelled` (new `AttemptChanged`
record, message key `workflow.attempt_superseded`); appends a new
non-transition `AttemptSuperseded` event carrying the bounded instruction
(new `ISprintStore.AppendAttemptSupersededAsync`, mirroring
`AppendAttemptActivityAsync`'s own pattern — validated in
`WorkflowFold.IsTransitionRecord` to require the instruction argument and
never itself projected into a snapshot field, matching how
`AttemptActivityRecordedType` is handled); creates a fresh attempt at the
node's next deterministic attempt id, carrying `SupersedesAttemptId` (the
new linkage) and, when the superseded attempt recorded one, the same
`BaseCommit` ("creates a fresh attempt for the same node from the superseded
attempt's recorded base"); and re-arms the node from `Running` back to
`Ready` so an ordinary later `StartAttemptAsync` call picks the fresh
attempt up.

The two new `AttemptSnapshot` fields (`BaseCommit`, `SupersedesAttemptId`)
are populated only by this path — every other attempt-creation call leaves
both `null`, matching the ADR's framing of them as supersession-specific
linkage, not general attempt metadata.

### Node re-arming takes the two-step path the state machine actually has

The node state machine has no direct `Running -> Ready` edge — only
`Running -> {AwaitingHuman, Succeeded, Failed, Cancelled}` and separately
`Failed -> Ready` (`WorkflowStateMachines`). `SupersedeAttemptAsync`
therefore re-arms in two independently-gated steps, each checked against the
node's *current* state rather than a single boolean flag: `Running ->
Failed` (message key `workflow.node_superseded`) only if the node is still
`Running`, then `Failed -> Ready` (`workflow.node_retried`) only if it is
now `Failed`. Gating on current state rather than "did we just do the first
step" means a retry resumed after a crash between the two transitions
finishes only whichever one is still outstanding, instead of re-attempting
(and illegally re-transitioning) a step already durably recorded. This was
found by a failing test (`workflow_transition_invalid` on a direct
`Running -> Ready` attempt) before it could reach a real caller.

### Idempotent replay is recognized by outcome, not by a since-advanced version

Because `SupersedeAttemptAsync` reads the target attempt fresh on every
call, a genuine replay (same `idempotencyKey`, same caller-supplied
`expectedAttemptVersion`) observes an attempt that has already legitimately
moved to `Cancelled` — one version past what the caller's original
pre-supersession snapshot expected. Checking the caller's stale
`expectedAttemptVersion` against that already-advanced state before
recognizing the replay would reject a genuine retry with `SuggestionStale`,
defeating resumability. The fix, mirroring `ResolveHumanGateAsync`'s own
documented pattern: an `alreadySuperseded` check (target attempt is
`Cancelled` and some other attempt records it as `SupersedesAttemptId`) runs
*before* the version/terminal checks; when true, those checks are skipped
entirely and the call falls straight through to the store's own
idempotency-key-based replay detection in `AppendTransitionAsync`, which
correctly short-circuits the cancel transition as already-applied. This was
also found by a failing test before it could reach a real caller.

### Deliberately deferred

- **Process-tree cancellation and worktree discard.** ADR 0006 says
  supersession "cancels the process tree, discards the owned worktree."
  Neither happens here: `SprintGitIsolation` (Stage 7) explicitly documents
  itself as holding no executor ("what a Work node's attempt actually does
  inside its worktree... is Stage 11's job; this class only creates,
  integrates, rebases, and discards the worktrees such an executor will
  use"), and no executor anywhere in the repo yet holds a live process tree
  or a live worktree handle to cancel or discard — the same gap every prior
  Stage 11 item has deferred to the same future executor. `SupersedeAttemptAsync`
  performs every part of supersession that is pure workflow-state
  bookkeeping; wiring in the live-resource cleanup is that executor's job
  once it exists.
- **Human-only enforcement.** "Agents and generated integrations cannot
  invoke this human-only command" is a caller-surface restriction (which
  command surfaces expose `attempt.supersede` and to whom), not a scheduler
  concern — `SprintScheduler` exposes the primitive; P11.56-P11.66 (CLI/TUI
  commands) is where a human-only gate on this specific command belongs.
- **`RecordOutcomeAsync`/circuit-breaker wiring for ordinary completions.**
  `StartAttemptAsync` only calls `RoutingLedger.DecideAsync` (budget/circuit
  admission) and `DeferAttemptAsync` only calls `RecordDeferralAsync`.
  Recording ordinary success/failure outcomes into the ledger's health
  tracking is a separate concern from rate-limit deferral and was not
  required to make deferral itself durable or budget-bounded.

## Consequences

- `RoutingLedger` (built and untested-in-production since Stage 8) now has
  its first production caller; rate-limit deferral is durable and
  budget-bounded end to end, closing the P8.42-P8.47 re-scope note.
- A future node executor and a future human-only CLI/TUI command each have a
  ready-made, already-tested scheduler primitive to call
  (`DeferAttemptAsync`, `SupersedeAttemptAsync`) — no workflow-state
  bookkeeping left to invent when those callers are finally built.
- Live-resource cleanup (process tree, worktree) on supersession remains
  unimplemented until the executor exists, same posture as every deferred
  item in ADR 0017.
- `attempt.supersede`'s reserved event pair (`AttemptSuperseded`,
  `AttemptChanged`) is now implemented exactly as reserved.

## References

- ADR 0006 (supervised execution and durable rate-limit/steering)
- ADR 0007 (cross-platform core and minimal OS adapters — worktree/process
  boundary this item does not cross)
- ADR 0014 (frozen execution profiles — the provider/model this item routes)
- ADR 0016 (provider stdin/environment/streaming)
- ADR 0017 (attempt deadlines — the prior item's `Running`-state-machine and
  resumability precedents this item reuses)
