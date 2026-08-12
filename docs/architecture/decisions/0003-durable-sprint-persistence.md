# ADR 0003: Durable sprint/node/attempt persistence

- Status: Accepted
- Date: 2026-08-05
- Revised: 2026-08-12
- Contract version: 1.0.0

## Context

Stage 6 requires durable sprint/node/attempt state with append-only events,
optimistic concurrency, idempotency, and crash recovery, so a sprint resumes
deterministically after a crash without any transcript dependency
(`docs/architecture/overview.md` "Durable workflow"). `overview.md`'s system
boundary diagram had labeled the persistence interface `SQLite/CAS`, but no
database dependency exists anywhere in the repository: every Stage 1/4/5
store (`AtomicConfigurationFile`, `DirectoryFlusher`, `JsonConfigurationStore`,
`YamlConfigurationStore`) is a custom, single-process, write-temp -> fsync ->
atomic-replace file store. This decision was confirmed with the maintainer
before implementation rather than assumed, since it shapes the whole
durable-workflow surface.

## Decision

Sprint/node/attempt state is event-sourced in plain files, not SQLite:

- Every mutation appends one `WorkflowEvent` (mirrors
  `docs/contracts/v1/schemas/event.schema.json`) to
  `<project-root>/.forge/sprints/{sprint-id}/events.jsonl`, one JSON object
  per line. The event log is the sole source of truth; current state is
  always folded from it (`Forge.Domain.WorkflowFold`), never read from a
  separately-trusted cache.
- Appends use the same durability primitive as configuration writes:
  `FileOptions.WriteThrough` plus an explicit `Flush(true)` before the append
  call returns success, followed by `DirectoryFlusher.Flush` to fsync the
  directory entry. Because every prior line was durable before its own append
  returned, a crash can only ever leave the *last* line torn; reading tolerates
  exactly that (a parse failure on the final line only) and rejects corruption
  anywhere else.
- Optimistic concurrency is per-aggregate: an append is accepted only if the
  caller's expected version matches that aggregate's current version folded
  from the log (0 for an aggregate that does not exist yet); otherwise it is
  rejected with `workflow_event_conflict` and no side effect.
- Idempotency uses two ledgers, both atomically written with
  `AtomicConfigurationFile`: a per-sprint `idempotency.json` for transition
  commands (run/cancel/resume — exactly one legal next action per sprint
  version, so the key is deterministically derived the same way
  `InitializeProjectCommand` derives its key), and a project-level
  `.forge/sprints/created.json` for sprint creation (creating a sprint does
  not change the project's own state version, so its idempotency key is an
  opaque caller-supplied token recorded against the sprint it produced).
- No snapshot cache exists yet. Sprint event streams are expected to stay
  small (tens of events), so folding on every read is simpler than maintaining
  a second crash-recovery surface for a cache. This is a deliberate, revisitable
  simplification (`ponytail:` comment on `FileSprintEventLog`), not a
  structural limit — nothing in the event schema or the fold function assumes
  it.
- The same journal records routing decisions and provider outcomes. Retry budget
  and circuit-breaker state are folded from those records; there is no separate
  routing store or routing snapshot. Pre-v0.11 routing sidecars migrate once with
  deterministic event ids and remain read-only rollback evidence.
- Sprint discovery enumerates completed sprint directories through their durable
  creation marker. Project manifest configuration never registers runtime sprint
  ids; legacy `sprints` input is accepted only for read compatibility and omitted
  on the next write.
- Current orchestration leaves failed attempts failed and returns a finding-blocked
  sprint through ordinary `ready -> running -> ready_to_finalize` transitions.
  The published v1 `failed -> abandoned` and `blocked -> ready_to_finalize` edges
  remain legal compatibility paths but are not used by the new orchestration.

`overview.md`'s system boundary diagram is updated from `SQLite/CAS` to one
sprint journal plus immutable artifacts. Artifact payloads remain separate files
addressed by digest; Forge adds no generic CAS service for the MVP.

## Consequences

- Zero new dependencies for Stage 6's persistence core.
- `project.snapshot` (listing/filtering across many sprints, nodes, findings)
  will need to scan and fold per-sprint logs rather than issue a SQL query;
  acceptable at MVP sprint-count scale, revisited if evaluation data shows
  otherwise.
- Store internals do not coordinate multiple writers. ADR 0005 makes this safe at
  the application boundary: one Forge Host owns mutations and holds a shared
  per-project OS lease; a competing host fails before reaching the append path.
  `FileShare.Read` remains a final fail-loudly guard, not the ownership mechanism.
