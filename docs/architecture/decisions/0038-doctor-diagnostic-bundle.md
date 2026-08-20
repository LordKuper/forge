# ADR 0038: `forge doctor --bundle`

- Status: Accepted
- Date: 2026-08-21

## Context

Stage 12's P12.1–P12.8 asks for "safe OpenTelemetry traces/metrics, structured
logs, and allowlisted `forge doctor --bundle`." ADR 0005 already named this
command's exact scope — "Forge/protocol/provider versions, startup checks,
project state summaries, event log integrity, worktree registrations,
circuit-breaker/retry state, writable probes, and safe error metadata,"
excluding "prompts, provider output, diffs, source contents, raw command
lines, credentials, full environment dumps, and unredacted personal paths" —
and P8.48–P8.54 already versioned the wire contract
(`docs/contracts/v1/schemas/diagnostic-bundle.schema.json`) and its matching
C# DTOs (`DiagnosticContracts.cs`), explicitly deferring "collection,
redaction proof, and the CLI command that produces one" to this item. This
item closes that half: the collector and the `--bundle` flag. OpenTelemetry
traces/metrics and structured logging are deliberately **not** in this
slice — see Consequences.

## Decisions

### The allowlist itself is the redaction; no free-text field exists to redact

Every `DiagnosticBundle` field is already a machine code, a version string,
a count, or a boolean. Unlike `SafeLogger`/`SecretRedactor` (which redact
free-text property bags that could contain anything), nothing collected here
is ever a prompt, provider output, diff, source content, raw command line,
credential, environment value, or unredacted path — the schema's own
`additionalProperties: false` and typed fields make an unsafe value
structurally impossible to include, not merely filtered after the fact.

### A collection failure omits that section; it never fails the whole bundle

ADR 0005: "If safe parsing or redaction cannot be proven, the payload is
omitted and the bundle records that omission." `CollectDiagnosticBundleAsync`
applies this uniformly, not only to redaction specifically: each section
(startup/providers/project, event log integrity, worktree registrations,
circuit breakers/retry budget, writable probes) is collected inside its own
try/catch, adding a name to `Omissions` on any exception rather than letting
one broken section (e.g. a corrupt sprint file) hide every other section's
healthy evidence — the exact scenario `forge doctor --bundle` exists to
diagnose. A bundle is always produced; it may simply say less.

### Event log integrity: a proactive walk, since no such check existed anywhere

Every persisted sprint's definition and folded state must load without
throwing `InvalidDataException` — the same corruption `FileSprintEventLog`'s
own read methods already detect reactively, per record, the first time
something happens to touch an affected sprint. This walks every sprint once
so corruption is reported before that happens, not only discovered
incidentally later.

### `IWorktreeManager.ListAsync`: git's own registration list, unfiltered

New primitive, mirroring `ExistsAsync`'s own `git worktree list --porcelain`
parsing but returning every entry instead of checking one path.
Deliberately **includes** the primary worktree (the project root itself) —
returning exactly what `git` reports rather than guessing which entries a
caller considers "Forge's own." `WorktreeRegistration.Exists` applies
`ExistsAsync`'s own registered-vs-directory-present distinction, so orphaned
worktrees (deleted externally, not through `RemoveAsync`) are visible in the
bundle.

### Circuit breakers and retry budget: project-wide views over per-sprint state

`RoutingLedger`'s breaker and retry-budget state are sprint-scoped by design
(its own remarks: one shared retry budget per sprint, breakers keyed by
`HealthKey` within a sprint). A project-wide bundle needs project-wide
figures with no precedent to follow:

- **Circuit breakers**: every distinct `HealthKey` any sprint's route
  decisions ever named, each resolved through the same
  `GetCircuitBreakerAsync` derivation already used elsewhere, keyed as
  `{sprint_id}/{health_key.canonical}` so two sprints' independent state for
  the same provider/model/surface never collides.
- **Retry budget**: `Total` is `RoutingLedger.DefaultRetryBudget` itself —
  every sprint is given the identical fixed total, so there is no
  meaningful sum or average to report instead. `Remaining` is the *minimum*
  remaining across every sprint that has consumed any of it (full budget
  when none has) — the sprint closest to exhausting its budget is what a
  diagnostic snapshot should surface, not a total that would obscure it.

### Writable probes: the two directories Forge itself ever writes durable state to

The project's own `.forge/` directory and this instance's namespaced share
of `IEnvironmentPaths.LocalApplicationData` (user configuration, worktrees,
and — once they exist — logs/caches, per ADR 0005's own instance-identity
namespacing). Each probe writes and immediately overwrites one fixed,
clearly diagnostic-named marker file rather than a randomly named one, so
repeated runs leave at most one stray file per directory instead of
accumulating one per run.

### `ForgeApplication` gains the collector directly, not a new class

`CollectDiagnosticBundleAsync` joins `ForgeApplication` itself — "the single
entry point both surfaces use" — rather than a standalone collector class,
matching how every other read (`GetStartupStatusAsync`,
`GetProjectSnapshotAsync`) is already shaped. It needed four new constructor
dependencies (`RoutingLedger`, `IWorktreeManager`, `IEnvironmentPaths`,
`IFileSystem`) plus `IClock`, all already registered in the shared
composition root (`AddForgeCore`), so every existing caller — production and
`TestEnvironment`'s own DI container alike — resolves it with no other
change needed.

### Not routed through the Host; not a `capabilities.json` entry

Like every other read `forge doctor`/`forge status`/`forge tree` already
performs, this runs directly against the local `ForgeApplication` — reads
are not subject to ADR 0005's "one Host writer" rule, which governs
mutations. It is also not added to `CapabilityIds`/`capabilities.json`: that
registry governs capabilities requiring parity across both CLI and Desktop
surfaces (`public_requires_both_surfaces: true`), and `forge doctor --bundle`
is a CLI-only support/diagnostic utility in the same category as `forge
tree`/`forge sprint inspect` — a local projection with no Desktop
counterpart planned, not a governed capability.

## Consequences

- New `IWorktreeManager.ListAsync`/`WorktreeRegistration`, implemented by
  `GitWorktreeManager` (real `git worktree list --porcelain`) and
  `FakeWorktreeManager` (reports every currently-tracked path as existing —
  it has no separate "registered but deleted" concept, since its own
  `RemoveAsync` unregisters and forgets a path in the same step; the
  orphan-detection half is proven only against real `git.exe`, in a new
  `GitWorktreeManagerListTests.cs`).
- `ForgeApplication`'s constructor gains `RoutingLedger`, `IWorktreeManager`,
  `IEnvironmentPaths`, `IFileSystem`, `IClock`.
- New `ForgeApplication.CollectDiagnosticBundleAsync` and its private
  section collectors (event log integrity, routing state, writable probes).
- New `forge doctor --bundle` CLI flag, printing `StatusJson.Serialize` of
  the collected bundle.
- New test coverage: `DiagnosticBundleCollectorTests.cs` (the real collector
  against a `TestEnvironment` project — uninitialized, initialized with
  accurate counts/versions, corrupt-sprint detection — each asserting
  schema conformance, not just field values), `GitWorktreeManagerListTests.cs`
  (real git), `DoctorBundleCliTests.cs` (CLI wiring, schema conformance,
  before/after project initialization).
- Explicitly **not** in this slice, named rather than silently absorbed:
  OpenTelemetry traces/metrics (P12.1–P12.8's other named deliverable — a
  genuinely separate, cross-cutting instrumentation effort touching startup,
  updates, providers, routing, attempts, deadlines, deferrals, workflow
  transitions, notifications, and reviews per `docs/architecture/overview.md`,
  with its own dependency decision this codebase has never had to make
  before); structured logging beyond the existing, still-zero-call-site
  `SafeLogger`/`ISafeLogger` (wired in a prior stage, never yet invoked);
  Desktop parity (deliberately out of scope — see the capability-registry
  decision above).

## References

- ADR 0005 (local Host and control plane — "Diagnostics are allowlisted and
  development is isolated," this item's own source text)
- The `diagnostic-bundle.schema.json`/`DiagnosticContracts.cs` contract
  (P8.48–P8.54), whose own doc comment named this item as the slice that
  would implement collection
