# ADR 0004: Git worktree isolation, fallback routing, and circuit breakers

- Status: Accepted
- Date: 2026-08-10
- Contract version: 1.0.0

## Context

Stage 7 needs sprint integration worktrees, per-write-attempt worktrees, dirty
recovery, ownership, an integration barrier, gated rebase, provider/model/
surface circuit breakers, cooldowns, a shared retry budget, clean replay, and
auth/policy exclusions (`docs/plans/implementation-plan.md` Stage 7;
`docs/architecture/overview.md` "Provider execution and fallback"). No node
executor exists yet — Stage 6 built the DAG scheduler ahead of its own
executor, and Stage 7 continues that pattern: it builds the Git and routing
primitives a future executor (Stage 10) will call, exercised here directly
against real `git.exe` and a real routing ledger rather than against a fake.

## Decisions

### Worktrees live outside the project's own working tree

Every worktree Stage 7 creates — one sprint integration worktree, one per
node write attempt — lives under
`%LOCALAPPDATA%\Forge\wt\<short-project-id>\<short-sprint-id>\...`
(`Forge.Application.WorktreeLayout`), never inside `<project-root>` itself.
Nesting a linked worktree inside the user's own checkout would need
`.gitignore` coordination and would let an accidental `git add -A` in the
user's repo sweep up an in-progress attempt's content; a location outside the
working tree needs neither. Branch names are deterministic and namespaced
(`forge/sprint/<short-id>`, `forge/attempt/<short-id>`), so no separate
ownership ledger is needed: an attempt's worktree path *is* its ownership
record, and the already-durable event-sourced attempt state from Stage 6
(`SprintGitIsolation.ReconcileAsync`) is enough to tell a live attempt's
worktree from an orphaned one on crash recovery — matched by recomputing each
live attempt's own short id, since the short id embedded in a directory name
cannot be reversed back into the full id it came from (`WorktreeLayout.ShortId`).

### Every id in the worktree/branch layout is short, and `core.longpaths` is set defensively

A worktree path under `%LOCALAPPDATA%\Forge\wt\...` is several directory
levels deeper than the user's own repository. Combined with git's own
administrative files under `.git\worktrees\<name>\...` (used during a
rebase), the total can exceed Windows' path limits even when no individual
segment looks unreasonable — confirmed directly, twice: a gated rebase across
two attempt worktrees at full-GUID-length nested paths reproducibly failed
with `fatal: ... Filename too long` locally until `core.longpaths=true` was
set; separately, attempt-worktree *creation* itself reproducibly failed on
CI (a different Windows machine, `core.longpaths` already set) with an
unhandled `Win32Exception: The directory name is invalid` — .NET's exact
message for a working directory that does not exist, meaning `git worktree
add` itself was failing there at that path depth. Rather than continue
chasing exactly where any one Windows/`git` combination's limit sits,
`WorktreeLayout.ShortId` keeps every id in this layout to the first 16 hex
characters of the underlying GUID (60 bits of actual randomness once the
fixed version nibble inside that prefix is excluded) — collision-safe for a
single user's local worktree cache — so the whole class of failure is avoided by
construction. `core.longpaths=true` (repository-scoped, not system- or
user-wide, set idempotently before every `git worktree add`) is kept as a
second, defense-in-depth layer.

### Deleting a worktree's directory never loses its branch's history

`git worktree prune` clears a stale *path* registration but never touches
the *branch* itself — confirmed directly against real `git`. A naive retry
of `worktree add -b <branch>` after a directory went missing (e.g. the user
emptied a temp/cache location) would therefore fail forever with "a branch
named '<branch>' already exists" — or, if the retry instead force-deleted
that branch to make room, would silently discard whatever it pointed to,
which for the *integration* branch can be every commit integrated into the
sprint so far. `GitWorktreeManager.CreateAsync`'s self-heal path checks
whether the branch still exists after a failed create-and-prune and, if so,
re-attaches a new worktree to that *existing* branch (`git worktree add
<path> -- <branch>`, no `-b`) instead — recovering the worktree without ever
force-deleting a branch. Only a leftover directory that is no longer a
registered worktree at all is ever removed, and only once neither a fresh
branch nor an existing one explains the failure.

### The integration barrier is a fast-forward-only merge with an explicit base check

A sprint's integration branch only ever accepts a fast-forward from an
attempt's branch. `SprintGitIsolation.IntegrateAsync` first re-reads the
integration worktree's actual `HEAD` and compares it against the caller's
`expectedIntegrationTip`; a mismatch fails closed with
`worktree_base_mismatch` and changes nothing — this is the base check. The
merge itself is additionally serialized per sprint (an in-process lock,
matching the single-process assumption `FileSprintEventLog` already makes),
so two attempts finishing at once integrate one at a time rather than racing.
Recovery from a stale base is only ever the explicit
`SprintGitIsolation.RebaseAttemptAsync` — `git rebase --onto <new-tip>
<previous-base>` inside the attempt's own worktree, aborted and reported as
`worktree_rebase_conflict` on the first conflict rather than left mid-rebase.
Nothing here ever resolves a conflict automatically.

### Clean replay is structural, not a policy flag

There is no API to "retry in place." A failed or discarded attempt's
worktree and branch are removed outright
(`SprintGitIsolation.DiscardAttemptAsync`); a new attempt always gets a fresh
worktree branched from the integration worktree's current tip
(`CreateAttemptWorktreeAsync`). A crash-interrupted, still-non-terminal
attempt is left untouched by recovery (`ReconcileAsync`) — only a terminal or
completely unknown attempt's worktree is removed — so recovery can never
discard a worktree that might still be legitimately in use.

"Removed outright" is a goal, not a guarantee `git` itself always keeps (an
open file handle on Windows can make `worktree remove` refuse). Removal is
therefore reported, not assumed: `DiscardAttemptAsync` and `IntegrateAsync`
(which discards the just-merged attempt) return whether cleanup actually
succeeded through `GitOperationResult.CleanupSucceeded`, kept deliberately
independent of the operation's own `Succeeded` — a leaked worktree from a
failed cleanup is not the same failure as a failed merge, and self-heals the
next time `ReconcileAsync` runs for that sprint.

### Circuit breakers and the retry budget are scoped per sprint, not shared

`RoutingLedger` keys a circuit breaker by `HealthKey(Provider, Model,
Surface)` and shares one retry budget across every node/attempt in a sprint,
both durable per sprint (`.forge/sprints/{id}/routing/`) through
`IRoutingStore`. This does not share breaker/budget state across concurrent
sprints on the same project, or across projects — a real provider outage
affecting every sprint identically is a gap this leaves open. Promoting this
to project or user scope is deferred until evaluation data shows flapping
across concurrent sprints is a real problem; every other MVP durable record
(events, findings, handoffs, node results) is already sprint-scoped, and nothing
here needs a new sharing mechanism until that evidence exists.

Once a breaker's cooldown elapses, the next decision moves it to
`half_open` and is routed as a trial; a fixed failure threshold (3) and fixed
cooldown (2 minutes) mirror `SprintScheduler.MaxAutomaticRetries`'s own fixed
policy rather than introducing new configuration surface. An authentication
or policy failure (`FailureClass.Auth` / `FailureClass.Policy`) is recorded
as its own `RouteOutcome.Excluded` decision and never touches the breaker,
and refunds the budget unit it consumed *only* when the decision it responds
to actually was `Routed` — a `RecordOutcomeAsync` call is matched to its
`DecideAsync` result by requiring the caller to pass that exact
`RouteDecision` back, not just its node/attempt/key, so a
`CircuitOpen`/`BudgetExhausted` decision (which never consumed a unit) can
never be refunded into extra budget. Matching `overview.md`'s "Authentication
and policy failures are never disguised as transient failures." Every
budget/breaker read-modify-write is additionally serialized per sprint (one
in-process lock shared by `DecideAsync` and `RecordOutcomeAsync`), the same
single-process guarantee `FileSprintEventLog`'s own per-sprint lock gives its
event log.

### Every routing decision is durable

`RoutingLedger.DecideAsync` and `RecordOutcomeAsync` both append to
`decisions.jsonl` before returning, regardless of outcome — routed, circuit
open, budget exhausted, or excluded. A fallback sequence is reproducible from
this log alone. Reading that log can itself write (truncating a torn
trailing line — see `FileRoutingStore.ReadLinesAsync`'s own remarks), so a
read holds the exact same per-path lock an append does; without that, a read
racing a concurrent append could truncate away an append its own caller had
already been told succeeded. This is a per-process guarantee only, matching
the single-process assumption every other Stage 6/7 file store already makes
(multiple Forge processes writing the same sprint concurrently is out of
scope, same as `FileSprintEventLog`).

## Consequences

- No new external dependency; `GitWorktreeManager` and `FileRoutingStore`
  follow the same real-process and atomic-file-write conventions Stage 1–6
  already established.
- `IWorktreeManager`/`SprintGitIsolation` are exercised in this stage's tests
  against real temporary Git repositories (`git.exe`), not fakes — worktree,
  merge, and rebase semantics are exactly what a fake would risk getting
  wrong silently.
- No CLI/Desktop wiring and no real node executor exist yet; `forge sprint
  rebase` (declared in `docs/contracts/v1/capabilities.json`) and actual
  provider-driven attempt execution remain Stage 10 work, same as the Stage 6
  scheduler waited for its executor.
