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

An independent review round found that consulting the ledger on *every*
attempt start — including a node's very first one — turns
`RoutingLedger.DefaultRetryBudget` into an unrecoverable lifetime cap on
ordinary sprint progress: `BuildBudget` (Stage 8's own tested contract)
consumes one unit per `Routed` decision and had refunded only `Excluded`
(auth/policy) failures, never a success. A sprint with more model-bearing
nodes and review iterations than the budget has units — entirely ordinary
usage — would permanently exhaust it with no caller wiring left to clear it,
since ADR 0006 frames the budget around bounding retry/deferral loops, not
capping total one-pass throughput. The fix keeps the ledger consulted on
every attempt (so `DeferAttemptAsync` always has a `Routed` decision to defer
against, including on a first attempt) and instead has `CompleteAttemptAsync`
refund the unit on a genuinely new success (see below) — `BuildBudget` now
treats `Succeeded` the same as `Excluded`. Caught by a new regression test
(`CompleteAttemptAsyncRefundsTheRoutingBudgetOnSuccessSoOrdinaryProgressNeverExhaustsIt`)
before it could reach a real caller.

The final review round found one more way the same unit could go
unrefunded: `DecideAsync` commits the budget-consuming `Routed` decision
*before* the node transition it is meant to authorize. When that node
transition itself then fails — a stale pre-check, or a genuine race against
a concurrent caller — the consumed unit represents work that never actually
happened, and nothing refunded it; a `StartAttemptAsync` recorded a chain of
`Routed` decisions for repeated conflicts on the same node, and only a
`Succeeded` (or `Excluded`) decision the sprint could no longer reach would
ever have refunded any of them. Fixed the same way `CompleteAttemptAsync`
already refunds a real success: on a failed node transition,
`RoutingLedger.RecordOutcomeAsync(decision, succeeded: true, ...)` refunds
the unit immediately before returning the failure.

### `DeferAttemptAsync` is the rate-limit-abandonment path

New `SprintScheduler.DeferAttemptAsync(projectRoot, sprintId, nodeId,
attemptId, inputDigest, cancellationToken)`: finds the `Routed`
`RouteDecision` this attempt actually consumed (via
`RoutingLedger.GetRouteDecisionsAsync` + last-match on attempt id — no
decision found means the caller is deferring an attempt that was never
routed through the ledger, a caller error surfaced as
`WorkflowEventConflict`), delegates to the existing
`CompleteAttemptAsync(succeeded: false, ...)` with a
`ProviderDiagnosticCodes.RateLimited` diagnostic, and only once that reports
success calls `RoutingLedger.RecordDeferralAsync` with `clock.UtcNow +
DefaultRateLimitBackoff` (one minute — see "no structured provider
metadata" below) — see "review also found" further down for why the
ordering matters. It does not invent new node/attempt transition logic:
"abandons the failed attempt... leaves the node ready but routing-deferred"
is exactly what an ordinary failed completion already does; deferral only
adds the ledger bookkeeping ordinary failure does not need.
`RecordDeferralAsync` only accepts a decision whose `Outcome == Routed` and
never refunds the shared retry budget it already consumed — "repeated
deferral cannot spin or bypass the sprint retry budget" holds because a
deferred attempt consumes the same shared per-sprint budget `DecideAsync`
already enforces for every other failure, with no separate allowance.

No structured provider metadata parser was added: no vendor JSON field name
for a rate-limit reset time is verified anywhere in this codebase (the same
discipline already applied to `DefaultModel` and provider authentication
variable names earlier in Stage 11), so this item ships only the "frozen
fallback policy" half of ADR 0006's `resume_not_before` sentence —
`SprintScheduler.DefaultRateLimitBackoff = TimeSpan.FromMinutes(1)`. Reading
a real per-provider reset time from provider output is deferred to whichever
future executor actually parses provider responses.

Review also found that `RecordDeferralAsync` ran *before* the
`CompleteAttemptAsync` call it rides on, so a completion that failed for a
reason `DeferAttemptAsync`'s own checks do not already cover (a node/attempt
version conflict, an illegal transition) still left the durable routing
block in place for an attempt that was never actually abandoned. Fixed by
recording the deferral only after `CompleteAttemptAsync` reports success,
covered by a new regression test
(`DeferAttemptAsyncDoesNotRecordADeferralWhenTheUnderlyingCompletionFails`
via `FlakySprintStore`).

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
`AttemptActivityRecordedType` is handled). An attempt can be superseded at
most once — it is terminal-`cancelled` by the same call — so
`AppendAttemptSupersededAsync` itself is idempotent by attempt id: a second
call for the same attempt is always a replay, recognized by whether an
`AttemptSuperseded` event for that attempt already exists, and skipped
outright rather than appended again. Review found the earlier, unconditional
append meant a replay carrying different instruction text (a caller bug, but
nothing prevented it) silently produced a second, contradictory record; the
durably recorded instruction is now always whichever one actually won the
race to append first — caught by a new regression test
(`SupersedeAttemptAsyncReplayWithDifferentInstructionTextKeepsTheOriginallyRecordedInstruction`).
It then creates a fresh attempt at the
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
documented pattern: an `alreadySuperseded` check runs *before* the
version/terminal checks; when true, those checks are skipped entirely and
the call falls straight through to the store's own idempotency-key-based
replay detection in `AppendTransitionAsync`, which correctly short-circuits
the cancel transition as already-applied (or legitimately rejects a
different, non-replay call against an already-`cancelled` attempt as an
illegal `cancelled -> cancelled` transition — either way, correctly). This
was found by a failing test before it could reach a real caller.

`alreadySuperseded` deliberately checks only `attempt.State ==
AttemptState.Cancelled`, not also "and a replacement attempt already
exists." A first review round used the stricter combined check; a second,
adversarial round found it still left a gap: a retry landing between the
cancel transition and replacement creation (the exact durable state a crash
right there would leave) saw the attempt already `Cancelled` but no
replacement yet, so the stricter check evaluated to false, fell into the
version/terminal pre-check, and was rejected with `SuggestionStale` against
a version the attempt had already legitimately moved past — the node stuck
`running` with a `cancelled` attempt and no way to finish short of aborting
the sprint. Checking `Cancelled` alone closes this: the append below is safe
to call unconditionally either way, since a genuine replay short-circuits
inside the store before any version check runs, and a call this permissive
check lets through for the wrong reason still lands on an illegal
transition there. Caught by a new regression test
(`SupersedeAttemptAsyncResumesAfterACrashBetweenCancellationAndReplacementCreation`).

### Superseded design attempts: reconstructing the replacement's id from a count

Three successive review rounds (2 through 4) each fixed a real bug in the
same idea — find "the" pending replacement, and tell whether it has
started, by recomputing a deterministic id from `currentNode.AttemptCount`
and comparing it to the replacement's own id. In order: recomputing the id
fresh on every call instead of caching it broke once `AttemptCount` moved
past the replacement's own number (an ordinary auto-retry, or a second
supersession) — fixed by finding the replacement through its
`SupersedesAttemptId` linkage instead of recomputing anything. Gating *both*
creation and re-arm on that linkage then broke resumability across the
crash window between the two — fixed by gating them independently, re-arm
on the replacement having "started" as told apart from the count via
another id-reconstruction. Comparing only the *current* count for equality
then broke once the count moved even further past the replacement's own
generation (an auto-retry of the replacement itself, or a later, second
supersession) — fixed by searching the whole range of numbers the node had
ever used instead of just the current one. Each fix genuinely closed the
gap the round before it found and shipped with its own regression test; the
pattern across all three was the same shape of bug recurring one layer
further out, because the id was always being *reconstructed* from a single
counter that cannot, on its own, distinguish "which generation is this"
once more than one attempt has ever been created for the same node without
starting.

### The node tracks which attempt it started directly, instead of reconstructing one

A fifth review round (critical-findings-only, following the fourth) found
the reconstruction approach broken a fourth time, from an entirely
different angle: superseding a replacement that was never started at all.
Its own number is always `currentNode.AttemptCount + 1` (nothing advances
that count until something is actually started), so superseding it again
naively recomputes the *same* number for the new replacement — a genuine id
collision with the very attempt just cancelled. The resulting version
conflict was swallowed by the existing "conflict on attempt creation might
just be a benign replay" tolerance, so the second supersession reported
success while creating nothing.

Caught by a new regression test
(`SupersedingAReplacementThatWasNeverStartedCreatesAGenuinelyDistinctSecondReplacement`)
before it could reach a real caller. Rather than patch this fourth variant
of the same underlying problem, the mechanism was replaced instead of
extended further. Two durable facts, tracked directly rather than
reconstructed:

- **`NodeSnapshot.CurrentAttemptId`** (new field, carried by a new
  `current_attempt_id` argument on the node's own `running` transition):
  the exact attempt id `StartAttemptAsync` just started, set every time it
  starts one — fresh or a picked-up pending replacement alike. No id is
  ever guessed from a count.
- **`StartAttemptAsync` finds a pending replacement by direct query**, not
  by recomputing its id: `state.Attempts.Values.FirstOrDefault(a =>
  a.NodeId == nodeId && a.State == AttemptState.Created)`. A `created`
  attempt already linked to a `ready` node *is* the thing waiting to be
  picked up, unambiguously, regardless of which number it happened to be
  minted at or how many prior supersessions came before it.

With `StartAttemptAsync` no longer depending on any particular number,
`SupersedeAttemptAsync`'s own creation step no longer needs to agree with
it on one either — it now walks forward from `currentNode.AttemptCount + 1`
skipping any number that already collides with an existing attempt
(trivially resolving the round-5 collision), and the replacement's number
becomes purely an implementation detail of how its id is minted, not a
contract any other call site has to reconstruct.

Detecting whether an existing replacement has already been picked up
(`SupersedeAttemptAsync`'s own re-arm gate) now combines the two durable
facts instead of reconstructing anything: the replacement's own `State` no
longer being `created` means a real completion
(`CompleteAttemptAsync`/`ResolveHumanGateAsync`) has already walked it
through `preparing`/`running`/`validating` to a terminal state — covering
the "started, then failed, retried, or superseded again" case even once
the node has moved on to a later generation the replacement is no longer
current for; `currentNode.CurrentAttemptId` matching the replacement's own
id directly covers the "started, still genuinely in flight, `state` still
`created`" case, since nothing drives an attempt through those states on
its own. Either condition is sufficient; re-arm proceeds only when neither
holds.

### `StartAttemptAsync`'s own fresh-id path needed the same collision skip

A sixth review round (critical-findings-only), reviewing this new design
fresh, found one asymmetry the redesign itself introduced:
`SupersedeAttemptAsync`'s creation step walks forward past any number that
already collides with an existing attempt, but `StartAttemptAsync`'s own
fresh-attempt path (reached when no pending replacement exists) still
derived an id straight from `AttemptCount + 1` with no equivalent check.
Since a collision-skip in `SupersedeAttemptAsync` never bumps
`AttemptCount` to match the number it actually consumed (deliberately —
that count only advances when something is actually *started*),
`AttemptCount` can undershoot the true next-free number. Concretely: a
replacement minted at a skipped-forward number, once later picked up and
failed (an ordinary auto-retry, unrelated to supersession), leaves
`AttemptCount` still short of that number; a subsequent ordinary start then
recomputes that same, now-terminal attempt's id — the round-5 collision
shape, recurring on the other side of the mechanism. The conflict this
produces is silently swallowed by the same "conflict on attempt creation
might just be a benign replay" tolerance both call sites already rely on
for legitimate resumability, so `StartAttemptAsync` reports success for an
attempt that already failed, and a later `CompleteAttemptAsync` call either
wedges on a version conflict or silently reuses the terminal attempt's
stale `NodeResult`. Fixed by giving `StartAttemptAsync`'s fresh-id path the
identical collision-skipping walk `SupersedeAttemptAsync`'s creation step
already has, so every place that mints a new attempt id now shares the same
guarantee regardless of how approximate `AttemptCount` has become. Extends
the existing regression test
(`SupersedingAReplacementThatWasNeverStartedCreatesAGenuinelyDistinctSecondReplacement`)
to reproduce this exact sequence.

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
- **Circuit-breaker wiring for ordinary failures.** `CompleteAttemptAsync`
  calls `RecordOutcomeAsync(succeeded: true, ...)` on a genuinely new success
  (required to keep the shared budget from becoming a lifetime cap, see
  above), but an ordinary failure completion still does not call
  `RecordOutcomeAsync(succeeded: false, ...)` — only `DeferAttemptAsync`'s
  own `RecordDeferralAsync` records anything for a failure, and it
  deliberately never trips the breaker (a rate limit says nothing about
  provider health). Feeding ordinary, non-rate-limit failures into the
  breaker is a separate concern from durable rate-limit deferral and budget
  sanity, both of which this item now fully covers without it.

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
- The five new diagnostic codes this item adds are registered in
  `docs/contracts/v1/README.md`'s diagnostics/exit-code table, per this
  repo's existing precedent for every prior new code. `Forge.Cli.ExitCodes.For`
  does not yet map any of them to a specific exit category (they fall
  through to `internal_error`) — this gap already predates this item for
  several other registered codes (`workflow_blocked`,
  `review_iteration_limit`, `review_repeated_findings`, `attempt_terminal`,
  and others), so closing it project-wide is left out of this item's scope
  rather than fixed piecemeal for only the five codes added here.

## References

- ADR 0006 (supervised execution and durable rate-limit/steering)
- ADR 0007 (cross-platform core and minimal OS adapters — worktree/process
  boundary this item does not cross)
- ADR 0014 (frozen execution profiles — the provider/model this item routes)
- ADR 0016 (provider stdin/environment/streaming)
- ADR 0017 (attempt deadlines — the prior item's `Running`-state-machine and
  resumability precedents this item reuses)
