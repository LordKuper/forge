# ADR 0015: ASD review-convergence engine

- Status: Accepted
- Date: 2026-08-17
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.21-P11.31) must implement
the ASD review engine ADR 0006 already specifies: independent design/
implementation counters, fresh contexts per iteration, full-scope-first-
then-incremental-later, file/rubric coverage, same-iteration approval,
rising severity floors, repeated-normalized-finding detection, and human
convergence gates — plus reuse ADR 0014's already-built reviewer-lineage
selection rather than reimplementing it.

ADR 0006, quoted in full for the parts this item makes concrete:

> "One engine runs design and implementation review with independent
> durable counters and rubric/scope inputs; it does not encode separate
> reviewer roles or pipelines. Each iteration starts fresh reviewer contexts
> with no authoring rationale or prior conversational context."
>
> "Default consecutive budgets are low `1`, medium `1`, high `2`, and
> critical `10`. Their cumulative range yields floors low on iteration 1,
> medium on iteration 2, high on iterations 3-4, critical on iterations
> 5-14, and an iteration-limit human gate before iteration 15. Findings
> below the current floor are recorded as dropped, not silently lost.
> User-approved continuation keeps the counter and pins the floor at
> critical; it never resets or re-admits lower severities."
>
> "Every internal reviewer emits a coverage ledger for every scoped file
> and every applicable rubric item. An incomplete ledger invalidates that
> verdict and causes one fresh re-dispatch in the same iteration."
>
> "The external reviewer receives the prior iteration's normalized finding
> set as explicit bounded input. Two consecutive identical sets by file,
> location, rule, and message fingerprint create a review-convergence human
> gate."

Confirmed before designing: no code anywhere represents any of this.
`Finding.Fingerprint` (`SprintScheduler.cs`) hashes `sprintId | messageKey |
evidence` — it excludes location entirely and was built for a different
purpose (a sprint-scoped record identity), not ADR 0006's four-component
cross-iteration comparison. `NodeSnapshot.AttemptCount` is one generic,
per-node retry counter shared with ordinary node failures — not the
"independent durable counters" per review dimension ADR 0006 requires. No
`ReviewVerdict`/`CoverageLedger`/severity-floor type exists. The two
diagnostic codes `review_iteration_limit` and `review_repeated_findings`
were reserved in `docs/contracts/v1/README.md` since an earlier stage but
never implemented. And, as with ADR 0014, no node executor exists anywhere
that would actually dispatch a reviewer and call back with a verdict —
`ImplementationCriticalGraphBuilder` has exactly one `review` node, no
separate internal/external nodes, no back-edge to `implementation`.

## Decisions

### Scope: the decision engine, not the dispatcher

This item builds the durable record shape and the pure decision rules a
verdict is judged against — `SprintScheduler.RecordReviewIterationAsync`,
callable today with a caller-supplied verdict — not a component that
dispatches a reviewer, waits for one, or routes "changes requested" back
into a fresh implementation attempt. That routing needs the same node
executor ADR 0014 already deferred (nothing calls `ILlmProvider.RunAsync`
anywhere); building it here would mean stubbing an executor's job to give
this item something to call, which the "shape now" precedent this repo has
used repeatedly (`Handoff`, `RubricAssessment`'s removal, `ExecutionProfile`
before this stage) argues against. `RecordReviewIterationAsync` does not
touch the `review` node's own `NodeState` — a review node's `Succeeded`/
`Failed` transition (via the existing, unmodified `CompleteAttemptAsync`)
remains a separate concern for whatever executor eventually drives it.

### One combined verdict per iteration, not per-reviewer-kind aggregation

ADR 0006 says "all mandatory eligible reviewers must approve in the same
iteration" — language that presumes a multi-reviewer configuration this
codebase has no concept of (how many reviewers are mandatory, or eligible,
is not configurable anywhere). Rather than model a reviewer-count concept
with no real source, `RecordReviewIterationAsync` takes one `ReviewerKind`
(`Internal`/`External`) and one `ReviewOutcome` per call, and treats each
call as one complete iteration for its `ReviewDimension`. "Same-iteration
approval" is trivially satisfied by construction — there is exactly one
verdict per iteration in this model — until a real multi-reviewer
configuration exists to aggregate across.

### `ReviewIterationRecord` and iteration counting

`Forge.Domain.ReviewIterationRecord` (`review-iteration.schema.json`)
records one verdict: `Dimension`, `ReviewerKind`, `Iteration`, `Outcome`,
`ExternalFindings` (populated only for `ReviewerKind.External`), and an
optional `Coverage` ledger. `Iteration` is derived — the count of prior
records for the same `(NodeId, Dimension)` plus one, the same pattern
`StartAttemptAsync` already uses for attempt numbers — never
caller-supplied, so a caller cannot skip or replay iteration numbers.
Persisted through the same state-independent, digest-free pattern
`RecordConfirmationAsync`/`RecordHandoffAsync` already established.

An `Internal` call with a missing or incomplete `CoverageLedger`
(`ReviewConvergencePolicy.IsCoverageComplete`: every scoped file and every
applicable rubric item covered) is rejected with `workflow_record_invalid`
and **records nothing, consuming no iteration** — ADR 0006's "causes one
fresh re-dispatch in the same iteration" is exactly this: the caller
retries with a complete ledger and lands on the same iteration number the
rejected call would have used.

### Severity floor and the dropped-not-lost rule

`ReviewConvergencePolicy.SeverityFloorFor(iteration)` implements the exact
cumulative budget table (low=1, medium=1, high=2, critical=10 → floors
low/medium/high/high/critical/.../critical across iterations 1/2/3-4/5-14).
On `ChangesRequested`, each `ReviewFindingDraft` is recorded through the
*existing* `Finding` machinery rather than a new one: at or above the floor,
`RecordFindingAsync` runs unchanged (an `Open` finding, blocking completion
exactly as it always has); below the floor, a `Finding` is still saved —
schema-valid, auditable — but with `FindingStatus.Dismissed` from the
start, never `Open`, so it never blocks anything. This is "dropped, not
silently lost" using a status this repository's `Finding` contract already
defines, rather than inventing a parallel "dropped findings" list.

### Pinning the floor at critical

`SprintScheduler.PinReviewFloorAsync` is the one new mutating capability
this item adds beyond recording verdicts — ADR 0006's "continue" choice at
the convergence gate. It is a one-way marker per `(sprint, node, dimension)`
(`ISprintStore.SetReviewFloorPinnedAsync`/`IsReviewFloorPinnedAsync`, a
plain file-existence check, not a versioned record — there is nothing to
version for a flag that only ever goes from unset to set) that makes every
later `SeverityFloorFor` call for that dimension return `Critical`
regardless of iteration count, matching "never resets or re-admits lower
severities." The gate's other two choices need no new code: "accept current
findings" is exactly the existing `ResolveFindingAsync` called per open
finding, and "abort" is exactly `SprintOrchestrator.CancelSprintAsync` —
duplicating either here would be new surface for a solved problem.

### The two convergence triggers

`RecordReviewIterationAsync` blocks the sprint (reason `review_convergence`
— a fifth member of the `node`/`finding`/`gate`/`confirmation` family,
requiring the operator's explicit `resume_sprint`/`run_sprint` exactly like
the other three non-auto-recovering reasons) through the same
`TryBlockSprintAsync` helper `RecordConfirmationAsync` already used —
extracted from that method into a shared helper as part of this change,
since both now need identical bounded-retry-on-conflict semantics — when
either:

- the new iteration would exceed the cumulative critical budget and the
  floor is not already pinned (`review_iteration_limit`), or
- this is an `External`, `ChangesRequested` verdict whose normalized finding
  set matches the *immediately preceding* `External` record's set exactly
  (`ReviewConvergencePolicy.HasRepeatedExternalFindingSet`,
  `review_repeated_findings`) — a set that repeats only after an
  intervening `Approved` iteration does not count, matching ADR 0006's
  "two *consecutive* identical sets."

`NormalizedFindingKey(File, Line, Rule, MessageFingerprint)` is built fresh
per finding from a `ReviewFindingDraft` (never from `Finding.Fingerprint`,
which excludes location) — `Rule` is the finding's `MessageKey`, the
closest existing concept this codebase has to "the rule that fired."

## What stays deferred

- **The node executor and any real reviewer dispatch.** Nothing calls this
  engine automatically; nothing routes a `ChangesRequested` verdict back
  into a fresh implementation attempt (ADR 0006: "Fixes run in a new
  implementation attempt, not inside the reviewer context") — that needs a
  graph shape or scheduler-level loop-back this item does not add, on top of
  the executor itself.
- **Which dimension triggers when, and from where.** `ReviewDimension` gives
  the two counters a stable identity; nothing decides when a "design" review
  happens versus an "implementation" review, since the built-in graph has
  one `review` node, not two.
- **Multi-reviewer same-iteration aggregation.** See "one combined verdict
  per iteration" above.
- **The full-scope-first, incremental-later distinction.** ADR 0006:
  "Iteration 1 reviews the full scoped artifact/diff; later iterations
  review the changes... plus the still-relevant acceptance and rule
  context." Deciding what a reviewer actually receives as input is a
  dispatch/prompt-construction concern for the executor this item does not
  build; `Iteration` alone gives a future caller everything it needs to make
  that choice itself.
- **Fresh-context enforcement.** "Starts without a parent transcript" is
  vacuously true today the same way ADR 0014 already recorded for its own
  "no widening" rule — no transcript concept exists anywhere yet.

## Consequences

Every rule ADR 0006 states as a *policy* — the floor table, the coverage
completeness check, the repeated-finding-set test, the iteration-limit
threshold — now has a real, pure, independently tested implementation
(`ReviewConvergencePolicy`) a future executor calls into, and a real durable
record (`ReviewIterationRecord`) it calls to persist a verdict against. The
sprint-level consequence of hitting either convergence trigger — an
explicit, operator-required block — is wired through the exact same
mechanism `ConfirmationArtifact` already established, rather than a new
one. Nothing here can be exercised end-to-end by a real review yet, since
nothing dispatches a reviewer or interprets its output as a verdict — that
gap is named, not hidden, and lands with the provider-protocol/executor work
later in Stage 11.

| Action | Recovery |
|---|---|
| record an internal verdict with missing/incomplete coverage | rejected (`workflow_record_invalid`); nothing persisted, no iteration consumed |
| record a verdict against an unknown node id or a node not tagged `Review` | rejected with `node_not_found`/`node_kind_mismatch` |
| a `ChangesRequested` verdict's new iteration exceeds the cumulative budget, floor not pinned | sprint blocks with `review_iteration_limit`; requires `resume_sprint`/`run_sprint` |
| an external `ChangesRequested` verdict repeats the immediately preceding external verdict's exact finding set | sprint blocks with `review_repeated_findings` |
| `PinReviewFloorAsync` is called for a dimension | every later iteration for that dimension floors at `Critical`, including one that would otherwise have exceeded the iteration limit |
| the sprint-blocking append itself cannot land after retrying | the verdict is still durable; `RecordReviewIterationAsync` reports `Succeeded: false`/`workflow_event_conflict`, mirroring `RecordConfirmationAsync`'s own rule |
