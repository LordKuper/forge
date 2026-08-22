# ADR 0045: Append-only stage revision model

- Status: Accepted
- Date: 2026-08-23

## Context

`docs/plans/desktop-workspace-redesign.md` section 8.4 requires that
rewinding a sprint to an earlier workflow stage "preserves append-only
history and starts a new stage revision. It does not delete or rewrite
prior events, results, findings, decisions, or artifacts." Node identity
must stay stable across a rewind while node execution state gains a
revision, so a query selects "the latest non-superseded revision" instead
of the codebase adding mutable deletion or cloning the sprint to revisit an
earlier stage. Nothing in the current domain model has a revision concept
at all — `NodeSnapshot` and `SprintSnapshot` are single-valued projections
folded once per aggregate from the event log (`WorkflowFold.Apply`). Slice 1
scopes this ADR to the value type and its additive placement; the rewind
coordinator that actually increments a revision and marks evidence
superseded is Slice 3 (plan section 11).

## Decisions

### `StageRevision` is a plain value, not a coordinator

`Forge.Domain.StageRevision(int Value)` is a `readonly record struct` with
a static `Initial` (`Value = 0`) and a pure `Next()` helper. It carries no
behavior beyond incrementing itself — deciding *when* to call `Next()` is
entirely the rewind coordinator's job, introduced later, matching how
`WorkflowStateMachines` owns transition legality while the state enums
themselves are inert data.

### Added additively to `SprintSnapshot` and `NodeSnapshot` only

Both records gain a trailing optional `StageRevision Revision = default`
parameter:

- `SprintSnapshot.Revision` is the sprint's current stage revision — the
  counter section 8.4 point 3 increments on a committed rewind.
- `NodeSnapshot.Revision` is the revision a node's own execution state
  belongs to — "node identity remains stable, while node execution state
  gains a revision."

Both additions are backward-compatible by construction: every existing
construction site (`WorkflowFold.Apply`, the only place either record is
built) uses positional arguments ending before these new trailing optional
parameters, so no call site changes. `default(StageRevision)` equals
`StageRevision.Initial` (`Value = 0`), so every sprint and node folded from
today's event logs — which carry no revision information — reads as
revision 0 without a migration step. Nothing yet increments it: this slice
adds no writer, so every live snapshot stays at `Initial` until Slice 3
ships the coordinator.

### `SupersededBy` names the marker; nothing carries it yet

`Forge.Domain.SupersededBy(StageRevision AtRevision, DateTimeOffset
RecordedAt)` is the "way to mark artifacts/results as superseded by a later
revision" the plan's Slice 1 checklist asks for — but as a named,
reviewed shape only. It is deliberately **not** attached to `NodeResult`,
`Handoff`, or `Finding` in this slice: each of those is a wire-schema-bound
type validated through `WorkflowRecordCodec` against a fixed
`docs/contracts/v1/schemas/*.schema.json` boundary and persisted through
its own explicit `Persisted*` DTO (e.g. `FileSprintEventLog.PersistedNodeResult`).
Attaching a real supersession field to any of them requires updating that
schema, its wire codec, and its persistence DTO together — exactly the
work plan section 11 Slice 3 item 1 assigns ("Add stage revision to node
state and relevant artifacts"), not a records-only contracts slice. Adding
the field to the wire-bound types now, unused, would risk exactly the kind
of speculative, uncalled shape ADR 0014 removed on review for a different
feature ("no real production callers... no real weight to preserve ahead
of a caller that needs it").

### Query semantics deferred, not decided incorrectly

"Queries and artifact lookups select the latest non-superseded revision"
(section 8.4) is a real query-time rule, but no query exists yet that reads
more than one revision of anything — every current reader assumes exactly
one live snapshot per aggregate. Deciding the selection algorithm's shape
now, before `AssessStageTransition` (ADR 0046) and the rewind coordinator
exist to consume it, would guess at an interface neither has constrained
yet.

## What stays deferred

- Incrementing `StageRevision` on a committed rewind, reopening the target
  stage, and recomputing eligible stages from the frozen DAG (Slice 3).
- Attaching `SupersededBy` (or an equivalent marker) to `NodeResult`,
  `Handoff`, `Finding`, and their wire schemas/codecs/persistence DTOs
  (Slice 3).
- The "latest non-superseded revision" query/lookup rule and its exclusion
  of superseded evidence from prerequisite checks (Slice 3, ADR 0046).
- Any Host, CLI, or Desktop surface exposing a node's or sprint's revision
  (Slice 3 backend, Slice 6 Desktop).

## Consequences

- `SprintSnapshot`/`NodeSnapshot` carry a `Revision` field today that is
  always `StageRevision.Initial` in every production path — honestly inert,
  not a partially-wired feature, until Slice 3's coordinator writes it.
- `SupersededBy` exists as an agreed, reviewed name and shape for Slice 3
  to attach, without pre-committing today's four evidence schemas to a
  field shape ahead of the coordinator that would actually populate it.
- No wire schema, codec, or persistence format changed in this slice; no
  migration is needed for existing `.forge/` directories.

## References

- Plan section 8.4 (rewind semantics), section 12.5 (acceptance criteria)
- ADR 0006 (append-only workflow journal as sole source of truth)
- ADR 0014 (precedent for removing a shape with no real caller yet)
