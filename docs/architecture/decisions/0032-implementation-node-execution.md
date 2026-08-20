# ADR 0032: Implementation node execution

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.0.0

## Context

ADR 0028 shipped `intake` (deterministic, no provider). ADR 0030 shipped
`planning` (the first real provider call, but deliberately makes no commit:
its prompt forbids edits and its worktree is always discarded). ADR 0031
built the missing git-commit primitive (`IWorktreeManager.CommitAllAsync`/
`SprintGitIsolation.CommitAttemptAsync`) ahead of the executor that would
need it. `implementation` is that executor: the `ImplementationCriticalGraphBuilder`
node depending on `planning`, whose whole job — unlike planning's — is
producing a real, committed diff.

## Decisions

### `ImplementationExecutionHostedService`, structurally identical to `PlanningExecutionHostedService`

Same options record, `TickAsync` loop, per-sprint failure isolation
(identical widened catch filter — `ArgumentOutOfRangeException`/
`YamlException`/`JsonException`/`ConfigurationScopeException`/
`Win32Exception`, the same failure surfaces `planning` already needed),
`StartAttemptAsync`/`CompleteAttemptAsync`/`DeferAttemptAsync` dispatch by
an explicit disposition enum (`Succeeded`/`Failed`/`RateLimited`/
`HostShuttingDown`). Registered and started/stopped by
`ControlPlaneHostedService` alongside its two siblings.

`Diagnostic`/`Digest`/`MapProviderFailure` — three helpers `planning`
already had, byte-identical — are extracted into a new internal
`NodeExecutionDiagnostics` (`Forge.Host.Runtime`), used by both executors.
Behavior-preserving, not a design change: the first slice
(`TokenBudgetResolver`, ADR 0030) drew this same line once a second real
caller needed an identical piece; this is the same move for the pieces two
model-bearing executors now share.

### The prompt carries planning's real `Handoff` and invites edits

`planning`'s `Handoff` is fetched directly (`SprintScheduler.GetHandoffsAsync`,
filtered to the sprint's own `planning`-role node id) rather than through
`ContextManifest.Layers.Handoffs`, which stays the permanent MVP stub ADR
0012 already named — the same "bypass the not-yet-built abstraction"
choice `planning` itself made for `.forge/` documents. No `Handoff` for
this sprint means nothing to implement: the node is left untouched at
`ready` (never started) rather than running an attempt with no plan to
follow — checked before `StartAttemptAsync`, matching every other
missing-precondition guard in this file. The prompt's own header inverts
planning's: it explicitly invites editing, creating, and deleting files,
and tells the provider not to commit its own changes ("Forge commits them
for you").

### Dirty-check gates whether anything is committed at all

After the provider run succeeds, `IWorktreeManager.IsDirtyAsync` is
checked before anything else. A clean tree — the provider ran, reported
success, but left no edit — is a recorded failure
(`DiagnosticCodes.ImplementationNoChanges`, new), not a silent no-op or a
degraded success: a role whose whole job is producing an edit that
produces none has failed at that job, the same reasoning
`SprintGitIsolation.CommitAttemptAsync`'s own doc comment already states
as the caller's responsibility to enforce. Only a dirty tree reaches
`CommitAttemptAsync`.

### Commit, then integrate; a stale base is a clean-replay failure, not an in-place rebase

`CommitAttemptAsync`'s message is the provider's own terminal summary's
first line (bounded to 200 characters — long enough to be useful, short
enough that a verbose provider never produces an unreasonable `git log`
entry), falling back to a fixed message when the summary is blank —
unlike `planning`, an empty terminal summary is not itself a failure here,
since the committed diff is the substantive product regardless of what
text accompanied it.

A successful commit is followed by `SprintGitIsolation.IntegrateAsync`
against the integration tip recorded when the attempt's worktree was
first ensured. `IntegrateAsync` already discards the attempt worktree
itself on success; this executor discards explicitly only on a failed
integrate (the one path where `IntegrateAsync` does not clean up after
itself). A stale base (`worktree_base_mismatch` — something else
integrated into this sprint since the attempt worktree was created) is
**not** resolved in place with the already-built
`SprintGitIsolation.RebaseAttemptAsync`: the attempt is discarded and
failed, and the scheduler's own bounded auto-retry mints a fresh attempt
against the now-current tip on the next tick. Deliberately the simpler
choice for a first slice — the built-in graph gives `implementation` no
sibling that integrates concurrently with it today, so this path is a
defensive guard against a currently-rare race, not a commonly exercised
one, and automatic rebase-and-retry-in-place would add real new
conflict-handling surface this slice does not need to justify yet.

### The output digest is of the integrated commit id, not the commit id itself

`node-result.schema.json`'s `outputs` pattern requires `^sha256:[0-9a-f]{64}$`
— a raw git object id (SHA-1, 40 hex characters, no vendor prefix) does
not match it. The recorded output is `NodeExecutionDiagnostics.Digest`
applied to the integrated commit id string: an indirect but
schema-compliant content-addressed handle to what this attempt produced,
the same "digest of substantive content" shape `planning`'s own output
(a digest of its terminal summary text) already established — the real
commit id itself is discoverable from the sprint's integration branch
state, not by decoding the `NodeResult`.

### A resumed attempt re-invokes the provider from scratch; no partial-edit inspection

Same accepted trade-off as `planning`'s crash-resumability: this class
does not inspect an attempt worktree left dirty by an interrupted run
before resuming. The resumed run's own `git add -A` simply restages and
re-commits whatever is present — the retried provider's own edits plus
anything a crashed prior run already wrote — which is the honest behavior
for a worktree this class does not otherwise track between ticks, not a
correctness gap it silently papers over.

### `implementation` records its own `Handoff` for `confirmation`

On success, `RecordHandoffAsync` names the sprint's `confirmation`-role
node as `NextNodeIds`, `Summary` the provider's terminal text (or the
fixed fallback). `confirmation`'s own executor does not exist yet — this
sets up real data for whenever it is built, the same "produce it now,
consume it later" precedent `planning`'s own handoff already established
for `implementation` itself.

## Consequences

- New `src/Forge.Host.Runtime/ImplementationExecutionHostedService.cs` and
  `ImplementationExecutionOptions`; registered in `ForgeHostApplication`
  and `ControlPlaneHostedService` alongside its two siblings.
- New `src/Forge.Host.Runtime/NodeExecutionDiagnostics.cs`, extracted from
  `PlanningExecutionHostedService` (behavior-preserving); that file now
  calls the shared helpers instead of its own private copies.
- New `DiagnosticCodes.ImplementationNoChanges`
  (`implementation_no_changes`).
- First production caller of `SprintGitIsolation.CommitAttemptAsync` and
  `IntegrateAsync`, and of `SprintScheduler.GetHandoffsAsync`.
- `implementation`'s own `Handoff` is `confirmation`'s first real input,
  once that executor exists.
- Explicitly **not** in this slice, named rather than silently absorbed:
  the `review` executor (needs lineage-independence handling
  `ExecutionProfilePolicy` already freezes but nothing yet reads);
  `SprintGitIsolation.RebaseAttemptAsync` wired to an automatic
  stale-base recovery (deliberately deferred above); structured
  decision/risk extraction from provider output into
  `Handoff.Decisions`/`OpenRisks`; inspecting or restaging a
  crash-interrupted attempt's own partial edits more carefully than
  "restage everything present"; `forge sprint rebase` and the two
  re-scoped snapshot fields (integration status, phase profile — still
  blocked on `confirmation`/`test_work`/`review`/`finalization` executors
  existing); and a real unforgeable caller-identity mechanism (unrelated,
  still open from ADR 0023).

## References

- ADR 0004 (worktree isolation)
- ADR 0006 (supervised execution)
- ADR 0009 (Forge document format)
- ADR 0012 (reproducible context assembly — the `Handoffs` layer stub this
  executor bypasses, same as `planning` already bypassed it for documents)
- ADR 0030 (planning node execution — the sibling executor this one
  mirrors structurally, and the `TokenBudgetResolver` extraction precedent
  this item's own `NodeExecutionDiagnostics` extraction repeats)
- ADR 0031 (attempt worktree commit primitive — first production caller)
