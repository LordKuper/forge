# ADR 0044: Stop-current-operation semantics

- Status: Accepted
- Date: 2026-08-23
- Contract version: state-machines.json 1.2.0, capabilities.json 1.6.0

## Context

`docs/plans/desktop-workspace-redesign.md` section 7 requires a
`StopCurrentOperation` mutation distinct from cancelling a sprint or
superseding an attempt: it targets the exact active attempt from a fresh
snapshot, cancels it without settling the sprint as failed or consuming
retry budget, discards its worktree, re-arms the owning node, and leaves the
sprint `paused` rather than resuming automatic execution. None of
`SprintState`, `AttemptState`, or the Host protocol has a way to express
"stopped, recoverable, and not currently executing" today — cancelling ends
the sprint permanently, and every existing attempt failure either retries
automatically or fails the node. Slice 1 records the state-machine and
protocol decisions; the stop coordinator, active-operation registry, and
worktree/process wiring land in Slice 2.

## Decisions

### `Paused` is reachable only through the existing single-writer append gate

`SprintState` gains `Paused` with transitions `Running -> Paused`,
`Paused -> Ready`, `Paused -> Cancelled` (plan section 7.2), added to both
`Forge.Domain.WorkflowStateMachines.Sprint` and
`docs/contracts/v1/state-machines.json` (1.1.0 -> 1.2.0). `AttemptState`
gains `Validating -> Cancelled` so a stop request submitted while an
attempt is validating its own outcome remains valid until the operation
actually settles.

No new guard type is introduced for this, because one already exists and
already governs every other state: `FileSprintEventLog.IsLegalTransition`
is the single store-level chokepoint every `AppendTransitionAsync` call
passes through, and it already delegates purely to
`WorkflowStateMachines.CanTransition` — "a caller-side check... is not
enough, since nothing stopped a bug elsewhere in the engine from appending
an illegal transition directly" (that method's own remarks). Updating the
transition table is sufficient; no generic public setter on
`SprintSnapshot`/`AttemptSnapshot` ever assigns a state directly, and this
slice adds none. The stop coordinator introduced in Slice 2 gets no special
bypass — it calls `AppendTransitionAsync` like every existing caller
(`SprintOrchestrator`, `SprintScheduler`).

### `StopIntent` is a plain contract type, not a persisted or wired shape

`Forge.Domain.StopIntent(AttemptId, DateTimeOffset RequestedAt, string
Reason)` names the durable record section 7.3 requires ("Persist the stop
intent before relying on the in-memory registry... Executors and restart
recovery must check the intent before starting or resuming an attempt").
This slice adds only the record. Its store, the `ActiveOperationRegistry`,
executor registration, and the idempotent resumable saga that reads/writes
it are Slice 2 work — building them now would freeze a persistence and
recovery shape before the crash-recovery acceptance cases that constrain it
(plan section 12.4's crash-at-every-boundary requirement) exist to test
against.

### Reserved `workflow.stop_operation` capability

`capabilities.json` gains `workflow.stop_operation` (command,
`StopCurrentOperation`, permission `human_stop_operation_confirm`) with a
`note` stating it is not yet in `CapabilityIds.Implemented` — same
reservation shape as ADR 0043's four query ids and ADR 0042's
`quality.evaluate`. An older Desktop or Host that has not shipped the stop
coordinator is never advertised this capability during handshake (plan
section 9.2), so it cannot silently attempt an operation with no coordinator
behind it.

### What `ResumeSprintAsync` does not yet do

`SprintOrchestrator.ResumeTarget` (the pure function backing `forge sprint
resume`) is deliberately left unchanged: it still maps only
`Blocked`/`Failed -> Ready`. Section 7.1's "Resuming a paused sprint
transitions it through `ready` and starts a fresh attempt from the current
integration base" is Slice 2 behavior once a real executor exists to start
that fresh attempt; wiring `Paused` into `ResumeTarget` now would let
`forge sprint resume` silently accept a `Paused` sprint with no attempt
ever created behind it, which is a materially different, half-built
behavior, not a data-only step.

## What stays deferred

- Active-operation registration in executors, durable stop-intent
  read/write, and the idempotent stop coordinator (Slice 2).
- Process-tree termination, worktree discard, and node re-arming without
  consuming retry budget (Slice 2).
- Paused-sprint resume (`ResumeTarget` mapping `Paused -> Ready` with a
  fresh attempt) and crash recovery (Slice 2).
- Host/CLI/Desktop surfaces for `workflow.stop_operation` (Slice 2 backend
  first, Desktop wiring in Slice 6 per the established CLI-first rhythm —
  ADR 0037's own precedent for `workflow.confirm`/`workflow.test_work`/
  `workflow.finalize`).

## Consequences

- `SprintState.Paused` and `AttemptState`'s new `Validating -> Cancelled`
  edge exist in the frozen state machine and are exercised by
  `WorkflowContractTests` and `SprintEventStoreTests`, but nothing in
  production code produces them yet — both are unreachable through any
  existing command, matching ADR 0014's "honestly vacuous... true by
  construction" precedent for deferred enforcement.
- `Forge.Domain.StopIntent` exists as a reviewed, parity-checked shape for
  Slice 2 to persist against, without committing to a storage format yet.
- No executor, registry, or CLI/Desktop surface changed.

## References

- Plan section 7 (stop current operation), section 12.4 (acceptance criteria)
- ADR 0037 (CLI-first / Desktop-parity-later precedent)
- ADR 0042 (the reserved-capability-id precedent this ADR reuses)
