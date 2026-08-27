# ADR 0059: Structured event payloads, first carried by attempt diff statistics

- Status: Accepted
- Date: 2026-08-27
- Contract version: `event.schema.json` 1.1.0; `SprintTimelinePage` 1.2.0; capabilities.json 1.14.0

## Context

`docs/plans/desktop-design-parity-review.md` finding D1: the timeline shows what *state* changed but
never what *work* happened, so a reader cannot tell a one-line edit from a fifty-file rewrite. Finding
C1 asks the sprint header for the same numbers. Both need structured, per-item data that the durable
journal has no way to carry today: `WorkflowEvent.Arguments` is
`IReadOnlyDictionary<string, string?>` — flat, string-valued — and `event.schema.json`'s envelope
declares `additionalProperties: false`, so nothing nested can ride on an event at all.

Exactly one data source for that already exists cleanly. Every implementation attempt runs in its own
worktree and produces its own commit (`SprintGitIsolation.CommitAttemptAsync`), and
`IWorktreeManager.DiffAsync` already reads a two-commit range — but only to assemble a review prompt.
Nothing is persisted or projected from it. Provider tool-call and test-run data, by contrast, is
discarded by the provider adapters entirely and would need new instrumentation before there is
anything to carry.

This ADR therefore does two things at once: it establishes the envelope mechanism, and it fills it
with the one payload family whose data already exists.

## Decisions

### The envelope gains a typed `payload` object, not a `payload_json` string

`event.schema.json` gains an optional `payload` object with `additionalProperties: false` and one
optional typed sub-object per family (`diff` today). The rejected alternative was a single
`payload_json` string property holding an escaped JSON document — attractive because it needs no
schema change per family and no envelope version bump ever again.

It was rejected because it moves every guarantee this repository already relies on out of the schema:

- The journal is schema-validated on **write and read** (`WorkflowEventCodec`). An opaque string is
  validated as "a string", so a malformed or foreign payload would be caught at render time, in
  whichever surface happened to parse it first, rather than at the boundary.
- `SecretRedactor` would then have to parse and re-serialize free-form JSON to redact it. A typed
  sub-object lets both redaction passes walk known string fields explicitly
  (`SprintTimelineRedaction.RedactPayload`).
- Bounds could not be declared. `files` carries `maxItems`, and every count is a non-negative integer,
  in the contract itself.
- A double-encoded string is a poor contract for the `--json` and Host-wire consumers this exists for:
  they would have to parse a string out of the document they just parsed.

The cost of the typed choice is one schema edit per new payload family. That is the point: a family is
a contract change and should look like one.

### `schema_version` becomes an enum, never a bumped `const`

`schema_version` moves from `{"const": "1.0.0"}` to `{"enum": ["1.0.0", "1.1.0"]}`. Every event line
already on disk in every existing sprint carries `1.0.0`, and this schema is re-validated on **read**,
so a bare bump would invalidate every existing journal on the next load — not a migration, a hard
failure. New lines are stamped `1.1.0` unconditionally, payload or not: the version describes the
envelope the producer writes to, not whether one optional field happens to be populated.

### One `AttemptDiffRecorded` event per attempt, never one per file

`FileSprintEventLog` re-reads and re-validates the **entire** journal on every append (`Sequence =
events.Count`). One event per changed file would make an attempt's append cost quadratic in the size
of its own change, on the exact path a large refactor takes. The whole per-file breakdown therefore
rides on a single event's payload.

The event is a non-transition (no `to_state`) recorded on the attempt's own aggregate, and is **not**
folded into any snapshot — the same treatment `AttemptSuperseded` gets, for the same reason: nothing
in the scheduler, an executor, or a prerequisite check ever needs to ask "what did this attempt
change" to decide what happens next. It is timeline and audit content.

It is deduplicated by "this event type already exists for this attempt" (`AppendAttemptSupersededAsync`'s
own idiom), not by a caller-supplied idempotency key: an attempt produces exactly one commit, so a
second call is always a replay.

### Recorded after integration, read before it

`ImplementationExecutionHostedService` reads the statistics **before** `IntegrateAsync` (a successful
integrate discards the attempt worktree the read resolves against) and appends the event **after** it
succeeds. A diff summary for work that never reached the integration branch would be a durable claim
about a change the sprint does not have. A failure to read or append is logged and never fails the
attempt: the change is already integrated and durable by then, so the cost is an audit record, not
work — the same accepted debt `RecordHandoffAsync` already carries at this service's other
post-success write.

An attempt whose net diff is empty is still recorded, as an all-zero payload. "This attempt changed
nothing" is itself worth showing, and skipping it would make an absent record ambiguous between
"empty" and "recorded before this feature existed".

### File paths are normalized, not redacted — and then redacted anyway

`git diff --numstat` reports repository-root-relative, forward-slashed paths by construction, so an
absolute, drive-prefixed, backslashed, or `..`-traversing entry cannot arise from a healthy repository
at all. `SprintGitIsolation.ReadDiffStatAsync` rejects any such entry outright
(`RelativePathShape.IsSyntacticallySafe`, reused from ADR 0009/0012 rather than reinvented) rather
than rewriting it: there is no safe interpretation of it to record. A dropped entry still counts
toward the totals and is added to `elided_files`, so a reader is never told a change was smaller than
it was.

Retained paths are then passed through `SecretRedactor` regardless (ADR 0054/0057's belt-and-braces
rule), before any bounding, since a redaction placeholder can be longer than the text it replaces.
Both timeline redaction passes cover the payload — pass 1 so a future cached projection never holds an
unredacted one, pass 2 because that is the pass every surface's output actually goes through.

### The per-file list is capped at 50, with the remainder counted

`GitWorktreeManagerDiffStatBudget.MaxFiles` is 50, kept in sync by hand with `payload.diff.files`'s own
`maxItems`. It is deliberately separate from `GitWorktreeManagerDiffBudget.MaxCharacters` (50,000),
which sizes a raw diff for a *prompt*: this one bounds how many rows a single durable journal line may
carry.

Only the per-file rows are bounded. `files_changed`, `insertions`, and `deletions` are totals over
**every** changed file, so a large change is never under-reported — the reader sees honest totals plus
an explicit `elided_files` count.

### Summary counts appear in both `arguments` and `payload.diff`

The three scalars are in `arguments` because that is what the localized template
(`workflow.attempt_diff_recorded`) substitutes, and every surface renders a timeline item through
`TimelineMessageFormatter`, which reads `arguments`. They are in `payload.diff` because a machine
consumer reading the structured payload needs the totals the capped `files` list cannot supply.

Drift is prevented structurally, not by discipline: `ISprintStore.AppendAttemptDiffRecordedAsync`
takes only the `DiffPayload` and derives the three arguments from it, so no caller can supply them
independently. `WorkflowFold.IsTransitionRecord` fails closed on an `AttemptDiffRecorded` event
missing either half.

### Two `git diff` invocations, both with `-z`

`--numstat` alone cannot distinguish an added file from a modified one whose change happens to be
additions only, and combining `--numstat` with `--name-status` makes git emit only the latter (verified
against real git). Both run with `-z`, so a path containing a space, a quote, or a non-ASCII byte
arrives verbatim instead of C-quoted, and a rename arrives as two separate fields instead of the
ambiguous `old => new` / `dir/{a => b}` shorthand — a real path may itself contain `=>`.

A binary file (`-`/`-` in `--numstat`, but an ordinary `M` in `--name-status`) is recorded as
`change_kind: "binary"` with zero counts: "how many lines changed", the only question the other kinds
answer, has no answer for it.

## What stays deferred

- **Tool-call and test-run payloads (slice 3).** Provider adapters discard this data entirely today;
  capturing it needs new provider instrumentation and is materially riskier than reading git. The
  envelope is now ready for it: a `tool_use` sibling of `diff` is a schema edit, nothing more.
- **Inline diff hunk content in the UI.** Hunks are deliberately **never** persisted. When a surface
  wants them it will fetch them from git on demand at render time, from the commits this payload
  already names. Only structural statistics are durable.
- **Any Desktop rendering.** `TimelineItemView` carries `Payload` through unchanged and nothing draws
  it. The diff-statistics card and the sprint header's counts (findings D1/C1's rendering halves) are
  separate follow-up work, exactly as ADR 0058 landed the gate contract without moving the gate card.
- **Diff statistics for non-implementation roles.** Only `ImplementationExecutionHostedService` commits
  an attempt's work; planning and review attempts produce no commit to summarize.

## Consequences

- `Forge.Runtime` (`Domain/WorkflowEvents.cs`): `WorkflowEventPayload`, `DiffPayload`, `DiffFileStat`,
  `DiffChangeKinds`; `WorkflowEvent.Payload` (nullable, positional, last, **defaulted**);
  `AttemptDiffRecordedType` plus its three argument constants; a non-folding `WorkflowFold.Apply` arm
  and a fail-closed `IsTransitionRecord` arm.
  The default deviates from ADR 0057/0058's "no default, review every construction site" precedent:
  `WorkflowEvent` is constructed at well over thirty sites, all but one of which have nothing
  structured to carry, so requiring it would produce thirty-odd mechanical `null` arguments and no new
  safety. The fail-closed fold arm is what makes the default safe.
- `Forge.Runtime` (`Application/WorkflowEventCodec.cs`): persisted payload DTOs; stamped version
  `1.0.0` -> `1.1.0`. A null payload is omitted from the line entirely
  (`DefaultIgnoreCondition.WhenWritingNull`), so an event without one is byte-identical to before.
- `Forge.Runtime` (`Application/Abstractions.cs`): `IWorktreeManager.DiffStatAsync`,
  `GitDiffStatResult`, `GitWorktreeManagerDiffStatBudget`, `ISprintStore.AppendAttemptDiffRecordedAsync`.
- `Forge.Runtime` (`Infrastructure/GitDiffStatParser.cs`, `Infrastructure/GitWorktreeManager.cs`,
  `Application/SprintGitIsolation.cs`): the git primitive and its path-safety/redaction wrapper.
- `Forge.Runtime` (`Application/SprintTimeline.cs`): `SprintTimelineItem.Payload`;
  `SprintTimelinePage.ContractVersion` `1.1.0` -> `1.2.0`; `SprintTimelineRedaction.RedactPayload`,
  called by both passes.
- `Forge.Runtime` (`Localization/`): `workflow.attempt_diff_recorded` in `Messages.resx` and
  `Messages.ru.resx`, and its `TimelineMessageFormatter` arm.
- `Forge.Host.Runtime` (`ImplementationExecutionHostedService.cs`): the read-then-record wiring and its
  own warning log event.
- `Forge.Desktop.Presentation` (`SprintTimelineViewModel.cs`): `TimelineItemView.Payload`, passed
  through; no rendering.
- `docs/contracts/v1/schemas/event.schema.json`: `schema_version` enum; `payload` and `$defs.diff_payload`.
- `docs/contracts/v1/capabilities.json`: `1.13.0` -> `1.14.0`; `sprint.timeline` documents the payload.
- No CLI code change: `forge sprint timeline` renders the localized summary through the same
  `TimelineMessageFormatter` it already used, and `--json` serializes the payload through the existing
  `StatusJson` options.
- `VERSION` moves from `0.81.0` to `0.82.0` (MINOR: additive, no breaking change).

## References

- `docs/plans/desktop-design-parity-review.md` findings D1 and C1 (the gap this ADR's data half closes,
  and the rendering half it does not)
- ADR 0054 (why every timeline item is a real journal event, and the two-pass redaction discipline)
- ADR 0057 (redact before bounding; the "last positional" precedent for widening a frozen record)
- ADR 0058 (slice 1 of the same review: land the contract, defer the rendering)
- ADR 0009 / ADR 0012 (`RelativePathShape`, the relative-path safety rules reused here)
