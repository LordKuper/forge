# ADR 0046: Stage-transition prerequisite policy

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.6.0

## Context

`docs/plans/desktop-workspace-redesign.md` sections 8.1-8.3 and 8.5 require
that moving a sprint to another workflow stage go through a Host-authoritative
read-only assessment (`AssessStageTransition`) before any mutation
(`MoveSprintToStage`) can commit. The assessment must report satisfied and
unsatisfied prerequisites, active-operation impact, what would be
superseded, whether confirmation is mandatory, and the expected state
version — and "the UI may explain these checks but may not calculate or
override them" (section 8.2). No prerequisite evaluator, assessment
contract, or commit coordinator exists today; `SprintGraphValidator` checks
only that a frozen DAG is structurally valid, not whether a specific stage
transition's runtime prerequisites currently hold. Slice 1 records the
policy decision and reserves the protocol surface; the evaluator and
coordinator are Slice 3.

## Decisions

### Assessment and commit are two capabilities, not one

`workflow.assess_stage_transition` (query, read-only, permission
`read_redacted`) and `sprint.move_stage` (command, permission
`human_stage_transition_confirm`) are reserved as separate
`capabilities.json` entries rather than one capability with a dry-run flag.
This mirrors the plan's own separation ("Add `AssessStageTransition`... as a
read-only Host query" distinct from "`MoveSprintToStage` carries... an
assessment token") and the repository's existing precedent of keeping reads
and mutations on separate capability ids even when tightly coupled (e.g.
`project.snapshot` vs. `sprint.manage`). Neither id is added to
`CapabilityIds.Implemented` yet — same reservation shape as ADR 0043/0044 —
so an older Host/Desktop is never advertised either during handshake before
Slice 3 ships them.

### The prerequisite rule list is host-authoritative by construction, not by convention

Section 8.2's ten prerequisite categories (predecessor stage results,
implementation confirmation, test-work decision, review convergence and
human approval, finding severity policy, provider/model policy, handoff/
artifact digests, Git cleanliness, rate-limit/retry-budget rules, no
conflicting active operation) are recorded here as the exhaustive rule set
future `AssessStageTransition` code must evaluate — not implemented as code
in this slice. Recording them now, before Slice 3 writes the evaluator,
fixes the contract's scope so the evaluator cannot silently ship a narrower
subset without an explicit ADR amendment: Slice 3's own review gate checks
its evaluator against this exact list, the same role ADR 0006 played for
`ExecutionProfilePolicy.Freeze` in ADR 0014.

The plan's "UI may explain these checks but may not calculate or override
them" is enforced structurally, not by a lint rule: `AvailableAction` (ADR
0043) and `AssessStageTransition`'s own future response are the only
contracts Desktop reads, and neither is designed to expose the underlying
predicate logic — only its already-evaluated result (satisfied/unsatisfied,
blockers, consequences). No prerequisite-evaluation code is added to
`Forge.Desktop*` or `Forge.Desktop.Presentation` in this slice or planned in
any later one.

### Advance and rewind share one assessment contract

Section 8.1's `AssessStageTransition(sprintId, targetStageId)` returns a
`direction` of `advance`, `rewind`, or `same` rather than two separate
contracts for the two directions. Both need the identical shape (source/
target stage, satisfied/unsatisfied prerequisites, active-operation impact,
confirmation requirement, expected version); rewind's additional fields
(what would be superseded, per section 8.4) are optional/empty for an
advance rather than justifying a second contract. This keeps one schema and
one Stage 0 gate entry instead of two contracts that would need to stay in
lockstep by hand.

### No evaluator, DAG-prerequisite code, or commit coordinator in this slice

`SprintGraphValidator.IsValid` (the only existing DAG check, used at sprint
creation) is unchanged: it validates structure, not stage-transition
prerequisites, and section 8.2's rules need runtime data (findings, Git
state, routing budget) that a structural validator never reads. Building
the evaluator now, without `StageRevision`'s query semantics (ADR 0045,
also deferred to Slice 3) or a real rewind coordinator to call it, would
freeze an interface neither has yet constrained.

## What stays deferred

- The prerequisite evaluator over the frozen DAG and current stage revision
  (Slice 3).
- `AssessStageTransition`'s read-only projection and blocker reporting
  (Slice 3).
- The idempotent `MoveSprintToStage` advance/rewind coordinator, assessment
  tokens bound to project/sprint/target/revision/version, and durable
  recovery (Slice 3).
- Host/CLI surfaces for both capabilities (Slice 3 backend), Desktop's
  stage-selection and confirmation UI (Slice 6, matching the established
  CLI-first rhythm).

## Consequences

- The ten prerequisite categories in section 8.2 are now a named, ADR-recorded
  contract Slice 3's evaluator must implement in full or amend this ADR to
  narrow — it cannot silently ship a subset unreviewed.
- `capabilities.json` documents `workflow.assess_stage_transition` and
  `sprint.move_stage` as reserved, unimplemented ids with no behavior
  behind them; existing capability-list-driven tests
  (`CapabilityIds.Implemented`-scoped) are unaffected.
- No evaluator, coordinator, or UI code exists yet; "the UI cannot
  calculate or override prerequisites" holds vacuously today because no UI
  code touches prerequisite logic at all, the same honest-vacuity posture
  ADR 0014 and ADR 0044 both use for deferred enforcement.

## References

- Plan sections 8.1-8.3, 8.5 (assessment, prerequisites, advance/rewind,
  commit command), section 12.5 (acceptance criteria)
- ADR 0045 (stage revision model this evaluator will query)
- ADR 0014 (precedent for recording a rule set ahead of its enforcement code)
