# ADR 0036: Finalization node CLI command

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.5.0

## Context

`finalization` is the last node in `ImplementationCriticalGraphBuilder`'s
graph, depending on `human_approval`. Like `confirmation`/`test_work`
(ADR 0034/0035), `ExecutionProfilePolicy.PhaseFor` returns `null` for
`NodeRole.Finalization` — "not a model phase" — but unlike them, its scope
was genuinely undecided rather than merely un-modeled: ADR 0001 says only
that "finalization rechecks the base and dependency identities. A
mismatch blocks integration and requires explicit rebase or replanning,"
and nothing else in this codebase's history commits to what finalization's
own real action is. Investigation for this item found:

- **No sprint ever reaches `Completed` today.** `SprintScheduler
  .EvaluateCompletionAsync` transitions a fully-succeeded sprint to
  `ReadyToFinalize` and deliberately stops there; `ReadyToFinalize →
  Completed` is a contract-legal (`docs/contracts/v1/state-machines.json`)
  but code-unreachable edge, since nothing has ever driven the
  `finalization` node to a terminal state.
- **A sprint's real code changes never land anywhere real.** Every
  mutating git operation in this codebase — worktree creation, attempt
  commits, per-attempt integration (`SprintGitIsolation.IntegrateAsync`)
  — happens inside an isolated worktree under `%LOCALAPPDATA%`, fast-
  forwarding only the sprint's own isolated `forge/sprint/<id>` branch.
  Nothing merges that branch into the project's actual working directory.
  Forge also has no concept of "the project's default branch" anywhere —
  every existing operation is purely commit-id-based (`IRepository
  .GetHeadAsync` is read-only, resolves whatever `HEAD` happens to be).
- A dormant `finalize_sprint` entry already exists in
  `docs/contracts/v1/recommendations.json` (predates the confirmation/
  test-work slices), with `safety_class: "human_approval"` — the same
  tier as `resolve_human_gate` — but zero implementation anywhere, and no
  matching `capabilities.json` entry at all (unlike `sprint.rebase`, which
  is at least fully documented as dormant).

Given this, finalization's scope was decided for this item rather than
merely discovered: it performs a real git merge of the sprint's
integration branch into the project's own default branch, then completes
the sprint. This is the first Forge operation of any kind that mutates the
project's own working directory rather than an isolated worktree — treated
throughout as new, first-of-its-kind trust territory, not an extension of
the existing worktree-isolation guarantee.

## Decisions

### `SprintDefinition.DefaultBranch` is frozen at sprint creation, alongside `BaseCommit`

Matches ADR 0001's own "immutable snapshot frozen once" principle for
every other sprint input. `SprintOrchestrator.CreateSprintAsync` now also
resolves `IRepository.GetCurrentBranchAsync` (`git symbolic-ref --short
HEAD`) and refuses sprint creation outright with the new
`DiagnosticCodes.RepositoryDetachedHead` if `HEAD` is detached — a
detached-HEAD sprint would have no branch name to freeze, and letting
every sprint carry a `null` `DefaultBranch` that finalization would then
have to fail closed on individually is more failure surface than refusing
once, early, at creation. `DefaultBranch` is nullable at the type level
only for backward compatibility with a sprint frozen before this field
existed; every sprint created from this point on always has one.

### `IRepository.MergeSprintIntoDefaultBranchAsync`: fast-forward-only, no `checkout`, ever

The new primitive (`GitRepository`, `src/Forge.Runtime/Infrastructure/RuntimeAdapters.cs`)
mirrors `IWorktreeManager.IntegrateFastForwardAsync`'s own established
philosophy exactly (`git merge --ff-only`, failing closed with the same
`DiagnosticCodes.WorktreeIntegrationDiverged` on any divergence — no real
three-way merge or automatic conflict resolution, ever), extended with two
guards a worktree never needed (a worktree is always created fresh at a
known commit; the project's own working directory is not):

1. Refuses with the new `DiagnosticCodes.RepositoryDirty` if the project's
   working directory has uncommitted changes (`git status --porcelain`).
2. Refuses with the new `DiagnosticCodes.RepositoryBranchMismatch` if the
   branch currently checked out there is not the sprint's frozen
   `DefaultBranch`.

Deliberately, this method **never runs `git checkout` itself** — it only
ever fast-forward-merges into whatever branch is already checked out, and
only when that already matches. This means the project's own working
directory never changes which branch it is on because Forge ran, under
any circumstance: a human who has switched to a different branch since
the sprint was created is refused with a clear diagnostic rather than
having their own checkout silently switched back. This is the single
most consequential safety property of this whole item, and every other
design choice here is subordinate to preserving it.

### `IRepository`, not `IWorktreeManager`, owns this method

`IWorktreeManager`'s own doc comment already states "no method leaves the
main repository itself checked out to a different branch or dirty" —
adding a `projectRoot`-mutating method there would read as contradicting
that documented invariant, even though the new method's own guards keep
it true in spirit. `IRepository` already establishes "this interface
touches `projectRoot` itself" (its only prior member, `GetHeadAsync`, is
read-only); this item's write operation joins it there instead.

### A human-only CLI command, not an autonomous executor

`ExecutionProfilePolicy.PhaseFor` returning `null` does not by itself
settle CLI-vs-executor — `confirmation`/`test_work` are also "not a model
phase" and both became human-only commands (ADR 0034/0035) precisely
because their own action is a judgment call. Finalization's own action is
not a judgment call (it is entirely machine-checkable — the same
`dependencies.current`/`gates.passed`-shaped preconditions
`recommendations.json`'s dormant entry already names), but it is a
**repo-mutating, first-of-its-kind, higher-blast-radius** operation, and
`recommendations.json`'s own `safety_class: "human_approval"` for this
exact action already anticipated that. `forge finalize` follows ADR 0019's
established human-only pattern exactly: ADR 0023's interactive-session
check, mandatory `--yes` with no config-driven bypass. Unlike `confirm`/
`test-work`, there is no outcome choice to make (finalization only ever
attempts the same merge), so this is a single command, not a noun with
two verb subcommands.

### Real I/O orchestrated in `ForgeApplication`, not folded into one `SprintScheduler` call

`ConfirmNodeAsync`/`RecordTestWorkAsync` (ADR 0034/0035) each compose
their whole `StartAttemptAsync`/action/`CompleteAttemptAsync` sequence
inside one `SprintScheduler` method, because their own "action" is a pure,
local, in-memory store write. Finalization's action is genuine external
git I/O — folding it into `SprintScheduler` would give that class (which
otherwise has no I/O beyond durable event-log state, by design) a real
dependency on `IRepository`. Instead, `ForgeApplication.FinalizeSprintAsync`
orchestrates directly: `StartAttemptAsync` → `IRepository
.MergeSprintIntoDefaultBranchAsync` → `CompleteAttemptAsync` → (on
success) a new `SprintScheduler.CompleteSprintAsync` walking
`ready_to_finalize → completed`. This mirrors how every model-bearing
node's own executor is already structured (real work happens between
`StartAttemptAsync` and `CompleteAttemptAsync`, orchestrated outside
`SprintScheduler`) — just synchronous and CLI-triggered instead of a
background poll loop, since finalization has no executor to poll with.

### No separate version/idempotency-key pre-check — `StartAttemptAsync`'s own already suffices

`ConfirmNodeAsync`/`RecordTestWorkAsync` each derive a version/key and
check it themselves before calling `StartAttemptAsync`, mainly to produce
a more specific `SuggestionStale` diagnostic than `StartAttemptAsync`'s
own generic `WorkflowEventConflict`. `StartAttemptAsync` already performs
the identical check natively (`node.Version != expectedNodeVersion` on
its own fresh state read, rejecting with `WorkflowEventConflict`) — since
`ForgeApplication.FinalizeSprintAsync` passes it the same freshly-read
`node.Version` either way, a duplicate pre-check in `ForgeApplication`
would be redundant, not additive. Skipped here as an intentional
simplification rather than an oversight — no `IForgeMutations` contract
promises a specific diagnostic code, only that a stale caller is
rejected.

### An already-terminal node returns its recorded state instead of re-merging; a failed merge auto-retries like any other Work node

An already-`succeeded`/`failed`-and-exhausted node short-circuits, mirroring
`ConfirmNodeAsync`'s own terminal check — a resumed call after the sprint
already completed reports that success back rather than attempting a
redundant merge. A **failed** merge (dirty tree, wrong branch, diverged)
does **not** get any special handling: `CompleteAttemptAsync`'s own
generic per-node retry budget (`MaxAutomaticRetries`, shared by every
other Work node) already resets the node back to `ready` — not stuck
`failed` — as long as fewer than three total attempts have landed, so a
human who fixes the underlying issue (cleans the tree, checks out the
right branch) can just run `forge finalize` again and land in the fresh
path naturally. Only after the retry budget is exhausted does the node
stay `failed`, identical to how any other Work node behaves once stuck —
`forge attempt supersede` remains the existing, general re-arm mechanism
for that case, not something this item needed to build a parallel path
for.

### Not yet in `CapabilityIds.Implemented`

Same precedent as `workflow.confirm`/`workflow.test_work`:
`capabilities.json` documents `workflow.finalize` now (bumping
`contract_version` to `1.5.0`), but it is not added to
`CapabilityIds.Implemented` — CLI-only this slice, Desktop parity
deferred and named as future work.

## Consequences

- New `SprintDefinition.DefaultBranch`; `SprintOrchestrator.CreateSprintAsync`
  now also resolves and freezes it, refusing sprint creation on a detached
  `HEAD` (new `DiagnosticCodes.RepositoryDetachedHead`).
- New `IRepository.GetCurrentBranchAsync`/`.MergeSprintIntoDefaultBranchAsync`,
  the first `IRepository` write operation and the first operation in this
  codebase's history to mutate the project's own working directory rather
  than an isolated worktree. New `DiagnosticCodes.RepositoryDirty`/
  `.RepositoryBranchMismatch`; reuses the existing
  `WorktreeIntegrationDiverged` for a diverged fast-forward.
- New `SprintScheduler.CompleteSprintAsync` — the first (and, by design,
  only) code path that ever appends the `ready_to_finalize → completed`
  transition.
- New `IForgeMutations.FinalizeSprintAsync`, implemented identically by
  `ForgeApplication` (local, the real orchestration) and
  `RemoteForgeMutations` (Host round-trip). `ForgeApplication`'s
  constructor gains an `IRepository` dependency for the first time.
- New `ControlProtocol.FinalizeSprintKind`/`FinalizeSprintRequest`; new
  `ControlPlaneHostedService` dispatch handler.
- New `forge finalize` CLI command; new `DiagnosticCodes
  .SprintDefaultBranchUnavailable` for a sprint frozen before
  `DefaultBranch` existed.
- New `workflow.finalize` entry in `capabilities.json`, documented but not
  yet `Implemented` (no Desktop control); `contract_version` bumped to
  `1.5.0`.
- English/Russian RESX localization for the new command.
- Explicitly **not** in this slice, named rather than silently absorbed:
  Desktop parity (`Sprints/Finalize`, named in `capabilities.json`'s own
  entry); an explicit re-check of `SprintDefinition.Dependencies`'
  identities (ADR 0001's own "dependency identities" half of its
  recheck sentence — the fast-forward-only merge itself already enforces
  the "base identity" half natively, since a diverged default branch
  simply fails the merge; dependency-reference re-validation would need
  new machinery — resolving a `Commit`-kind reference's continued
  reachability, an `Artifact`-kind reference's source-sprint terminal
  state — that nothing in this codebase builds yet, and is not the
  common case a first slice needs to justify); a real technical control
  for "human-only" (still the same gap ADR 0019 first named, unrelated to
  this item); rebasing a diverged sprint automatically (the existing,
  separate `SprintGitIsolation.RebaseAttemptAsync`/`forge sprint rebase`
  gap, orthogonal to this item's own merge).

## References

- ADR 0001 (Stage 0 foundation — "finalization rechecks the base and
  dependency identities," the one sentence of prior design this item
  had to work from)
- ADR 0004 (git isolation and circuit breakers — "worktrees live outside
  the project's own working tree," the invariant this item's new
  primitive is the first to ever cross, deliberately and narrowly)
- ADR 0005 (local Host and control plane — the mutation-routing pattern
  this item's dispatch handler follows)
- ADR 0018 (rate-limit deferral and attempt supersession — the generic
  per-node retry budget this item relies on for clean-retry-after-failure
  rather than building its own)
- ADR 0019 (human-gate and supersession CLI commands — the CLI-noun,
  ADR-0023-check, mandatory-confirmation precedent this item extends)
- ADR 0023 (interactive-session detection — the technical control this
  item's command shares with every other human-only command)
- ADR 0034 (confirmation node CLI command — the composition-in-one-call
  pattern this item deliberately does *not* follow, and why)
- ADR 0035 (test-work node CLI command — the immediate sibling this
  item's CLI/wire wiring otherwise mirrors exactly)
