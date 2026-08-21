# ADR 0041: Structured logging, first slice

- Status: Accepted
- Date: 2026-08-21

## Context

Stage 12's P12.1–P12.8 names "safe OpenTelemetry traces/metrics" and "structured logs" as still
open after `forge doctor --bundle` shipped (ADR 0038). `ISafeLogger`/`SafeLogger` already existed
(`src/Forge.Runtime/Infrastructure/SafeLogger.cs`) — a redacted `Information(eventName,
properties)` method, registered in DI — but had zero production callers anywhere, and its
implementation logged through `Microsoft.Extensions.Logging.ILogger<SafeLogger>`, which the Host
only ever routes to JSON console output (`ForgeHost.cs`: `logging.ClearProviders();
logging.AddJsonConsole(...)`). Console output nobody watches for a background daemon is not a
useful "structured log" in the sense this item means: an operator or a future `forge doctor
--bundle` needs something that survives the terminal that started the Host closing.

Investigation found the destination question already has an answer elsewhere in this codebase,
not a new decision: ADR 0005 states instance identity "namespaces IPC endpoints, user
configuration, logs, caches, and worktrees under per-user paths," and
`ForgeApplication.CollectWritableProbesAsync` already probes
`Path.Combine(paths.LocalApplicationData, "Forge", paths.InstanceId)` annotated "user
configuration, worktrees, and — once they exist — logs/caches, per ADR 0005." OpenTelemetry has no
comparable small first slice: every standard exporter is either OTLP-over-network (this project's
whole design never phones home) or needs a local collector process with no existing
infrastructure — a real decision only a maintainer should make, not attempted here.

## Decisions

### `SafeLogger` writes redacted JSONL directly to a file, not through `ILogger`

Rewrote `SafeLogger` to append one compact JSON object per line to
`LocalApplicationData/Forge/<InstanceId>/logs/forge.jsonl`, bypassing the general `ILogger`
pipeline entirely — matching `FileSprintEventLog`'s own append-only, single-writer JSONL
convention rather than inventing a new one. This is a **separate channel from `ILogger`
on purpose**: a blanket file provider capturing every logging category would silently persist
exception messages and other `Log*` calls elsewhere in the codebase that were never audited for
redaction-safety (AGENTS.md: "Never expose secrets or sensitive data in logs or errors"). Keeping
persistence opt-in per call site — only what a caller explicitly builds a property bag for
`ISafeLogger` — keeps that guarantee local and auditable. `ISafeLogger.Information` became
`InformationAsync` (async file I/O; the interface had zero callers, so this is not a breaking
change to anything real). Every property still passes through the existing
`SecretRedactor.RedactProperties`, unchanged.

### First real caller: the Host's own start/stop, not a per-node event

`ControlPlaneHostedService` gets the first two calls: `host_started` right after it starts
listening (project id, instance id, whether the lease was recovered from an abandoned one), and
`host_stopped` in `StopAsync`, guarded on `lease is not null` so a Host that lost the lease race
and never actually started does not log a stop it never logged a start for. This was chosen over
per-node/per-attempt events specifically because `ControlPlaneHostedService` already writes
`LogListening` via `ILogger` for the identical moment — the new call is not filling a
"nothing is logged here" gap, it is giving that one moment a **persisted** record alongside the
existing ephemeral one, the smallest, least ambiguous slice to prove the new sink end to end
before deciding which of many possible per-node events (attempt start, provider failure, routing
deferral, ...) deserves persistence next.

## Consequences

- `SafeLogger` is `IDisposable` (owns a `SemaphoreSlim` serializing writes); DI disposes it with
  the container, matching every other owned-resource singleton in this codebase.
- Two new tests: `SafeLoggerTests` (unit-level, an isolated `TestEnvironment` — proves one JSON
  line per call, and that a planted secret-shaped property name is redacted, not written raw) and
  `ControlPlaneTests.StartingAndStoppingTheHostRecordsRedactedLifecycleEvents` (a real Host
  round-trip — proves the wiring, not just the primitive). Both confirmed via a live mutation
  check: removing `SecretRedactor.RedactProperties` and removing the `host_started` call each
  reproduce the exact regression the corresponding test now catches.
- Explicitly deferred, named rather than silently dropped: OpenTelemetry traces/metrics (needs its
  own exporter/dependency decision, out of scope for this slice); every other candidate structured
  event (per-node attempt lifecycle, provider failures, routing deferrals, notification delivery);
  log rotation/retention (a single ever-growing `forge.jsonl` for now, matching
  `FileSprintEventLog`'s own no-rotation precedent for per-sprint files); and surfacing this log's
  contents through `forge doctor --bundle` (that bundle's schema is deliberately closed and was
  never meant to hold raw log lines — a future slice's own scoping question, not this one's).

## References

- ADR 0005 (instance-namespaced `LocalApplicationData` paths — the destination decision this slice
  reuses rather than re-litigates)
- ADR 0038 (`forge doctor --bundle`; explicitly named both OpenTelemetry and structured logging as
  excluded from its own scope)
