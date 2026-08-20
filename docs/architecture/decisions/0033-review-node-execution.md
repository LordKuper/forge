# ADR 0033: Review node execution

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.0.0

## Context

ADR 0006/0015 built the whole severity-floor/convergence review engine
(`SprintScheduler.RecordReviewIterationAsync`, `ReviewConvergencePolicy`)
with zero production callers, ahead of any executor that would drive it —
the same "primitive ahead of its executor" rhythm ADR 0031's commit
primitive and ADR 0032's implementation executor already established.
ADR 0032 shipped `implementation`, the `ImplementationCriticalGraphBuilder`
node depending on `planning`. `review` is the next node in that graph,
depending on `test_work` (itself gated on a `Confirmed` confirmation
artifact — `SprintScheduler.IsTestWorkEligibleAsync`), and is the first
executor to invoke the review engine for real.

## Decisions

### `ReviewExecutionHostedService`, structurally identical to its three siblings

Same options record (`ReviewExecutionOptions`, a test-overridable poll
interval), `TickAsync` loop, per-sprint failure isolation (the identical
widened catch filter every prior executor uses), and registration/
start-stop lifecycle in `ForgeHostApplication`/`ControlPlaneHostedService`
alongside `intake`/`planning`/`implementation`. Reuses
`NodeExecutionDiagnostics` (`Diagnostic`/`Digest`/`MapProviderFailure`) and
`TokenBudgetResolver`, both already shared across the other executors.

### Deliberately narrow: one dimension, one reviewer kind, no located findings

Only `ReviewDimension.Implementation` (the dimension ADR 0006's
repeated-finding-set convergence rule is built around) and only
`ReviewerKind.External` (a single provider's own verdict) are exercised;
`ReviewerKind.Internal` needs a rubric/coverage-scoping mechanism this
slice does not build. The provider's response is parsed for a verdict
only — a fixed `APPROVED`/`CHANGES_REQUESTED` marker as the terminal
summary's last non-blank line (case-insensitive) — never individual,
located findings: `ReviewFindingDraft` requires evidence and a
schema-shaped message key per finding, and reliable structured extraction
from free-text provider output is real, separate design work. `findings`
is always empty; ADR 0006's repeated-finding-set rule still works
correctly on the empty set exactly like any other, so this is an honest
degradation, not a broken feature.

### A review attempt spans however many iterations it takes to converge

The central design decision of this slice. `SprintScheduler
.MaxAutomaticRetries` fixes an ordinary Work node's attempt budget at two;
ADR 0006's own convergence budget allows fourteen review iterations before
its iteration-limit gate trips. Routing every `ChangesRequested` verdict
through the generic `CompleteAttemptAsync(succeeded: false, ...)` path
would exhaust the node's retry budget by the third iteration, long before
convergence ever applies. Instead, an ordinary unresolved
`ChangesRequested` verdict calls `RecordReviewIterationAsync` but
deliberately makes **no** scheduler completion call at all: the attempt
stays `running`, resumed with the same attempt id on the next tick,
producing the review's own next iteration. Only two outcomes complete the
attempt: an `Approved` verdict, or `RecordReviewIterationAsync` itself
reporting a convergence-gate trip (`DiagnosticCodes.ReviewIterationLimit`
or `.ReviewRepeatedFindings`) — both call `CompleteAttemptAsync(succeeded:
true, ...)`, since a convergence-gate trip is ADR 0006's own designed
stopping point (it blocks the sprint via the review engine's existing
`review_convergence` mechanism), not a technical failure. A genuine
technical failure — provider error, timeout, an unparseable verdict —
still completes the attempt as failed and stays bounded by the generic
retry budget; only "the reviewer asked for changes" is exempt from it.

### A new minimal, bounded, read-only diff primitive

Review needs to show the provider what changed, which no existing
primitive produces: `ContextManifestCompiler` assembles rules/knowledge
documents, not a git diff. Rather than a general diff-querying mechanism
across the whole context-compilation system, this slice adds the smallest
thing review needs — `IWorktreeManager.DiffAsync`/`SprintGitIsolation
.ReadDiffAsync`, base commit → integration tip, capped at 50,000 characters
(`GitWorktreeManagerDiffBudget.MaxCharacters`) with a truncation flag
carried into the prompt. Unlike ADR 0031's commit primitive (which got its
own slice because it mutates the working tree), this primitive is
read-only and low-risk, so it is bundled directly into this executor's
slice rather than shipped ahead of its first caller.

### Reviewer independence is recorded, never enforced

`ExecutionProfile`'s frozen `ExecutionLineage` (whether the review
provider differs from the implementation provider) is rendered into the
prompt as informational context — the provider is told whether it is
reviewing its own prior work — but a same-provider review still runs and
still records a real verdict. This matches ADR 0006 itself, which only
ever *records* reduced lineage separation; it never gates on it.

### Review reads implementation's handoff directly; `test_work` has no executor yet

Same "bypass the not-yet-built intermediary" choice `implementation`
itself already made for `planning`'s handoff: the review node's graph
dependency is `test_work`, which has no executor and never records a
handoff, so this executor fetches `implementation`'s own `Handoff`
directly via `GetHandoffsAsync`. No handoff for this sprint means nothing
to review — the node is left untouched rather than starting an attempt
with nothing to show the reviewer, the same missing-precondition guard
every other executor in this file already applies before `StartAttemptAsync`.

## Consequences

- New `src/Forge.Host.Runtime/ReviewExecutionHostedService.cs` and
  `ReviewExecutionOptions`; registered in `ForgeHostApplication` and
  `ControlPlaneHostedService` alongside its three siblings.
- New `IWorktreeManager.DiffAsync`/`SprintGitIsolation.ReadDiffAsync`
  (`GitDiffResult`, `GitWorktreeManagerDiffBudget.MaxCharacters = 50_000`).
- New `DiagnosticCodes.WorktreeDiffFailed` and
  `ProviderDiagnosticCodes.ReviewVerdictUnparseable`.
- First production caller of `SprintScheduler.RecordReviewIterationAsync`
  and of ADR 0006/0015's whole convergence engine.
- Explicitly **not** in this slice, named rather than silently absorbed:
  individual located findings with evidence (`findings` is always empty);
  `ReviewerKind.Internal`; automatic routing or a back-edge to
  `implementation` on rejection (a convergence-gate trip blocks the sprint
  for a human, which is ADR 0006's own designed stopping point, not a gap
  this slice needs to fill); and the `confirmation`/`test_work`/
  `finalization` executors (this slice's own tests drive those nodes
  directly, the same way `implementation`'s tests already drove `intake`/
  `planning`).

## References

- ADR 0004 (worktree isolation)
- ADR 0006 (supervised execution and review convergence — the engine this
  slice becomes the first production caller of)
- ADR 0009 (Forge document format)
- ADR 0012 (reproducible context assembly — the `Handoffs` layer stub this
  executor bypasses, same as `implementation` already bypassed it)
- ADR 0015 (ASD review convergence engine — severity floors, iteration
  limit, repeated-finding-set rule)
- ADR 0030 (planning node execution)
- ADR 0031 (attempt worktree commit primitive — the "primitive ahead of
  its executor" precedent this slice's own `DiffAsync` follows, though
  bundled rather than split into its own slice since it is read-only)
- ADR 0032 (implementation node execution — the sibling executor this one
  mirrors structurally)
