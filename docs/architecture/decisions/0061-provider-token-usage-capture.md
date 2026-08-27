# ADR 0061: Provider token-usage capture, with an honest context window or none at all

- Status: Accepted
- Date: 2026-08-27
- Contract version: `event.schema.json` 1.3.0; `SprintTimelinePage` 1.4.0; capabilities.json 1.16.0

## Context

ADR 0059 built the `payload` envelope and filled it with git diff statistics; ADR 0060 filled the
`tool_use` sibling from a real recorded Codex stream. This is the third slice on the same envelope and
changes nothing about its design.

`docs/plans/desktop-design-parity-review.md` finding E3 reads "project `TokenBudget` exists but
consumption is not reported," pointing at the design mockup's `ctx 41k / 200k` composer indicator.
Investigation found the premise misleading and the finding worth building anyway, for a different
reason. `context.token_budget` is Forge's own **document-assembly** budget — how much `.forge/` content
gets packed into a prompt — and has nothing to do with a model's context window. Reporting it against
a context-window denominator would be a category error dressed as a metric.

What is real: both provider CLIs already emit genuine per-attempt token accounting on their terminal
stream event — the same event `ProviderEventKind.Result` already classifies and ADR 0006's uniqueness
rule already singles out — and Forge discards all of it. That is the buildable version of E3, and it is
what this ADR captures.

## Decisions

### Both mappings come from real captures, and from nothing else

ADR 0060's rule, applied again. Two captures, both committed:

- **Codex** — `tests/Forge.Tests/Unit/fixtures/providers/codex-exec-json-tool-calls.jsonl`, the same
  capture ADR 0060's tool-call mapping was built from (Codex CLI 0.149.1). Its last line is
  `turn.completed`, whose `usage` carries `input_tokens`, `cached_input_tokens`,
  `cache_write_input_tokens`, `output_tokens`, `reasoning_output_tokens`.
- **Claude** — `tests/Forge.Tests/Unit/fixtures/providers/claude-stream-json-usage.jsonl`, captured for
  this slice from Claude Code 2.1.233 driven against a throwaway worktree, with the captured absolute
  path and account identifiers replaced by placeholders. Its terminal `result` line carries `usage`
  (`input_tokens`, `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens`) and a
  `modelUsage` object keyed by the exact model string that ran.

Both adapter tests run against those files rather than hand-written approximations.

### Codex has no context-window denominator, and that is reported as absent

Codex's usage object contains no context-window field of any kind. A `ctx X / Y` reading therefore has
no honest `Y` for a Codex attempt, and `ProviderUsage.ContextWindow` stays null for that provider —
never filled in from a per-model lookup table Forge would have to guess at, hardcode, and then keep
current as vendors ship new models. An absent denominator is a fact a surface can render around; a
wrong one silently corrupts every reading built on it.

Claude's `modelUsage` entry does carry `contextWindow`, so a Claude attempt gets a real one.

Codex's two cache counters (`cached_input_tokens`, `cache_write_input_tokens`) are deliberately left
unmapped. Whether they mean the same thing as Claude's `cache_read_input_tokens` /
`cache_creation_input_tokens` is not something one capture per vendor establishes, and asserting an
equivalence onto a shared durable field would be exactly the guess this slice refuses to make. Codex's
`reasoning_output_tokens` is likewise unmapped: its relationship to `output_tokens` (a subset? a
sibling?) is undocumented, and a field whose arithmetic is unknown cannot be summed into anything.

### `ProviderUsage` is flat, never a per-model map

Claude's CLI reports usage as `modelUsage`, a dictionary keyed by model string
(`"claude-opus-5[1m]"`). That shape exists because the vendor's own session may span models; a Forge
**attempt** runs exactly one. Preserving the dictionary would put a vendor implementation detail on a
durable Forge contract and force every consumer to handle a case that cannot arise.

`ProviderUsage` and `UsagePayload` are therefore flat records of five nullable integers. The adapter
extracts the single entry and reports its `contextWindow`.

A `modelUsage` with zero entries, or with more than one, yields `ContextWindow = null` rather than a
pick. This codebase has never observed a multi-entry `modelUsage`; choosing "the first" or "the largest"
of several would be a guess presented as a measurement, and the whole point of the field is that it is
the honest denominator.

### Every field is nullable, and absence is never zero

A provider may publish none, some, or all of these counts. `null` means "not reported" and `0` would
mean "reported as zero" — different facts, kept different. A vendor number that is missing,
non-numeric, fractional, out of `int` range, or negative is treated as not-reported rather than coerced:
the durable contract declares each count a non-negative integer, and a value that is not one is not a
clamped version of the truth.

The one place the distinction is collapsed is the localized one-line summary
(`workflow.attempt_usage_recorded`), whose `total_tokens`/`input_tokens`/`output_tokens` arguments
substitute 0 for an unreported half. `UsagePayload` itself keeps the null, so a machine consumer
reading the structured payload can always still tell the two apart — and the record is not written at
all unless something was reported, so that summary can never be an all-zero line standing in for an
observation that never happened.

### Nothing is recorded for a run that observed nothing

ADR 0060's rule, not ADR 0059's. An all-zero **diff** payload meaningfully says "this attempt changed
nothing." An all-null usage payload says nothing at all, and cannot even be read as "this attempt spent
no tokens" — no provider reports a zero-token turn. `ProviderUsageReport.ToPayload` returns null when
the usage observation is absent or every field on it is, and the write site skips the append.

### Read from the terminal event the uniqueness check already found

There is no second scan and no new notion of "the terminal event." `BoundedOutputSink` already
increments `TerminalCount` and latches `TerminalResult` on the first `ProviderEventKind.Result` line;
usage is latched from that same line, under the same lock, guarded by the same `is null` idiom. A run
emitting more than one terminal result fails closed as `DuplicateTerminalResult` before any caller
reads usage, so "first" and "only" are the same event on every path that reaches a caller.

Unlike the tool-call slice this needs no start/completion pairing, no fan-out over an array, no
retention cap, no path normalization, and no redaction surface: exactly one event per attempt carries
usage, and every field on it is a plain number.

### `rate_limit_event` is a non-interference check, and nothing more

The Claude capture contains a mid-stream `{"type":"rate_limit_event","rate_limit_info":{...}}` line.
`ClaudeLlmProvider.Classify` returns `ProviderEventKind.Unknown` for it (verified, and pinned by a
test), so it never reaches the usage extractor at all; `ExtractUsage`'s own `type == "result"` check is
a second, independent reason it could not be mistaken for the terminal event. A test feeds the real
fixture through the whole sink and asserts that the `rate_limit_event` produces no usage and does not
displace the genuine terminal `result` that follows it.

That is the entire relationship between this slice and provider quota signalling (parity review finding
B7). The two ride on the same stream and are otherwise unrelated concerns; conflating them is
explicitly out of scope here.

### Fail-open on both the capture path and the write path

Identical to ADR 0060, for identical reasons:

- An adapter extractor that **throws** is caught inside `BoundedOutputSink.RecordUsage`, leaves `Usage`
  null, and the run continues. It runs on the stdout pump, so an escaping exception would fault that
  pump and fail an otherwise-successful attempt. There is no drift counter to increment here — a usage
  object is a leaf of one known event, not a stream of items whose shapes could stop being recognized.
- The journal append copies `RecordAttemptDiffAsync`/`RecordAttemptToolUseAsync`'s exception filter
  **exactly**, including the `OperationCanceledException` `FileSprintEventLog`'s own per-sprint gate
  raises, with a carve-out rethrowing only a cancellation of the method's own token (a real Host
  shutdown). This write sits in the identical risk position — after a successful integrate, before
  `CompleteAttemptAsync` — and this exact class of bug has been found and fixed three times across the
  prior two slices' reviews. The regression test is mutation-verified: narrowing the filter back to
  `IOException`/`UnauthorizedAccessException`/`InvalidDataException` makes it fail with the attempt
  stranded in `running`; restoring the filter makes it pass.

### One event per attempt, non-folding, deduplicated by type

Identical to both siblings. Here it is not merely an append-cost decision (`FileSprintEventLog`
re-reads the entire journal on every append) but the shape of the data itself: each provider reports
usage on exactly one terminal event per run. The event is a non-transition on the attempt's own
aggregate, never folded — nothing in the scheduler, an executor, or a prerequisite check decides
anything from what an attempt spent — and it is deduplicated by "this event type already exists for
this attempt", since a second call is always a replay of the same finished run.

### `UsagePayload` needs no redaction arm, and that is a decision

`SprintTimelineRedaction.RedactPayload`'s doc comment requires every new **string** field on a payload
sub-object to be walked explicitly. `UsagePayload` has none: five nullable integers, no free text, no
closed-set label, no path. A no-op arm rewriting each number onto itself would imply per-field
consideration where there is nothing per-field to consider, so none is added; the method's closing
`payload with { ... }` carries `Usage` through untouched by construction. Both the omission and its
trigger to reverse (should the family ever gain a model name or service tier) are stated in that doc
comment, and a test asserts a usage payload survives both passes intact.

## What stays deferred

- **Per-sprint cumulative token spend.** This slice is strictly per-attempt. Aggregating across an
  entire sprint — and deciding what that number means across retried, superseded, and cancelled
  attempts — is separate work.
- **Provider quota and rate-limit signalling (finding B7).** A completely separate concern despite
  riding on the same stream; see above. ADR 0052 found no verified quota signal in either integration
  and that conclusion is untouched here.
- **Codex's cache and reasoning counters**, named above so a future reader knows they were seen and
  deliberately not mapped.
- **A `ctx X / Y` composer indicator.** The denominator now exists durably for Claude attempts; drawing
  it is rendering work, deferred exactly as ADR 0059/0060 deferred theirs.
- **Failed, timed-out, and stopped attempts.** `ProviderRunResult.Failed` carries no usage and the
  executor never reaches the write site on those paths — ADR 0059/0060's scope boundary, unchanged.
- **Planning and review roles.** Only `ImplementationExecutionHostedService` records this, matching both
  siblings.
- **Any Desktop rendering.** `TimelineItemView` carries `Payload` through unchanged and nothing draws
  it.

## Consequences

- `Forge.Runtime` (`Providers/ProviderExecution.cs`): `ProviderUsage` (with `HasAnyValue`),
  `ProviderUsageReport.ToPayload`; `ProviderRunResult.Usage` (positional, last, with `Success`'s new
  argument optional so unrelated call sites are unchanged); `RunAsync`'s trailing optional
  `extractUsage`; `BoundedOutputSink.Usage` and its fail-open `RecordUsage`, latched off the existing
  terminal-result branch under the existing lock.
- `Forge.Providers.Claude.Windows` (`ClaudeLlmProvider.cs`): `ExtractUsage`, `ExtractContextWindow`,
  `NonNegativeInt32`, wired as `RunAsync`'s new argument. This adapter still passes
  `extractToolCall: null` (ADR 0060's deferral), named rather than positional so the skipped slot is
  explicit. `Classify` and the text extractor are untouched.
- `Forge.Providers.Codex.Windows` (`CodexLlmProvider.cs`): `ExtractUsage`, `NonNegativeInt32`, wired
  beside the existing `ExtractToolCall`.
- `Forge.Runtime` (`Domain/WorkflowEvents.cs`): `UsagePayload`; `WorkflowEventPayload.Usage` (required,
  no default — four construction sites, re-counted rather than assumed); `AttemptUsageRecordedType`
  plus its three argument constants; a non-folding `WorkflowFold.Apply` arm and a fail-closed
  `IsTransitionRecord` arm.
- `Forge.Runtime` (`Application/WorkflowEventCodec.cs`): `PersistedUsage` and its per-family mapping;
  stamped version `1.2.0` -> `1.3.0`. Nullable fields are omitted rather than written as explicit
  nulls, which is why `usage_payload` requires nothing.
- `Forge.Runtime` (`Application/Abstractions.cs`): `ISprintStore.AppendAttemptUsageRecordedAsync`.
- `Forge.Runtime` (`Application/FileSprintEventLog.cs`): that method, mirroring
  `AppendAttemptDiffRecordedAsync`'s dedup and derive-the-arguments discipline.
- `Forge.Runtime` (`Application/SprintTimeline.cs`): `SprintTimelinePage.ContractVersion` `1.3.0` ->
  `1.4.0`; `RedactPayload`'s documented no-arm decision.
- `Forge.Runtime` (`Application/StartupContracts.cs`): `DiagnosticCodes.ProviderUsageUnavailable`.
- `Forge.Runtime` (`Localization/`): `workflow.attempt_usage_recorded` in `Messages.resx` and
  `Messages.ru.resx`, and its `TimelineMessageFormatter` arm. Every substituted value is a plain
  number, so no closed-set label needs localizing.
- `Forge.Host.Runtime` (`ImplementationExecutionHostedService.cs`): `RecordAttemptUsageAsync` and its
  own warning log event (2060).
- `docs/contracts/v1/schemas/event.schema.json`: `schema_version` gains `1.3.0`; `payload.usage` and
  `$defs.usage_payload`.
- `docs/contracts/v1/capabilities.json`: `1.15.0` -> `1.16.0`; `sprint.timeline` documents the third
  payload family.
- No CLI code change and no Desktop code change: both render the new item through the same generic
  timeline paths ADR 0059/0060 already proved out. The CLI acceptance test keeps that claim honest.
- `VERSION` moves from `0.83.0` to `0.84.0` (MINOR: additive, no breaking change).

## References

- ADR 0059 (the `payload` envelope this fills; schema-version-as-enum; one event per attempt; the
  fail-open audit-write rule)
- ADR 0060 (the immediately preceding slice: map only what a real capture verified, never a vendor
  document; nothing recorded for a run that observed nothing; the exact exception filter this write
  copies)
- ADR 0006 (the terminal-result uniqueness rule this reads usage from; bounded stream consumption)
- ADR 0008 (the adapter/core split: vendor shapes stay in the adapter, the flat contract is the core's)
- ADR 0052 (why provider quota is reported as `unknown`, and why finding B7 is not this slice)
- `docs/plans/desktop-design-parity-review.md` finding E3 (the finding, and why its stated premise is
  not what got built)
