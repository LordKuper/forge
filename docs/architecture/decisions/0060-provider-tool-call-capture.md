# ADR 0060: Provider tool-call capture, mapped only from a real recorded stream

- Status: Accepted
- Date: 2026-08-27
- Contract version: `event.schema.json` 1.2.0; `SprintTimelinePage` 1.3.0; capabilities.json 1.15.0

## Context

ADR 0059 built the `payload` envelope and filled it with the one family whose data already existed
(git diff statistics), explicitly deferring tool-call capture as "slice 3" because "provider adapters
discard this data entirely today; capturing it needs new provider instrumentation." This ADR is that
slice. It fills the `tool_use` sibling ADR 0059 sized the envelope for, and changes nothing about the
envelope's own design.

The timeline can now say what an attempt *changed*. It still cannot say what the agent *did* — whether
a one-file change came from a single edit or from twenty commands and eleven rewrites. That
distinction is the difference between a summary a reader trusts and one they have to go read git to
interpret.

## Decisions

### The mapping was built from a real recorded stream, and from nothing else

A prior planning pass produced a speculative mapping table and a fixture written to match it. Both
were wrong: the repository's only Codex fixture was an unverified guess at the event shape, and the
mapping derived from it named subtypes at a JSON path that does not exist.

The correction was to run the real thing. `codex exec --json` (Codex CLI 0.149.1) was driven against a
throwaway git worktree — read a file, run a command, edit a file — and its stdout captured verbatim as
`tests/Forge.Tests/Unit/fixtures/providers/codex-exec-json-tool-calls.jsonl`, with only the captured
absolute path replaced by a placeholder. Every mapping decision below is traceable to a line in that
file, and the adapter test that pins the mapping runs against that file rather than against a
hand-written approximation.

What the capture actually establishes:

- The wrapper's `type` is the lifecycle marker (`item.started`/`item.completed`) — which is what
  `CodexLlmProvider.Classify`'s existing `StartsWith("item.")` check already matched, so classification
  is untouched. The tool subtype is nested one level deeper, at `item.type` — the same field *name* as
  the wrapper's own, which is precisely why an unverified reading of it is easy to get wrong.
- Exactly two tool-call subtypes appear: `command_execution` and `file_change`.
- `item.id` is a string present on both the start and the completion of the same call.
- `file_change`'s `changes` is an **array** (every observed instance held one entry).
- `command_execution` carries `command` and `aggregated_output` in full.

Subtypes that appear in vendor documentation or in the earlier speculation — `mcp_tool_call`,
`web_search`, `patch_apply`, a file-read subtype — are **deliberately left unmapped**, not guessed.
An unmapped subtype is not silently lost: it increments the drift counter below, which is exactly the
signal that would tell us a new capture is worth taking.

### Command text and command output are never persisted, in any form

`command` is a full shell command line and `aggregated_output` is its complete stdout. Both routinely
contain secrets — an `Authorization:` header, an inline environment assignment, a token the command
printed. ADR 0006's no-raw-content rule forbids persisting either, and `SecretRedactor` is not an
answer here: it is a pattern matcher over text that is *supposed* to be recorded, not a licence to
record arbitrary command lines.

`CodexLlmProvider.ExtractToolCall` therefore reads only specifically named fields and never enumerates
an item generically. A command is recorded as the bare fact that one ran, plus `exit_code` and a
derived `succeeded` (a `completed` status with exit code zero is true, `completed` with a non-zero code
is false, anything else stays unknown). It carries **no target at all**: the only text that would
identify *which* command is the command line itself, so there is no safe abbreviation to record — a
truncated command line is still a command line.

The invariant is pinned by a test that feeds a synthetic completion carrying a credential-bearing
command and a marker-bearing output, then asserts neither string appears in the resulting
`ProviderToolCall` **or in the real journal line written to disk** — not merely in the typed record, so
a field added at any layer between the adapter and the file cannot reintroduce the leak unnoticed.

### Three outcomes when classifying an item, not two

A vendor stream interleaves genuine tool calls with ordinary model narration. An earlier draft of this
slice had only two buckets — mapped, or unmapped-and-counted — which would have counted every
`agent_message` as drift and made the drift counter non-zero on every healthy run. A signal that fires
constantly is not a signal.

`ProviderToolCallExtraction` therefore expresses three outcomes:

- **Extracted** — a known tool-call subtype; produces one or more `ProviderToolCall` rows.
- **Ignored** — known, recognized content that is simply not a tool call (`agent_message`, and
  `reasoning`, allowlisted defensively as an unambiguous documented sibling). No row, and explicitly
  **no drift increment**.
- **Unmapped** — a shape this adapter's mapping does not cover at all. No row; increments
  `unmapped_items`, which rides on the durable payload.

`unmapped_items` counts ITEMS, not stream lines, and Codex describes one item on two lines
(`item.started` then `item.completed`). An unmapped verdict therefore carries the item's correlation
id when the adapter could read one, and the sink deduplicates on it — reusing the pairing the duration
already relies on — so a start/completion pair for one unrecognized item counts once. A line with no
readable id has nothing to deduplicate on and always counts on its own: under-reporting real drift
would be a worse failure than the double count.

The extraction result carries a *list* of candidates rather than one, because `file_change`'s `changes`
is an array: every entry becomes its own row sharing the item's correlation id (and therefore its
duration), so an entry past the first is never silently dropped. An empty or wholly malformed
`changes` array reports Unmapped rather than Extracted-with-nothing — the adapter recognized the
subtype but not the shape inside it, and that is drift.

### The adapter returns raw vendor data; the core owns normalization and safety

ADR 0008's split, applied literally. `ProviderToolCallCandidate.RawTarget` is whatever the vendor wrote
(Codex reports an absolute, OS-native path). `ProviderExecution.NormalizeToolCallTarget` — core-side,
because `RelativePathShape` is `Forge.Runtime`-internal and because path safety is policy rather than
vendor translation — relativizes it against the attempt's working directory, forward-slashes it, and
applies `RelativePathShape.IsSyntacticallySafe`.

A path that fails is **rejected, never rewritten**, reusing ADR 0059's rule for diff paths verbatim:
there is no safe interpretation of a path that escapes the worktree. The call itself is still recorded,
just with no target, so a rejected path shrinks the detail, never the count. The surviving path is
redacted before any bounding (ADR 0057/0059), since a placeholder can be longer than what it replaces.

Normalization is fail-open in full, not for one exception type: it runs on the stdout pump, so anything
escaping it faults that pump and fails an otherwise-successful attempt. `Path.GetRelativePath` throws
on more than the obvious embedded null character — a rooted path at or past ~32,767 characters raises
`PathTooLongException` on Windows, well inside the 1 MiB frame bound — and enumerating every exception
a path API may raise across three operating systems is exactly the guess the catch refuses to make. Any
of them is treated as "no usable target"; only a genuine cancellation still propagates.

`kind` (`"update"`, ...) on a `file_change` entry is deliberately **not** read. ADR 0059 could define a
closed `DiffChangeKinds` vocabulary because `git diff --name-status`'s statuses are a documented,
stable, exhaustive set; a single capture of one vendor's output is not a basis for the equivalent
claim, and a half-defined enum on a durable envelope is worse than no enum.

### Duration is Forge-observed, never vendor-reported

Neither vendor publishes a timing field, and this ADR does not invent one. `item.id` pairs a start with
its completion **in memory only** (the raw vendor id is never persisted), and the duration is
`Stopwatch`-measured wall time between the two lines arriving on stdout. It is therefore Forge's
observation of the call, including transport and scheduling, not the vendor's measurement of its own
work — and it is null when no matching start was seen. `turn.completed`'s `usage` token counts are out
of scope entirely.

### Fail-open on both the capture path and the write path

Tool-call capture is optional enrichment. Every failure mode fails open:

- An adapter extractor that **throws** is caught inside `BoundedOutputSink`, counted as drift, and the
  run continues. A vendor-shape surprise must never fail an attempt.
- An adapter that returns a kind outside the closed set is treated as drift, not fabricated onto the
  envelope. An adapter is untrusted input like any other.
- The journal append copies `RecordAttemptDiffAsync`'s exception filter **exactly**, including the
  `OperationCanceledException` `FileSprintEventLog`'s own per-sprint gate raises, with a carve-out that
  rethrows only a cancellation of the method's own token (a real Host shutdown). This is not stylistic
  symmetry: the write sits in the identical risk position, after a successful integrate but before
  `CompleteAttemptAsync`, and PR #116's review found exactly this class of bug twice on the diff path.
  The regression test was mutation-verified — narrowing the filter back to
  `IOException`/`UnauthorizedAccessException`/`InvalidDataException` makes it fail with the attempt
  stranded in `running`; restoring the filter makes it pass.

### Nothing is recorded for a run that observed nothing

Unlike ADR 0059's diff record — where an all-zero payload meaningfully says "this attempt changed
nothing" — an absent tool-call record and an all-zero one are not equivalent here, because most
providers do not extract tool calls at all. An all-zero record from a Claude attempt would be a durable
claim that the agent ran no commands, which is false. So the append is skipped when there are zero
calls **and** zero unmapped items.

A non-zero unmapped count with zero mapped calls **is** recorded: that is the drift signal, and it is
worth being durable precisely when the mapping produced nothing.

### One event per attempt, non-folding, deduplicated by type

Identical to `AttemptDiffRecorded` and for the identical reasons: `FileSprintEventLog` re-reads the
entire journal on every append, so per-call events would make an attempt's append cost quadratic in its
own chattiness; the event is a non-transition on the attempt's own aggregate and is never folded,
because nothing in the scheduler, an executor, or a prerequisite check decides anything from what tools
an attempt used; and it is deduplicated by "this event type already exists for this attempt", since an
attempt runs its provider exactly once and a second call is always a replay.

The per-call list is capped at `ProviderToolUseBudget.MaxCalls` (50), matching
`payload.tool_use.calls`'s own `maxItems`, with `ContractTests.TheEventSchemasToolCallCapMatchesTheBoundTheProducerActuallyApplies`
reading both actual sources and failing if they drift. Only the rows are bounded; `tool_calls`,
`commands`, and `edits` are totals over every observed call, with the remainder in `elided_calls`.

The sink carries a second, larger cap of its own — `ProviderExecution.MaxRetainedToolCalls`, four
times the durable one — because `MaxEventCount` (20,000) does **not** bound the row list: one
extraction returns a *list* of candidates, and a `file_change` completion becomes one row per
`changes` entry, so a single line fans out into as many rows as that array holds inside the 1 MiB
frame bound. "Every entry originates from one retained event" was never true of this design and is not
the bound in place. Capping the rows costs the durable record nothing: it is larger than the durable
cap, and `ProviderRunResult.ToolCallTotals` counts each call *before* the retention check — the payload
reads every total and `elided_calls` from those counters, never from the retained list — so a call past
the cap is elided, never silently subtracted from the totals. The sink's two in-memory id collections
(pending starts for duration pairing, and the ids already counted as drift) do not follow event count
either, and are capped at `MaxEventCount` entries each: a dropped pending start costs only a duration
the record already declares nullable, and a full drift set stops deduplicating new ids, which can
over-count drift but never under-count it — the direction an id-less line already accepts.

### `WorkflowEventPayload`'s new member is required, with no default

ADR 0059 gave `WorkflowEvent.Payload` a default and said so explicitly, because that record has well
over thirty construction sites. `WorkflowEventPayload` itself has three — the two producing store
methods and the codec's read path — so ADR 0057/0058's "review every construction site" discipline is
affordable here and is applied. A third family must be considered at every existing producer rather
than silently defaulting to absent.

The codec's payload mapping also stops being an either/or. `payload?.Diff is not { } ? null : ...` was
correct while `diff` was the only family; with two, an early return keyed on one would silently discard
the other. Both `ToPersisted`/`FromPersisted` and `SprintTimelineRedaction.RedactPayload` are now
per-family — and the redaction test for `tool_use` is deliberately a separate test rather than an
extension of the diff one, because a helper that returns early on whichever family it checks first
would still pass the diff test.

## What stays deferred

- **Claude tool-call capture.** A separate, larger slice: `ClaudeLlmProvider.Classify` cannot currently
  return `ProviderEventKind.ToolUse` at all, so there is no line for an extractor to be handed. The
  contract is provider-neutral and the `extractToolCall` parameter is a trailing optional argument
  precisely so that slice needs no change here.
- **Planning and review roles.** Only `ImplementationExecutionHostedService` records this, matching ADR
  0059's own scope: those roles' attempts are not what this summary is about.
- **Failed, timed-out, and stopped attempts.** `ProviderRunResult.Failed` carries no tool-call data and
  the executor never reaches the write site on those paths — the same rule ADR 0059 set for diff
  statistics, for the same reason: their work never reached the integration branch.
- **Any Desktop rendering.** `TimelineItemView` carries `Payload` through unchanged and nothing draws
  it, exactly as ADR 0059 left it.
- **Every unverified Codex subtype.** `mcp_tool_call`, `web_search`, and `patch_apply` are named here
  specifically so a future reader knows they were considered and deliberately not guessed.

## Consequences

- `Forge.Runtime` (`Providers/ProviderExecution.cs`): `ProviderToolCallKinds`,
  `ProviderToolCallOutcome`, `ProviderToolCallCandidate`, `ProviderToolCallExtraction`,
  `ProviderToolCall`, `ProviderToolCallTotals`, `ProviderToolUse.ToPayload`;
  `ProviderRunResult.ToolCalls`/`UnmappedItemCount`/`ToolCallTotals` (positional, last, with `Success`'s
  three new arguments optional so unrelated call sites are unchanged, and the totals derived from the
  list for every producer that never capped it); `MaxRetainedToolCalls`; `RunAsync`'s trailing optional
  `extractToolCall`; `BoundedOutputSink`'s accumulation, start/completion pairing, and drift counting
  under its existing lock; `NormalizeToolCallTarget`.
- `Forge.Providers.Codex.Windows` (`CodexLlmProvider.cs`): `ExtractToolCall` and its helpers, wired as
  `RunAsync`'s new argument. `Classify` and the text extractor are untouched.
- `Forge.Runtime` (`Domain/WorkflowEvents.cs`): `ToolCallStat`, `ToolUsePayload`;
  `WorkflowEventPayload.ToolUse` (required, no default); `AttemptToolUseRecordedType` plus its three
  argument constants; a non-folding `WorkflowFold.Apply` arm and a fail-closed `IsTransitionRecord` arm.
- `Forge.Runtime` (`Application/WorkflowEventCodec.cs`): per-family payload mapping; stamped version
  `1.1.0` -> `1.2.0`. Nullable per-call fields are omitted rather than written as explicit nulls, which
  is why `tool_use_payload` requires only `kind` per row.
- `Forge.Runtime` (`Application/Abstractions.cs`): `ProviderToolUseBudget`,
  `ISprintStore.AppendAttemptToolUseRecordedAsync`.
- `Forge.Runtime` (`Application/FileSprintEventLog.cs`): that method, mirroring
  `AppendAttemptDiffRecordedAsync`'s dedup and derive-the-arguments discipline.
- `Forge.Runtime` (`Application/SprintTimeline.cs`): a `ToolUse` arm in
  `SprintTimelineRedaction.RedactPayload`, called by both passes; `SprintTimelinePage.ContractVersion`
  `1.2.0` -> `1.3.0`.
- `Forge.Runtime` (`Application/StartupContracts.cs`): `DiagnosticCodes.ProviderToolUseUnavailable`.
- `Forge.Runtime` (`Localization/`): `workflow.attempt_tool_use_recorded` in `Messages.resx` and
  `Messages.ru.resx`, and its `TimelineMessageFormatter` arm. Every substituted value is a plain
  number, so no closed-set label needs localizing.
- `Forge.Host.Runtime` (`ImplementationExecutionHostedService.cs`): `RecordAttemptToolUseAsync` and its
  own warning log event (2059).
- `docs/contracts/v1/schemas/event.schema.json`: `schema_version` gains `1.2.0`; `payload.tool_use` and
  `$defs.tool_use_payload`.
- `docs/contracts/v1/capabilities.json`: `1.14.0` -> `1.15.0`; `sprint.timeline` documents the second
  payload family.
- No CLI code change and no Desktop code change: both render the new item through the same generic
  timeline paths ADR 0059 already proved out. The CLI acceptance test exists precisely to keep that
  claim honest.
- `VERSION` moves from `0.82.0` to `0.83.0` (MINOR: additive, no breaking change).

## References

- ADR 0059 (the `payload` envelope this fills, and the rules it restates: honest totals plus explicit
  elision, reject-don't-rewrite paths, redact before bounding, fail-open on the audit write path)
- ADR 0006 (no raw provider content in durable state; bounded stream consumption)
- ADR 0008 (the adapter/core split this ADR applies to normalization)
- ADR 0009 / ADR 0012 (`RelativePathShape`, reused rather than reinvented)
- PR #116's review (findings 1 and 2: the two fail-open regressions on the diff write path this slice
  must not reintroduce)
