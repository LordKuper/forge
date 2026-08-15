# Forge architecture overview

**Status:** target MVP architecture
**Updated:** 2026-08-13

This concise English overview is the canonical architecture summary. The
[research summary](ai-agentic-software-development-workflow.md) captures the
non-normative reasoning behind it; the [implementation plan](../plans/implementation-plan.md)
tracks delivery work.

Forge is a local control plane that coordinates Claude Code CLI and Codex CLI
through durable, isolated, policy-controlled software-delivery workflows. Models
provide judgment; deterministic code owns state, routing, retries, Git,
permissions, validation, and release gates.

Normative architecture decisions and machine contracts live in:

- [`decisions/0001-stage-0-foundation.md`](decisions/0001-stage-0-foundation.md)
- [`decisions/0002-provider-toolchain.md`](decisions/0002-provider-toolchain.md)
- [`decisions/0005-local-host-and-control-plane.md`](decisions/0005-local-host-and-control-plane.md)
- [`decisions/0006-supervised-execution-and-review-convergence.md`](decisions/0006-supervised-execution-and-review-convergence.md)
- [`decisions/0007-cross-platform-core-and-minimal-os-adapters.md`](decisions/0007-cross-platform-core-and-minimal-os-adapters.md)
- [`decisions/0008-modular-provider-runtime.md`](decisions/0008-modular-provider-runtime.md)
- [`../contracts/v1/`](../contracts/v1/)
- [`../plans/implementation-plan.md`](../plans/implementation-plan.md)

## MVP boundary

The MVP distribution is per-user Windows with two equal interaction surfaces:

- `forge` CLI/TUI;
- .NET MAUI Desktop on WinUI 3.

Both surfaces are clients of a per-user, cross-platform headless Forge Host and
call the same versioned commands, project snapshot, typed
events, permission policies, and status advisor over current-user local IPC.
Neither surface contains business orchestration or directly calls provider CLIs,
Git, event stores, or updater implementations. Active work survives either
client's exit, restart, crash, or compatible update.

The only MVP workflow is `implementation-critical`. All reusable Forge code is
cross-platform; only explicit, minimal OS adapters may use platform APIs. The
Host, protocol, client, CLI/TUI, workflow, contracts, and reusable Desktop state
are tested on Windows, Linux, and macOS. This does not enable a non-Windows MVP
surface or distribution. Linux and macOS installers/Desktop hosts,
package-manager distribution, machine-wide installation, lightweight workflows,
distributed workers, SaaS, and enterprise policy are deferred.

## System boundary

```text
OS bootstrap ─ Cross-platform CLI/TUI ─┐
                                      ├─ Versioned local protocol ─ Forge Host ─ Application ─ Domain
OS UI host ─ Cross-platform UI model ──┘                                  │
                                           ├─ Workflow engine
                                           ├─ Status advisor/projections
                                           ├─ Configuration/localization
                                           └─ Interfaces
                                               ├─ Registered provider adapters
                                               ├─ Git/worktrees
                                               ├─ Sprint journal/artifacts
                                               ├─ Files/process/network
                                               └─ Platform update strategy
```

The dependency direction points inward. Infrastructure implements application
interfaces. Neutral code never references an OS adapter. A thin OS bootstrap or
native UI host selects adapters and delegates to neutral code; it contains no
workflow or presentation state. The host is the only workflow writer. A
cross-platform, current-user named mutex prevents release, development, test,
CLI, or Desktop instances from concurrently mutating one `.forge/` tree.

## Platform boundary

Cross-platform is the default for every Forge project, including Host, CLI/TUI,
application, domain, contracts, protocols, persistence, provider contracts and
lifecycle policy, Git adapters, update policy, and reusable Desktop presentation.
These projects use portable .NET TFMs and the same behavior on Windows, Linux,
and macOS. Portable runtime detection may report capabilities or select an
adapter; it may not perform the native operation inline.

Code that must call an OS API lives in a dedicated, marked leaf adapter. An
adapter translates a neutral port to one native operation and normalizes the
result. Provider-owned paths, scripts, commands and environment behavior,
installer activation, PATH/shortcut integration, native Desktop hosting, OS
notifications, secret storage, and a proven BCL process/file-durability gap are
valid adapter boundaries. Workflow, policy, retries, persistence, protocols,
and presentation state are not. See
[`decisions/0007-cross-platform-core-and-minimal-os-adapters.md`](decisions/0007-cross-platform-core-and-minimal-os-adapters.md).

## Local control plane

The local protocol uses length-prefixed UTF-8 JSON over one cross-platform
`System.IO.Pipes` transport and begins with a version and capability handshake.
Forge uses `NamedPipeServerStream`/`NamedPipeClientStream` with
`PipeOptions.CurrentUserOnly`; .NET maps them to Windows named pipes or Unix-domain
sockets and enforces the same-user boundary. A platform-neutral transport
interface owns framing and connection lifecycle, with no Forge OS adapters.
Request-size limits, deadlines, correlation ids, schema validation, authorization,
expected state versions, confirmations, and idempotency apply before dispatch. It
introduces no alternate command model, TCP fallback, or permission bypass.

`GetProjectSnapshot(detail, sprint_id?)` is the authoritative read model for
startup, status, suggested actions, tree, sprint detail, provider/integration
state, findings, gates, artifacts, and routing. `forge status`, `next`, `tree`,
`sprint inspect`, and Desktop screens project this DTO locally. `ReadControlEvents`
merges unseen records from durable per-sprint journals through an opaque cursor;
follow mode uses bounded short polling. The snapshot is authoritative after reconnect or cursor failure, and
prompts, transcripts, raw commands, and terminal output never enter either
contract. See
[`decisions/0005-local-host-and-control-plane.md`](decisions/0005-local-host-and-control-plane.md).

## Startup pipeline

Every public surface uses the same ordered bootstrap:

1. load and migrate user configuration;
2. resolve UI, interaction, and LLM languages;
3. detect OS and architecture;
4. select a platform update strategy;
5. verify the latest stable Forge release;
6. stage, verify, activate, restart, and roll back when required;
7. connect to or start a compatible Forge Host and complete its handshake;
8. resolve enabled providers, probe local versions, conditionally update under
   the release cache, recheck, and verify authentication readiness;
9. verify an explicit project root;
10. load or explicitly initialize `.forge/`;
11. validate and synchronize project configuration;
12. build the versioned project snapshot;
13. rank safe next actions and open the requested surface.

An unusable or unauthenticated enabled provider is fail-closed for project and
sprint work. Release-check failure does not block an otherwise usable installed
provider. Recovery diagnostics remain available.

## Self-update

The updater core uses the immutable
`UpdateTarget(OsFamily, Architecture, Packaging)` tuple and resolves exactly one
`IPlatformUpdateStrategy` before network or filesystem mutation. The MVP
registers only `WindowsUpdateStrategy`.

Forge accepts only a newer published stable SemVer release from
`github.com/LordKuper/forge`. Assets require name, size, SHA-256, and
checksum verification. Activation uses
an immutable version directory, atomic current pointer, self-test, one-use
restart token, startup handshake, and rollback.

A compatible running Host may finish active attempts while clients move to a new
version, then drain and restart when idle. An incompatible Host replacement waits
for idle or requires explicit cancellation; updates never kill active work
silently.

## Project source of truth

`<project-root>/.forge/` is the sole canonical project configuration tree.
Generated provider-native files are derived outputs with source hashes and
generator versions.

Initialization:

- checks only the current or explicitly supplied absolute directory;
- displays the absolute path and requires confirmation;
- never searches upward when creating configuration;
- stages a complete tree and publishes it atomically;
- never overwrites an unknown or partial `.forge/`;
- creates only the `implementation-critical` workflow.

## Scoped configuration

User configuration is personal and stored under `%LOCALAPPDATA%\Forge`.
Project configuration is reproducible and stored only under `.forge/`. Their key
spaces do not overlap.

User scope owns:

- `language.ui`;
- `language.interaction`;
- `language.llm`;
- ordered `providers.enabled` selection and fallback priority;
- interaction and recent-project preferences.

Project scope owns:

- `artifacts.language.user_facing`;
- `artifacts.language.agent_facing`;
- workflows, policies, and generator settings.

Wrong-scope keys return `configuration_scope_violation`. Each store has its own
schema version, migration stream, atomic write, provenance, and recovery.
Credentials belong to provider authentication or OS secret storage, never YAML,
JSON, prompts, artifacts, or logs.

## Localization and artifact audiences

English is the default and ultimate fallback; Russian is built in. One catalog
serves both presentation surfaces. Commands, flags, identifiers, schemas, codes,
telemetry, and durable state remain culture invariant.

Each generator declares exactly one audience:

- `user_facing`: language comes from project user-facing policy;
- `agent_facing`: language comes from project agent-facing policy;
- `machine`: no language is attached or applied.

Artifact metadata records audience, resolved language when applicable, policy
snapshot hash, and generator version. Missing artifact-language capability blocks
generation; it never silently falls back.

## Durable workflow

A sprint is the top-level isolation boundary. It owns:

- immutable inputs and base commit;
- workflow version and configuration snapshot;
- branch and integration worktree;
- namespaced events, nodes, attempts, findings, and artifacts;
- separate worktrees for every write attempt.

Cross-sprint input is allowed only through an explicit immutable commit or
content-addressed published artifact. Mutable state from another non-terminal
sprint is forbidden.

The workflow engine persists transitions before side effects, uses idempotency
keys, resumes after crashes, and distinguishes sprint, node, and attempt
lifecycles. Deterministic gates decide pass/fail; models never self-certify
tests. Sprint/node/attempt transitions, routing decisions, and provider outcomes
share one append-only, localization-safe journal under `.forge/sprints/{id}/`.
Workflow state, retry balance, and circuit breakers are folded on every read (see
[`decisions/0003-durable-sprint-persistence.md`](decisions/0003-durable-sprint-persistence.md)).

The `implementation-critical` workflow implements the accepted scope before it
selects or authors new tests. It first confirms the implementation against the
frozen definition of done or explicit user expectations using inspection,
execution, and relevant existing checks. It then assesses the actual change's
residual risks, adds the smallest useful set of tests, and runs final
deterministic gates. Expected results come from the accepted behavior rather than
the implementation; every fix retains a regression test proven against pre-fix
behavior or an equivalent targeted mutation.

This is one built-in workflow invariant in both delivery scopes. Forge
contributors follow it through `AGENTS.md`; every managed project receives it
through Host-enforced transitions and the generated provider integration.
Project policy may add stricter gates but cannot permit new scope-test selection
or authoring before implementation confirmation. The Host schedules separate
implementation, confirmation, and test-work nodes; test work is not eligible
until a valid confirmation artifact exists.

An explicit operator supersession cancels a non-terminal attempt, discards its
worktree, records a bounded instruction artifact, and starts a fresh attempt from
the same recorded base. It never edits the frozen plan or continues partial edits.

## Provider execution and fallback

The core owns only `ILlmProvider`, provider-neutral lifecycle contracts, and
generic selection, maintenance, and routing policy. Explicit Windows composition
registers `Forge.Providers.Codex.Windows` and
`Forge.Providers.Claude.Windows`; each library exclusively owns its vendor's
paths, scripts, commands, authentication, environment, and output normalization.
The provider projects do not reference each other, and there is no shared
`Forge.Providers.Windows` library. Forge uses no reflection or dynamic provider
plugin loader for the MVP.

User configuration selects an exact ordered provider set. Omission enables all
registered built-ins in composition order; `[]` leaves diagnostics available but
blocks model work. A project profile may narrow and reorder this set but cannot
enable a provider the user disabled. Disabled providers are never probed,
installed, updated, authenticated, or executed — but `forge models`, the project
snapshot, and Desktop still list a registered-but-disabled provider as a
read-only, never-probed row (`enabled: false`, `state: null`,
`diagnostic_code: provider_disabled`), so it stays visible without being
touched. The resolved ordered intersection is frozen into each sprint. See
[`decisions/0008-modular-provider-runtime.md`](decisions/0008-modular-provider-runtime.md).

Enabled adapters install and update official CLIs through each vendor's native
Windows mechanism (see
[`decisions/0002-provider-toolchain.md`](decisions/0002-provider-toolchain.md)).
Startup always performs a bounded local version probe, but checks remote update
availability at most once per 24 hours and invokes the vendor updater only when
a newer version exists. Failed checks and updates retry after one hour;
`forge models --refresh` bypasses the cache but not the availability check.
After maintenance, Forge explicitly checks authentication for every enabled
provider and retains only normalized readiness and stable diagnostics.
`forge models --json` emits the versioned `provider-health.schema.json`
envelope (`schema_version` plus a `providers` array), not a bare array.

Provider adapters execute official CLIs without shell-string concatenation,
consume versioned JSON/JSONL, validate schema-constrained output, and normalize
quota, rate-limit, authentication, policy, transient, and malformed-output
errors.

Prompts travel through redirected standard input, never process arguments or
environment variables. Provider children receive a minimal allowlisted
environment. Forge consumes bounded stdout/stderr streams concurrently and
supervises each process with separate absolute-session and activity-based idle
deadlines. Cancellation or timeout terminates the owned process tree and records
the exact safe outcome. Provider activity may update a throttled heartbeat but
provider prose never drives workflow state.

Fallback uses provider/model/surface circuit breakers and a shared retry budget,
both folded from the sprint journal rather than separate routing files.
A failed write attempt is never continued in place by another model. Fallback
replays from the original base commit in a clean worktree. Authentication and
policy failures are never disguised as transient failures. Sprint integration
and per-attempt worktrees, the fast-forward integration barrier, gated rebase,
and durable routing decisions are defined in
[`decisions/0004-git-isolation-and-circuit-breakers.md`](decisions/0004-git-isolation-and-circuit-breakers.md).

A retryable rate limit records `resume_not_before`, releases the executor slot,
and is re-enqueued durably by Forge Host; no worker sleeps through the delay.
Planning, implementation, and review use three provider/model/effort/sandbox/
deadline profiles frozen into the sprint. Internal and external review share the
review profile with fresh attempts and bounded inputs; finalization is deterministic.
See
[`decisions/0006-supervised-execution-and-review-convergence.md`](decisions/0006-supervised-execution-and-review-convergence.md).

## Review convergence

One review engine accepts a scope and rubric for design or implementation.
Reviewers start with fresh context and attempt identity. Forge first selects an
available reviewer with provider/model lineage different from implementation.
If none exists, it uses the first available reviewer in normal configured
priority, including the same provider or model family. The verdict records the
achieved separation; reduced provider/model independence is diagnostic, not a
human gate. No available review model still blocks review. Every internal verdict
proves scoped file and rubric coverage; incomplete coverage is re-dispatched once
in the same iteration.

Design and implementation review counters are independent. Default consecutive
severity budgets are low `1`, medium `1`, high `2`, and critical `10`: the floor
rises from all findings to medium, high, and finally critical-only. Before the
next iteration exceeds the cumulative budget, a human chooses to continue at the
critical floor, accept/override current findings with rationale, or abort. Two
consecutive identical normalized external finding sets trigger the same gate.
Forge does not infer review stagnation from HEAD or diff changes.

## Context assembly

Durable state does not live in transcripts. MVP context is assembled through
progressive disclosure:

1. always-on rules and workflow contracts;
2. sprint-scoped specifications and decisions;
3. project knowledge, accepted ADRs, and structured handoffs;
4. exact Git, file, and `rg` lookup under a recorded token budget.

Every model node starts without inherited conversational context. Forge supplies
a frozen, content-addressed context manifest and the capability allowlist from
the node's execution profile. A model may propose a versioned declarative
context-query plan containing only bounded, read-only Git, file, and `rg`
operations. Forge validates and executes the plan, then admits only the selected,
budgeted result bundle to model context. The plan, source commit, selections,
digests, truncation, and rationale remain reproducible; model-authored scripts or
shell pipelines are not context engines and cannot widen capabilities.

Forge builds no full-text, Tree-sitter, LSP, graph, SCIP, or semantic index for
the MVP. Add one only after measurements show exact retrieval misses a required
lookup and the index can prove freshness against the source commit.

## Status and next actions

`ProjectSnapshot` is immutable and versioned. The active sprint is an
explicit selection or the only non-terminal sprint; Forge never silently chooses
among multiple candidates.

Recommendations are deterministic and prioritize:

1. startup recovery;
2. human gates;
3. blockers;
4. recoverable failures;
5. finalization;
6. resumable work;
7. first-sprint creation;
8. inspection.

Every recommendation carries rationale, preconditions, safety class, target,
command, idempotency key, and expected state version. Stale recommendations are
rejected without side effects.

The dashboard emphasizes work needing attention: human gates, blockers, failures,
and newly completed work before active or informational work. Status always has a
text and shape representation in addition to color. Keyboard navigation,
screen-reader names and announcements, high contrast, and reduced motion are MVP
acceptance requirements. Attention navigation changes only the selected view; it
never mutates workflow state or infers status from provider prose.

Human gates present rationale, bounded diff/artifact references, findings, and
compatibility/security impact through one shared decision contract. Approval or
rejection is an explicit human-only command with confirmation, expected state
version, and idempotency; an agent integration cannot self-approve.

Desktop/OS notifications mirror durable `awaiting_human`, `blocked`, `failed`,
and `completed` events. They are user-configurable, best-effort, redacted, and
deduplicated; delivery never changes workflow state. Network channels and custom
notification scripts are deferred.

## Agent integration and instance identity

The `.forge/` compiler generates Claude Code and Codex skill/plugin views from one
canonical integration source. Outputs contain source hashes, ownership markers,
minimum Forge/protocol versions, the built-in workflow invariants including the
implementation-first testing order, and no human-only authority. Generated
instructions describe that order, while Forge Host owns its enforcement.
Generation and optional installation are idempotent and never overwrite unknown
user-owned files.

Release, Debug, and tests use `forge`, `forge-dev`, and unique ephemeral instance
ids respectively. They have distinct IPC endpoints, configuration, logs, caches,
and worktrees resolved through cross-platform .NET per-user paths, while the
shared project lease still prevents concurrent writers. Host publishing uses
CoreCLR rather than NativeAOT until named-mutex behavior passes the full OS matrix.

## Post-MVP learning

After the MVP, Forge may derive an evidence-backed `KnowledgeProposal` from a
successful sprint, a corrected failure, or explicit operator instruction. A
proposal identifies its canonical `.forge/` target, applicability, source event
and artifact references, and a reviewable diff. It is inert until a human
approves it through the normal versioned, idempotent gate; approval updates the
canonical source and only then regenerates provider-native views. Provider prose,
transcripts, and generated skills never update project knowledge directly.

An optional post-MVP per-user hot-memory snapshot may retain only a small,
hard-capped set of personal preferences and verified environment facts. It is
frozen at attempt start, never stores project truth or credentials, and rejects
overflow instead of silently dropping entries. Project recall continues to use
canonical project files and durable sprint artifacts, not a conversation-history
database.

## Security and observability

Project content, provider output, generated files, skills, hooks, and prompts are
untrusted data. Policy and permission checks precede execution. Logs and
diagnostic bundles pass through structured redaction before persistence or
display; full environment dumps and raw credentials are forbidden.

`forge doctor --bundle` is allowlist-based. It may include versions, startup and
project summaries, event-log integrity, worktree registrations, routing/retry
state, writable probes, and safe error metadata. It excludes source/diff content,
prompts, provider output, raw commands, secrets, unredacted personal paths, and
any payload whose safe parsing or redaction cannot be proven.

OpenTelemetry traces cover startup, updates, providers, routing, attempts,
deadlines, deferrals, workflow transitions, notifications, and reviews. Metrics
and diagnostics use stable codes and never contain localized user content or secrets.

## Release acceptance

The release gate requires:

- reproducible `win-x64` and `win-arm64` bundles;
- matching source, artifact, tag, and release versions;
- checksums and SBOM;
- clean-profile installer and update tests;
- CLI/TUI and Desktop capability parity;
- host continuity, protocol compatibility, project single-writer, snapshot/event
  read-back, and client reconnect tests;
- cross-platform Host/protocol/lease tests on Windows, Linux, and macOS;
- portable-TFM, dependency-boundary, native-import, and three-OS build/test gates
  for all neutral projects, with adapter tests on their declared OS;
- attention dashboard, human-gate, keyboard, screen-reader, high-contrast, and
  reduced-motion acceptance;
- canonical agent-integration generation and development/release isolation;
- reproducible declarative context-query plans, frozen per-node context and
  capability boundaries, and explicit schema-valid terminal results;
- stdin-only prompts, minimal provider environments, bounded streaming, dual
  watchdogs, process-tree cleanup, durable rate-limit resumption, operator
  supersession, fresh review with best-effort provider/model lineage separation,
  ASD convergence, and notification deduplication;
- English/Russian localization completeness;
- scoped-configuration and artifact-language acceptance;
- updater, provider, workflow, fallback, recovery, and security suites;
- no unresolved blocking review findings.
