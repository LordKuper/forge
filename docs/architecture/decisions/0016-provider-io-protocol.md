# ADR 0016: Provider stdin, minimal environment, and bounded streaming

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.32-P11.40) must replace the
Stage 5 proof's prompt-as-CLI-argument, host-inherited-environment, and
buffered-after-exit execution path with the bounded contract ADR 0006 already
specifies for provider input, environment, and output.

ADR 0006, quoted for the parts this item makes concrete:

> "Forge sends prompts through redirected standard input, never a
> command-line argument... Provider children receive a minimal environment
> assembled by Forge. A frozen provider environment contract allowlists
> required platform, home/temp, locale, proxy, toolchain, and
> provider-authentication variables... Known nested-session markers and
> credentials for other providers are removed... Stdout and stderr are
> consumed concurrently as bounded streams. The adapter limits a frame,
> line, aggregate output, and retained safe tail... Oversized or malformed
> frames fail closed... A zero process exit code proves only that the
> provider transport ended normally. An attempt succeeds only after Forge
> receives a schema-valid terminal result for the owned attempt... Exit
> without an explicit terminal result is a provider failure; duplicate or
> contradictory terminal results fail closed... Safe, throttled activity
> events may update the attempt's last-activity time without persisting
> provider content."

As with every prior Stage 11 item, no node executor exists anywhere in the
repo yet (`SprintScheduler.StartAttemptAsync` and `ILlmProvider.RunAsync`
still have zero production callers) — this item is pure infrastructure
rework, not a new caller.

## Decisions

### The prompt travels on stdin, never a CLI argument

`ProviderExecution.RunAsync` takes the prompt as its own parameter and hands
it to `IProcessRunner` through `ProcessRequest.StandardInput`. Both adapters
drop the trailing `["--", prompt]` argument entirely. Claude Code's `claude
-p` reads the prompt from stdin when no positional prompt is given; Codex's
`codex exec --json` never accepted a positional prompt in the first place
(ADR 0002) — the old code's `["--", prompt]` for Codex was a latent
discrepancy this item removes, not a behavior this item changes.

### Every provider child gets a Forge-assembled minimal environment

`ProviderEnvironmentPolicy.BuildMinimalEnvironment` is the one place the
allowlist lives: fixed platform/home-temp, locale, proxy, and
Node-toolchain variable names (both vendor CLIs are Node-based), plus
whichever provider-authentication variable names the calling adapter names.
Exclusion is by omission — a nested-session marker like `CLAUDECODE` or `CI`,
or another provider's credential, is simply never in the allowlist, so it
can never reach a child regardless of what the current host process has set.
`ProviderExecution.RunAsync` passes the built environment with
`ProcessRequest.ReplaceEnvironment: true`, so the child's environment is
exactly this frozen set, never the host process's own plus overrides.

Neither adapter names a concrete authentication variable today: Claude Code
and Codex both authenticate through their own local credential stores
(`claude auth status`, `codex login status`), not an environment variable
Forge reads or writes. Each adapter's `AuthenticationVariableNames` is
therefore an empty list with a comment recording why, rather than a guessed
vendor variable name — the same caution ADR 0014 applied to `DefaultModel`.

This item deliberately does not touch the separate, pre-existing
`ProviderInstallation` probe path (`--version`, `auth status` /
`login status`, `update`) — that additive-environment behavior already has
its own adapter-level tests and is out of this item's scope, which ADR 0006
frames specifically as "long-running provider work" (prompt execution), not
install/update probing.

### Output is consumed as a bounded, concurrent stream

`IProcessRunner`/`ProcessRunner` (already reworked earlier in this branch)
call an `IProcessOutputSink` once per stdout/stderr line as it arrives,
concurrently with the process still running. `ProviderExecution.RunAsync`
supplies a private `BoundedOutputSink` that:

- Parses each stdout line as one JSON object, exactly as the old buffered
  code did, but incrementally instead of after exit.
- Enforces three bounds: a single line's byte length (`MaxLineLengthBytes`,
  1 MiB — ADR 0006's "frame"), the number of parsed events retained
  (`MaxEventCount`, 20,000), and the combined stdout+stderr byte total
  (`MaxAggregateBytes`, 64 MiB — ADR 0006's "aggregate output"). A violation
  of any of these fails the run closed with `ProviderFailureKind
  .MalformedOutput`, cancels the run so the child is actually terminated
  rather than left running to natural exit, and carries a redacted
  `SafeTailCharacters` (8,192-character) tail of raw output as diagnostic
  detail (ADR 0006's "retained safe tail").
- Redacts every event's extracted `Text` through `SecretRedactor.Redact`
  before it is added to the returned `ProviderEvent` — "applies redaction
  before any durable or presentation boundary" — since `ProviderEvent`
  crosses out of this shared helper into caller/presentation code.
- Counts events the adapter's `classify` function marks `Result`. Exactly
  one such event is required for success: zero produces
  `ProviderFailureKind.MissingTerminalResult`, more than one produces
  `ProviderFailureKind.DuplicateTerminalResult` regardless of whether the
  events agree — "duplicate or contradictory terminal results fail closed."
  The first `Result` event's extracted (redacted) text becomes the new
  `ProviderTerminalResult.Summary`.
- Invokes an optional `onActivity` callback once per parsed stdout event,
  mapping a `ToolUse`-classified event to `AttemptActivityKind.ToolUse` and
  everything else to `AttemptActivityKind.Heartbeat`. This layer calls it
  unthrottled on every event; throttling the resulting attempt-activity
  write is left to whichever future caller persists it (`SprintScheduler
  .RecordAttemptActivityAsync`), since only that caller knows its own
  durability/throttle policy — this shared execution helper only signals
  that activity happened, and of which kind.

`ProviderRunResult` gains a `TerminalResult` field (`null` on any failure);
`ProviderTerminalResult` is a deliberately minimal `(string? Summary)` —
neither vendor publishes a stable success/failure field name for its
terminal event, so this does not invent one. Overall `Succeeded` still comes
from process exit code, stderr keyword classification, and terminal-result
uniqueness together, never from a field guessed inside the terminal event
itself.

`ILlmProvider.RunAsync` gains the same optional `onActivity` parameter,
threaded straight through by both adapters.

### The sink is genuinely concurrent, and a bound violation cancels the run

`ProcessRunner` runs its stdout and stderr read loops side by side, both
calling into the same `BoundedOutputSink` instance — "concurrently," not
merely "interleaved by `await`." Every mutation (`events`, the safe tail,
`aggregateBytes`, `Failure`) runs under one lock; only the resulting
`onActivity` callback (itself async, and the caller's to await) runs outside
it, so the lock is never held across an `await`. A bound violation both
stops further parsing and cancels a `ProviderExecution`-owned linked
`CancellationTokenSource` — distinct from the caller's own token — so
`ProcessRunner` kills the child promptly instead of continuing to consume
its output until natural exit; `ProviderExecution.RunAsync` recognizes this
self-inflicted cancellation (checking `sink.Failure` alongside the caller's
own token) and returns the already-classified failure rather than
propagating an `OperationCanceledException`. This bounds retained memory by
kill latency, not by a hard byte cap — a true hard cap belongs in the
generic, provider-agnostic `ProcessRunner` only if a future non-provider
caller also needs one; today every other caller (git, installers) already
relies on receiving its complete output. Kill latency itself stays bounded
even against a child that never emits a newline at all: `ReadStreamAsync`'s
own `MaxBufferedLineChars` (4 MiB, generic — not a provider-specific
constant) forcibly hands off a line mid-stream once it grows past that
ceiling, so the sink's much smaller `MaxLineLengthBytes` always gets a
chance to see and reject an oversized single line within a bounded amount
of buffering, not only when a real newline eventually arrives.

### Stream reads preserve exact bytes; stdin writes are cancellable and pipe-safe

`ProcessRunner.ReadStreamAsync` reads in fixed chunks rather than
line-by-line, appending every decoded character verbatim to the string it
returns — including original CRLF/bare-CR line endings and a missing or
present trailing newline — while separately stripping `\r` only from the
line buffer used for per-line sink notification. This matters beyond
providers: `GitContextReader` (ADR 0012) hashes this exact returned text
into a content digest, so a line-splitting read that normalized line
endings or dropped a trailing newline (as `ReadLineAsync`-based reading
would) would silently change that digest for any CRLF or no-final-newline
file content — a compatibility break this item's own read-loop rewrite
would otherwise have introduced. Separately, the stdin write now uses the
caller's cancellation token (previously unbreakable once the prompt
exceeded the OS pipe buffer and the child stopped reading), tolerates the
child closing or never opening its stdin pipe (`IOException`/
`ObjectDisposedException` around the write, not surfaced as an unhandled
exception out of `RunAsync`), and closes the stdin handle in a `finally` so
a write failure can never leak it open.

### A misbehaving sink can never break the read loop itself

`ReadStreamAsync`'s per-line sink notification is wrapped with a 30-second
bounded wait and a broad catch: a sink call (in practice, a caller-supplied
`onActivity` callback with no production implementation yet) that throws or
simply hangs is treated as "nothing more to notify" rather than faulting
the read loop or leaving it stuck. This matters specifically because the
same read loop is what a cancellation's kill-then-drain sequence awaits to
finish — an unbounded sink call would have made that supposedly-bounded
cleanup path itself unbounded. The losing side of that race is cleaned up
rather than left dangling: the timer is cancelled immediately once the
sink call wins, and if the sink call times out instead, its eventual fault
(if any) is observed through a continuation so it can never surface as an
unhandled `UnobservedTaskException` later.

Separately, the stdin write's own `Close()` (the graceful end-of-input
signal a normal run needs) now only runs after a write that actually
completed. `Close()` performs a blocking, uncancellable flush; running it
unconditionally in a `finally` — including when the write itself had just
been cancelled because the pipe was already backed up — could hang before
the outer `catch`'s `process.Kill(true)` (the actual fix for that backed-up
pipe) ever got a chance to run. A cancelled write now falls straight
through to that outer catch instead, which tears the whole child down
without needing a graceful stdin close at all.

### Diagnostic detail stays within the safe-tail bound

The malformed-JSON failure message no longer interpolates the offending
line itself (previously up to `MaxLineLengthBytes`, 1 MiB — far past what
`SafeTailCharacters` bounds); it states the fact only, since the
already-appended, already-redacted, `SafeTailCharacters`-bounded safe tail
carries the same content for diagnostics without defeating that bound. The
non-zero-exit failure path (pre-existing, not new to this item, but
rewritten here) is trimmed the same way: classification still scans the
complete stderr text, but only the trimmed tail is stored as `Detail`.

### The fold now carries `LastActivityKind` through every subsequent transition

The attempt-transition branch of `WorkflowFold.Apply` rebuilds
`AttemptSnapshot` on every transition; it already carried `NodeId`,
`TargetOutcome`, and `LastActivityAt` forward from the previous snapshot but
omitted `LastActivityKind`, silently resetting a `ToolUse`/`Heartbeat`
classification to `null` (a wrong classification, not merely a missing one)
on the very next transition after any activity event. Now carried forward
identically to the other fields.

### New durable activity-kind fold behavior is directly tested

The `activity_kind` event argument, its fold into `AttemptSnapshot
.LastActivityKind`, backward compatibility with a pre-v0.37 event that
never carried the argument (folds to `null`, not an error), and an
unrecognized value on replay (fails loudly as `InvalidDataException`,
wrapping the same `FormatException` every other snake_case-encoded enum
argument in the fold already throws for corruption) are now each pinned by
a dedicated test in `SprintEventStoreTests`, rather than only exercised
incidentally by the rest of the suite continuing to pass.

### Fixed the Codex terminal-event misclassification this item's own check surfaced

The prior `Classify` matched any `type` starting with `"turn."`, so
`turn.started` (a lifecycle marker mid-run) was misclassified as `Result`
alongside the genuinely terminal `turn.completed`/`turn.failed`. Under the
old code this was silent — nothing checked terminal-result count. Under this
item's uniqueness check it would have failed every normal Codex run closed
as a duplicate. Fixed to match only `"turn.completed"` and `"turn.failed"`.

### No new durable schema

`ProviderTerminalResult` and the bounded-stream types above are in-memory
only — nothing in the repository persists a provider run result yet (same
"no node executor exists" gap every prior Stage 11 item has noted). Adding a
`docs/contracts/v1/schemas/*.schema.json` file and a validating codec for a
type nothing writes to disk would be speculative scaffolding with no real
consumer to validate against; ADR 0006's "schema-valid terminal result" is
satisfied at this stage by the sink's own JSON-object parsing and the
adapter's documented event-shape classification (ADR 0002). A durable schema
belongs with whichever future item actually persists a terminal result.

## Consequences

- The Stage 5 proof's command-line prompt, host-inherited environment, and
  buffered-after-exit output path is fully replaced before any node executor
  exists to depend on it.
- A provider process can no longer see Forge's own session markers, another
  provider's credentials, or any variable outside the frozen allowlist.
- A hung, runaway, or malformed provider process fails closed and is
  actually terminated (not merely stopped-from-being-retained), bounding
  its resource use to kill latency instead of the process's full lifetime.
- `GitContextReader`'s content digests (ADR 0012) remain byte-exact even
  though `ProcessRunner`'s read path changed from whole-buffer to
  chunked/streaming.
- Every successful run now carries exactly one terminal result; a provider
  that exits zero without one, or emits more than one, is a recorded
  failure instead of a silently-accepted success.
- `ILlmProvider.RunAsync` and `ProviderExecution.RunAsync` still have zero
  production callers — this item is infrastructure only, consistent with
  every prior Stage 11 item building toward, not yet reaching, the node
  executor.

## References

- ADR 0006 (supervised execution and bounded review convergence)
- ADR 0002 (provider toolchain — Codex's documented "no positional prompt")
- ADR 0014 (frozen execution profiles — the `DefaultModel` no-invented-vendor-fact precedent)
