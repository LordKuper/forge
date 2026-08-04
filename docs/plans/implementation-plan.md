# Forge MVP implementation plan

**Updated:** 2026-08-04
**Status:** active

## Plan rules

- Mark an item complete only after producing verifiable evidence.
- Record the commit, PR, test run, or artifact in the evidence table.
- Complete every task and check before closing a stage gate.
- Update the normative ADR and contracts before changing architecture.
- Put non-gating work in the deferred backlog.

## Progress

- [x] Stage 0 — Freeze MVP contracts, threats, and boundaries.
- [x] Stage 1 — Create the .NET solution and host skeletons.
- [x] Stage 2 — Implement the platform-neutral self-updater.
- [x] Stage 3 — Implement the Windows installer and update strategy.
- [x] Stage 4 — Implement startup and `.forge/` initialization.
- [x] Stage 5 — Implement provider toolchain management and adapters.
- [ ] Stage 6 — Implement independent sprints and the durable workflow engine.
- [ ] Stage 7 — Implement Git isolation, safe fallback, and circuit breakers.
- [ ] Stage 8 — Implement the `.forge/` source-of-truth compiler.
- [ ] Stage 9 — Implement memory, context building, and code intelligence.
- [ ] Stage 10 — Implement the `implementation-critical` workflow and UI parity.
- [ ] Stage 11 — Add observability, evaluations, and security hardening.
- [ ] Stage 12 — Build, install, and accept the MVP release.

## Stage 0 — Contracts, threats, and MVP boundaries

**Goal:** remove architectural ambiguity before runtime code.

### Completed decisions

- [x] P0.1–P0.8 — Freeze the Windows-only MVP, platform-neutral updater
  boundary, `.forge/` source of truth, fail-closed startup, independent sprint
  model, trust boundaries, stable-release policy, and verified rollback-capable
  updates.
- [x] P0.9–P0.12 — Define self-update, provider, sprint, node, and attempt
  lifecycles plus `IPlatformUpdateStrategy` and `UpdateTarget`.
- [x] P0.13–P0.15 — Define versioned external schemas, diagnostic/exit codes,
  credential restrictions, and redaction.
- [x] P0.16–P0.21 — Define presentation parity, shared commands/queries/events,
  status snapshots, deterministic recommendations, stale-state rejection,
  permissions, confirmations, and idempotency.
- [x] P0.22–P0.30 — Define localization, language packs, culture-invariant
  machine contracts, scoped configuration, provenance, atomic migrations, and
  artifact audiences.

### Gate

- [x] Required decisions are accepted in ADR 0001.
- [x] State machines contain only declared transitions.
- [x] External boundaries use versioned Draft 2020-12 schemas.
- [x] Every public capability maps to both surfaces, one permission policy, and
  an acceptance test.
- [x] Every recommendation defines rationale, preconditions, safety, target, and
  stale behavior.
- [x] Configuration keys have one owner, schema, default or inheritance, and
  provenance.
- [x] Every mutating action defines rollback or recovery.
- [x] MVP and deferred scope are separate.

## Stage 1 — .NET solution skeleton

**Depends on:** Stage 0.

- [x] P1.1–P1.10 — Create `Forge.slnx`, consolidated `Forge.Runtime`,
  platform-neutral and Windows updater projects, CLI, and categorized tests.
- [x] P1.19–P1.20 — Create the .NET MAUI Windows `Forge.Desktop` host and
  retain presentation contracts inside `Forge.Runtime`.
- [x] P1.11–P1.18 — Configure dependency injection, clock/files/process/network
  abstractions, structured redacted logging, analyzers, formatting,
  warnings-as-errors, test categories, CI, architecture tests, and CLI skeleton.
- [x] P1.21–P1.23 — Add Desktop startup skeleton, Windows publish targets, and
  architecture rules forbidding presentation-to-infrastructure dependencies.
- [x] P1.24–P1.27 — Add the shared localization catalog, complete English and
  Russian resources, catalog linting, and hard-coded-string checks.
- [x] P1.28–P1.31 — Add scoped configuration registries, stores, migrations,
  atomic writes, provenance, and anti-bypass architecture tests.

**Gate:** clean restore/build/test; both hosts build and share presentation,
localization, and configuration contracts; architecture and redaction tests pass.

## Stage 2 — Platform-neutral self-updater

**Depends on:** Stages 0–1.

- [x] P2.1–P2.5 — Detect OS/process architecture, normalize `UpdateTarget`,
  resolve exactly one strategy, and forbid mutation before resolution.
- [x] P2.6–P2.12 — Query the latest published stable GitHub release, enforce
  SemVer/no-downgrade, use ETags without TTL bypass, select assets, and verify
  name, size, and SHA-256.
- [x] P2.13–P2.18 — Implement the update lifecycle, restart token, argument/cwd
  preservation, startup handshake, and platform-neutral rollback orchestration.
- [x] P2.19–P2.24 — Cover platform detection, fake strategies, unsupported
  platforms, release selection, verification failures, restart, and rollback.

**Gate:** the core has no Windows dependency and cannot mutate unsupported
platforms; all update/restart/rollback contract tests pass.

## Stage 3 — Windows installer and update strategy

**Depends on:** Stage 2.

- [x] P3.1–P3.8 — Implement in-app installation, RID detection, verified download,
  per-user version layout, host self-tests, idempotent PATH update, and rollback.
- [x] P3.9–P3.16 — Implement mutex/timeout, staging, helper activation, atomic
  current switch, handshake rollback, and concurrent-launch handling.
- [x] P3.17–P3.23 — Test clean-profile installation, reinstall, N→N+1,
  argument/cwd preservation, corrupt assets, handshake failure, and concurrency.
- [x] P3.24–P3.30 — Bundle CLI/Desktop/updater, manage Start Menu shortcuts,
  preserve surface and language across install/update/rollback, and ship `en`/`ru`.

**Gate:** clean Windows installation and verified atomic update/rollback work for
`win-x64` and `win-arm64`.

## Stage 4 — Startup and `.forge/` initialization

**Depends on:** Stages 2–3.

- [x] P4.1–P4.4 — Implement ordered startup, global commands, internal self-test
  bypass, and recovery-only failure mode.
- [x] P4.5–P4.16 — Verify explicit roots, require confirmation, initialize through
  staging/atomic publish, create the minimal project tree, and never overwrite
  unknown configuration.
- [x] P4.17–P4.24 — Share bootstrap between CLI/Desktop, restore navigation,
  render project/sprint status, and expose safe recovery recommendations.
- [x] P4.25–P4.34 — Load/migrate language and scoped configuration, implement
  CLI/Desktop editors and provenance, reject cross-scope keys, and initialize
  project artifact languages independently from user language.

Scope notes: sprint rendering covers the empty snapshot contract, and sprint
records arrive with the workflow engine in Stage 6. Desktop navigation is
restored by querying durable state on activation; persisting the selected project
across launches needs a new user-scope key and moves with recent-project
preferences. Replaying a completed mutation is rejected as stale until Stage 6
adds the durable idempotency store.

**Gate:** startup remains fail-closed, root confirmation is safe and idempotent,
and both surfaces show equivalent status/configuration.

## Stage 5 — Provider toolchain and adapters

**Depends on:** Stage 4.

- [x] P5.1–P5.9 — Define provider strategies; discover, version-check,
  install/update, refresh, and recheck official Codex/Claude CLIs; forbid project
  overrides; block sprint work until both are ready.
- [x] P5.10–P5.17 — Execute providers without shell concatenation, parse versioned
  JSON/JSONL, validate constrained output, check compatibility, diagnose auth
  safely, normalize failures, and maintain provider fixtures.

**Gate:** clean Windows provider preflight reaches `ready` or returns stable,
redacted, fail-closed diagnostics.

## Stage 6 — Independent sprints and durable workflow

**Depends on:** Stages 4–5.

- [ ] P6.1–P6.8 — Persist sprint/node/attempt state and append-only events with
  optimistic concurrency, idempotency, crash recovery, cancellation, and resume.
- [ ] P6.9–P6.18 — Freeze base commit, inputs, workflow/configuration/model
  policy, dependencies, and state namespaces; reject mutable cross-sprint input.
- [ ] P6.19–P6.27 — Implement deterministic DAG scheduling, retries, human gates,
  findings, handoffs, node results, and completion gates.
- [ ] P6.28–P6.34 — Store localization keys rather than rendered text, preserve
  invariant state, snapshot artifact policy, and pass separate conversation and
  artifact languages to providers.

**Gate:** concurrent sprints remain isolated and resume deterministically after a
crash without transcript state.

## Stage 7 — Git isolation, fallback, and circuit breakers

- [ ] P7.1–P7.7 — Create sprint integration worktrees, per-write-attempt
  worktrees, base checks, dirty recovery, ownership maps, integration barriers,
  and gated rebase.
- [ ] P7.8–P7.14 — Implement health keys, circuit breakers, cooldowns, shared
  retry budgets, clean replay, auth/policy exclusions, and route-decision events.

**Gate:** no write fallback continues over an unknown diff; replay starts from the
frozen base and all routing decisions are reproducible.

## Stage 8 — `.forge/` source-of-truth compiler

- [ ] P8.1–P8.7 — Parse manifest/YAML/Markdown/frontmatter into validated semantic
  IR with safe relative paths, references, inheritance, scopes, and context limits.
- [ ] P8.8–P8.14 — Generate Claude/Codex-native outputs with source hashes,
  generator versions, build manifest, drift detection, sync, and validation.
- [ ] P8.15–P8.20 — Enforce artifact language policy, declared audiences,
  language-pack capability, metadata provenance, and separate representations for
  separate audiences.

**Gate:** generated provider files are reproducible derived outputs and drift is
detected without becoming canonical state.

## Stage 9 — Memory, context, and code intelligence

- [ ] P9.1–P9.9 — Implement layered sprint/project memory, structured handoffs,
  reproducible context manifests, token budgets, full-text retrieval, retention,
  and content-addressed cleanup without transcript dependency.
- [ ] P9.10–P9.16 — Implement Git/file/ripgrep, Tree-sitter, LSP/Serena, optional
  graph/SCIP, freshness checks, graceful fallback, and evidence requirements.

**Gate:** context is reproducible, bounded, sprint-scoped, and never trusts a
stale derived index.

## Stage 10 — `implementation-critical` workflow and parity

- [ ] P10.1–P10.14 — Implement intake, threat analysis, spec, ADR, traceability,
  task DAG, isolated implementation, test matrix, independent correctness/security
  review, convergence, adversarial verification, human approval, and finalization.
- [ ] P10.15–P10.19 — Implement the seven MVP roles and deterministic
  format/build/test/secrets gates; reject `fast` and `standard`.
- [ ] P10.20–P10.30 — Complete CLI/TUI and Desktop lifecycle, DAG, events,
  findings, artifacts, approvals, recovery, diagnostics, parity matrix, concurrent
  surfaces, and durable Desktop restoration.
- [ ] P10.31–P10.42 — Complete equivalent status guidance, recommendation order,
  English/Russian localization, language switching, scoped config editors, and
  artifact audience/language previews.

**Gate:** every public capability has equivalent CLI/TUI and Desktop behavior,
permission semantics, durable results, and acceptance coverage.

## Stage 11 — Observability, evaluations, and hardening

- [ ] P11.1–P11.5 — Add safe OpenTelemetry spans, metrics, versions/strategy
  diagnostics, bundles, and secret-leak tests.
- [ ] P11.6–P11.12 — Add updater/provider/bootstrap/sprint/implementation evals,
  thresholds, and model-policy update gates.
- [ ] P11.13–P11.18 — Review threats, release trust, prompt injection,
  permissions, supply-chain pins, dependencies, licenses, and vulnerabilities.
- [ ] P11.19–P11.35 — Add parity, advisor, concurrency, localization,
  pseudo-localization, third-language, scoped-config, migration, cross-user, and
  artifact-language suites plus safe metrics.

**Gate:** no critical security finding remains; evaluation thresholds, parity,
localization, scoped configuration, and release trust all pass.

## Stage 12 — MVP release and acceptance

- [ ] P12.1–P12.8 — Produce reproducible Windows bundles, release assets,
  checksums, SBOM, published-asset installer tests, update/rollback
  tests, and operational documentation.
- [ ] P12.9–P12.28 — Execute end-to-end installation, self-update, provider
  preflight, project init, isolated concurrent sprints, clean fallback,
  implementation workflow, dual-surface parity, stale recommendations, and
  machine-readable status acceptance.
- [ ] P12.29–P12.42 — Accept English default, Russian switching, persistence
  through update/rollback, invariant machine contracts, third-language packs,
  cross-user project policy, separate conversation/artifact languages,
  wrong-scope rejection, provenance parity, and explicit regeneration behavior.

**Final gate:** every stage is complete, no blocker/high finding remains, clean
environment acceptance is reproducible, architecture matches implementation, and
the signed MVP release installs globally for the Windows user.

## Deferred backlog

- Linux and macOS installers/update strategies.
- macOS Desktop surface.
- Package-manager and machine-wide distribution.
- `standard` and `fast` workflows.
- Distributed workers and centralized scheduling.
- Multi-tenant/SaaS runtime.
- Enterprise policy integration.
- Extended transparency and key rotation.

## Evidence

| Scope | Evidence | Date |
|---|---|---|
| Stage 0 decisions and contracts | `docs/architecture/decisions/0001-stage-0-foundation.md`, `docs/contracts/v1/` | 2026-07-27 |
| Stage 0 gate | `pwsh ./tests/Forge.Tests/Contracts/Stage0.Contracts.Tests.ps1` | 2026-08-03 |
| Stage 1 solution and hosts | `Forge.slnx`, `src/`, `tests/Forge.Tests/` | 2026-08-03 |
| Stage 1 gate | `pwsh ./.github/scripts/test-stage1.ps1` | 2026-08-03 |
| Stage 2 updater core and gate | `dotnet build Forge.slnx --no-restore --configuration Release`; `dotnet test Forge.slnx --no-build --configuration Release` (61 tests) | 2026-07-29 |
| Stage 3 Windows installer and gate | `dotnet restore Forge.slnx --locked-mode`; `dotnet format Forge.slnx --no-restore --verify-no-changes`; `dotnet build Forge.slnx --no-restore --configuration Release`; `dotnet test Forge.slnx --no-build --configuration Release` (83 tests); `build/Publish-WindowsBundle.ps1` for `win-x64` and `win-arm64` | 2026-08-03 |
| Stage 4 startup and initialization gate | `dotnet restore Forge.slnx`; `dotnet format Forge.slnx --no-restore --verify-no-changes`; `dotnet build Forge.slnx --no-restore --configuration Release`; `dotnet test Forge.slnx --no-build --configuration Release` (161 tests) | 2026-08-04 |
| Stage 5 provider toolchain and adapters, and gate | `docs/architecture/decisions/0002-provider-toolchain.md`; `src/Forge.Runtime/Providers/`; `dotnet restore Forge.slnx --locked-mode`; `dotnet format Forge.slnx --no-restore --verify-no-changes`; `dotnet build Forge.slnx --no-restore --configuration Release`; `dotnet test Forge.slnx --no-build --configuration Release` (189 tests); live run against `github.com/openai/codex` and `github.com/anthropics/claude-code`: `forge models --json` reaches `"ready": true` for both providers with genuine verified downloads (`codex 0.146.0`, `claude_code 2.1.221`), and `forge doctor --startup` reports `providers passed none` | 2026-08-04 |

## Open decisions

| ID | Decision | Target stage | Status |
|---|---|---:|---|
| D-005 | Revalidate official Codex and Claude installation/update strategies on Windows immediately before implementation. | 5 | Resolved — `docs/architecture/decisions/0002-provider-toolchain.md` |
