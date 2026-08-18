# ADR 0017: Attempt deadlines and process-tree teardown

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.41-P11.47) must add the two
frozen per-attempt deadlines and the distinct-outcome vocabulary ADR 0006
already specifies, and prove `Process.Kill(entireProcessTree: true)` actually
tears down a multi-generation process tree on Windows, Linux, and macOS
before considering a native OS-adapter containment call.

ADR 0006, quoted for the parts this item makes concrete:

> "Every attempt has two frozen deadlines: an absolute session deadline and
> an idle deadline. Any bounded stream activity resets the idle deadline;
> model wording does not... The durable outcome distinguishes
> `provider_idle_timeout`, `provider_session_timeout`, user cancellation,
> and ordinary provider failure. Cancellation or either deadline terminates
> the entire owned process tree, waits for exit, drains bounded pipes, and
> records cleanup outcome. The first implementation uses .NET
> `Process.Kill(entireProcessTree: true)` on all platforms. Windows, Linux,
> and macOS tests launch a child and grandchild and prove none survives
> cancellation, timeout, or normal parent exit. If those tests prove the
> BCL guarantee insufficient, only the missing native containment call
> moves to a minimal OS adapter under ADR 0007; supervision policy remains
> cross-platform."

As with every prior Stage 11 item, no node executor exists anywhere in the
repo yet (confirmed again by repo-wide search: `ILlmProvider.RunAsync` and
`SprintScheduler.StartAttemptAsync` still have zero production callers).
This item is pure infrastructure, built for a future executor to use, not a
new caller. `ExecutionProfile.SessionDeadlineSeconds`/`IdleDeadlineSeconds`
(ADR 0014, P11.13-P11.20) already exist and are frozen onto every sprint,
but nothing reads them yet — this item is what would read them.

## Decisions

### `AttemptSupervisor` owns the two deadlines and the outcome distinction

New `Forge.Application.AttemptSupervisor` (`AttemptSupervision.cs`): given
an absolute session `TimeSpan`, a sliding idle `TimeSpan`, and the caller's
own `CancellationToken` (both deadlines validated as strictly positive
before any disposable resource is created, so a rejected constructor call
leaks nothing), it exposes a linked `Token` to hand to supervised work in
the caller's place, and an `OnActivityAsync` callback to pass as that
work's own activity callback (e.g. `ILlmProvider.RunAsync`'s `onActivity`,
added by P11.32-P11.40). Two `System.Threading.Timer`s back the two
deadlines: the session timer is armed once and never reset; the idle timer
re-derives the true remaining time from an activity timestamp on every
tick (see below) — "any bounded stream activity resets the idle deadline;
model wording does not" holds because `OnActivityAsync` never inspects the
activity's kind or any event text, so it cannot distinguish (and does not
try to distinguish) "real" activity from prose — every parsed event
counts, exactly matching what
`ProviderExecution`'s own `onActivity` already invokes on.

Whichever fires first — session timeout, idle timeout, or the caller's own
token — is latched as the attempt's `AttemptTerminationReason` (`None`
until then) and cancels `Token`. `SuperviseAsync<T>` is the ergonomic entry
point: it runs the supplied work with `Token`/`OnActivityAsync`, and
translates a resulting `OperationCanceledException` into a classified
`AttemptSupervisionResult<T>` only when both this supervisor's `Reason` is
latched *and* the exception's own `CancellationToken` equals `Token` — not
merely `Reason != None`, since work that independently throws for an
unrelated token could otherwise be misattributed purely because of a
timing coincidence with an already-latched reason. Any exception that
fails that check (an ordinary thrown error, or a cancellation for an
unrelated token) propagates unchanged.

The idle timer is self-rescheduling rather than reset-via-`Change` from
`OnActivityAsync`: a `Timer.Change` call can never recall a callback the
runtime has already dispatched, so resetting the due time on every
activity would still leave a window where activity arriving right as the
timer fires produces a spurious idle timeout. `OnActivityAsync` instead
only records an atomic last-activity timestamp
(`Stopwatch.GetTimestamp()`); the idle timer's own callback re-derives the
actual remaining time from that timestamp on every tick and either fires
or reschedules for the true remaining span — closing the race rather than
merely narrowing it. Every mutation of the latched reason and every
`CancellationTokenSource.Cancel()`/`Dispose()` call runs under one lock
held for the whole critical section (not released and re-acquired), so a
timer callback can never run concurrently with `Dispose()` tearing down
the same `CancellationTokenSource` — which that type documents as
unsupported. `Cancel()` is wrapped in a broad catch, not just
`ObjectDisposedException`: `Token` is public, so an arbitrary third-party
registration on it (or on a further-linked token) throwing must never
crash a timer callback thread.

### Two new diagnostic codes, no new persistence plumbing

`ProviderDiagnosticCodes.IdleTimeout` (`provider_idle_timeout`) and
`.SessionTimeout` (`provider_session_timeout`) are added — both already
reserved in `docs/contracts/v1/README.md` since Stage 8, unimplemented
until now. No change to `SprintScheduler.CompleteAttemptAsync`, `WorkflowEvent`,
or any schema was needed: `CompleteAttemptAsync` already accepts
`IReadOnlyList<NodeDiagnostic>? diagnostics`, and `NodeDiagnostic.Code` is
exactly where a future node executor would record
`AttemptSupervisionResult.Reason` as one of these two codes (or leave
diagnostics as-is for `Reason == None`, i.e. an ordinary outcome). "User
cancellation" and "ordinary provider failure" need no new code at all:
`AttemptTerminationReason.Cancelled` and `.None` already distinguish them
in-memory, and neither needs a durable diagnostic code beyond what already
exists (a cancelled attempt is recorded through the existing attempt
lifecycle, not as a node diagnostic).

### Process-tree teardown is verified with a genuine grandchild, cross-platform

New `ProcessRunnerTests` cases spawn a child that itself spawns a
grandchild — a nested `Start-Process` (its own `.ps1` file) on Windows, a
freshly `sh`-invoked script (its own `.sh` file) on POSIX — each writing
its own process id to a marker file so the test can positively confirm
liveness and death via `Process.GetProcessById` rather than inferring it
from a file lock (whose semantics differ enough across platforms to be a
weaker proof). The POSIX grandchild deliberately runs as a separate `sh`
invocation, not a `(...)` subshell: `$$` inside a subshell still reports
the *invoking* shell's own pid in POSIX sh/dash/bash (fixed at shell
startup, not re-evaluated per fork), so a `(...)`-based grandchild would
have written the same pid as the child — silently testing the same process
twice rather than a real second generation. Where a process was previously
confirmed alive, its start time is captured then and death is checked
against that exact instance (`Process.StartTime`), not the pid alone,
since an OS can reassign a just-freed pid to an unrelated process before a
liveness check runs.

`CancellationTerminatesTheEntireProcessTreeIncludingAGrandchild` proves
cancellation kills both generations; `NormalParentExitLeavesNoOrphanedGrandchildRunning`
is the companion baseline — a well-behaved parent that waits for its own
grandchild before exiting leaves nothing running, no kill involved. Both
run on Windows via `powershell.exe` and on Linux/macOS via `/bin/sh` (ADR
0007), joining the cross-platform integration tests P11.32-P11.40 already
added. The existing `Process.Kill(entireProcessTree: true)` call in
`ProcessRunner` (already present before this item) needed no change: these
tests confirm the BCL guarantee holds on all three platforms this repo
targets, so no native OS-adapter containment call is added under ADR 0007
— exactly the outcome ADR 0006 anticipated as the likely one ("if those
tests prove the BCL guarantee insufficient...").

### Deliberately deferred

`AttemptSupervisor` has no production caller: wiring it into an actual
attempt-execution loop (arming it from a sprint's frozen
`ExecutionProfile.SessionDeadlineSeconds`/`IdleDeadlineSeconds`, calling
`SuperviseAsync` around `ILlmProvider.RunAsync`, and feeding
`AttemptSupervisionResult.Reason` into `CompleteAttemptAsync`'s
`diagnostics`) needs the same node executor every prior Stage 11 item has
deferred — still nothing in the repo drives an attempt from `Created`
through a real provider call. Cleanup-outcome *recording* beyond the
diagnostic code itself (ADR 0006: "...and records cleanup outcome") is
this same executor's job once it exists; there is no cleanup event to
record when nothing yet drives a real cleanup.

## Consequences

- A future node executor has a ready-made, already-tested primitive for
  both frozen deadlines and outcome distinction — no ad hoc timer/token
  logic to invent when that executor is finally built.
- `ExecutionProfile.SessionDeadlineSeconds`/`IdleDeadlineSeconds` (frozen
  since ADR 0014) remain unread by production code, same as before — this
  item makes them *readable by* a future caller, not read by one yet.
- Process-tree teardown is now proven, not assumed, on Windows, Linux, and
  macOS, closing out the "if the BCL guarantee proves insufficient" branch
  ADR 0006 left open — no ADR 0007 OS adapter was needed.
- `AttemptSupervisor`, `ProviderDiagnosticCodes.IdleTimeout`, and
  `.SessionTimeout` have zero production callers — infrastructure only,
  consistent with every prior Stage 11 item building toward, not yet
  reaching, the node executor.

## References

- ADR 0006 (supervised execution and bounded review convergence)
- ADR 0007 (cross-platform core and minimal OS adapters)
- ADR 0014 (frozen execution profiles — the deadline fields this item reads)
- ADR 0016 (provider stdin/environment/streaming — the self-cancellation
  pattern and `onActivity` callback this item reuses)
