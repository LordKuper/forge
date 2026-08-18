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

### The replacement attempt is found by linkage, never recomputed from a moving count

A third review round found that the fix above still had a gap one step
further downstream: the replacement attempt's deterministic id was
recomputed on every call as `currentNode.AttemptCount + 1`. That count is
not stable across the replay window this ADR's whole "idempotent replay"
section is about — it advances the moment a later, ordinary
`StartAttemptAsync` call legitimately picks the replacement up and the node
re-enters `running`. A replay of `SupersedeAttemptAsync` arriving *after*
that point recomputed a different, unrelated id (creating a second,
orphaned replacement attempt still linking back to the original, already-
cancelled one) and then — because it saw the node `running` again — walked
the `running -> failed -> ready` re-arm path a second time, forcibly ending
the replacement's own legitimate, already in-flight run. The fix: find the
replacement by linkage (`SupersedesAttemptId == attemptId`) instead of
recomputing it. Computing the deterministic id from `currentNode.AttemptCount`
remains correct only when no replacement exists yet, where the node still
carries only the consequence of this same call's own cancel transition.

### Re-arming is gated on the replacement having started, not merely existing

That same fix, in its first form, skipped *both* attempt creation and the
node re-arm together whenever a replacement already existed — an
independent, later pass reviewing that exact change found this regressed
the crash-window fix two sections up. Creation and re-arm are two separate
durable steps; a crash can land between them, with the replacement already
created but the node still `running` from the cancel transition's own
consequence, waiting to be re-armed. Skipping re-arm whenever a replacement
merely *exists* left that window permanently stuck: the replacement is
durably created, but nothing ever re-arms the node to `ready`, and no other
verb can move a `running` node — the sprint wedged with a `created`
replacement its own node can never start, short of aborting the sprint.
Creation and re-arm are gated independently: creation on whether a
replacement exists at all (unchanged), re-arm on whether it has actually
**started**. The replacement attempt's own `State` cannot answer that:
nothing in this codebase yet drives an attempt past `created` (no node
executor exists), so `StartAttemptAsync` picking the replacement up leaves
its `State` at `created` regardless — only the *node* moves, to `running`,
carrying the attempt number it started as an argument. The reliable signal
is therefore the node's current attempt number: `currentNode.AttemptCount`
is untouched by the cancel transition above, so re-deriving the attempt id
that number resolves to (the exact same deterministic-id formula used to
create the replacement) and comparing it against the replacement's own id
tells the two cases apart. A replacement not yet picked up still needs the
node re-armed to `ready` so an ordinary `StartAttemptAsync` can reach it
(covering both "just created by this same call" and "created by an earlier,
crash-interrupted call"); only once it has genuinely been picked up must
re-arming stay hands-off — the original bug this whole three-round chain
traces back to.

### The re-arm signal searches every attempt number the node has used, not just its current one

A fourth review round — the last full-scope round already used, this one
critical-findings-only per this repository's review-gate rules — found that
comparing the node's *current* attempt number against the replacement's own
for plain equality was still one step short. That number only ever grows:
an ordinary auto-retry of the replacement itself (a ordinary provider
failure, nothing to do with supersession) or a later, second supersession
both advance `currentNode.AttemptCount` further without ever moving it
back. A replay of the original supersede call arriving after such further
progress compares the node's now-*later* generation against the
replacement's own, fixed generation, finds no match, and wrongly concludes
"not picked up yet" — re-arming the node out from under whatever later,
genuinely in-flight generation is actually running. Exactly the class of
bug the two sections above this one already fixed, recurring one layer
further out. The fix: since `StartAttemptAsync` increments
`currentNode.AttemptCount` by exactly one on every call, the set of attempt
numbers the node has ever actually used is precisely the dense range
`1..currentNode.AttemptCount` — no gaps, nothing skipped or reused — so the
check searches that whole range for whichever number's deterministic id
matches the replacement's own, rather than only the current one. A match
anywhere in the range means the node reached that generation at some point
and, being monotonic, never un-reached it, so its `running` now belongs to
it or a later descendant either way.

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
