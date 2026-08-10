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
`%LOCALAPPDATA%\Forge\worktrees\<project-id>\<sprint-id>\...`
(`Forge.Application.WorktreeLayout`), never inside `<project-root>` itself.
Nesting a linked worktree inside the user's own checkout would need
`.gitignore` coordination and would let an accidental `git add -A` in the
user's repo sweep up an in-progress attempt's content; a location outside the
working tree needs neither. Branch names are deterministic and namespaced
(`forge/sprint/<id>`, `forge/attempt/<id>`), so no separate ownership ledger
is needed: an attempt's worktree path *is* its ownership record, and the
already-durable event-sourced attempt state from Stage 6
(`SprintGitIsolation.ReconcileAsync`) is enough to tell a live attempt's
worktree from an orphaned one on crash recovery.

### `core.longpaths` is set on every worktree creation

A worktree path under `%LOCALAPPDATA%\Forge\worktrees\...` is several
directory levels deeper than the user's own repository. Combined with git's
own administrative files under `.git\worktrees\<name>\...` (used during a
rebase), the total can exceed Windows' default 260-character path limit even
when no individual segment looks unreasonable — confirmed directly: a gated
rebase across two attempt worktrees at realistic nested paths reproducibly
failed with `fatal: ... Filename too long` until `core.longpaths=true` was
set, after which the identical operation succeeded. `GitWorktreeManager`
therefore runs `git config core.longpaths true` (repository-scoped, not
system- or user-wide) before every `git worktree add`; idempotent, so
repeating it is harmless.

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
as its own `RouteOutcome.Excluded` decision and never touches the breaker or
the budget — matching `overview.md`'s "Authentication and policy failures are
never disguised as transient failures."

### Every routing decision is durable

`RoutingLedger.DecideAsync` and `RecordOutcomeAsync` both append to
`decisions.jsonl` before returning, regardless of outcome — routed, circuit
open, budget exhausted, or excluded. A fallback sequence is reproducible from
this log alone.

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
