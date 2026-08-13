# Forge MVP implementation plan

**Updated:** 2026-08-12
**Status:** active

## Rules

- Complete a stage only with reproducible evidence and a green repository gate.
- Update normative ADRs and versioned contracts before implementation.
- Keep one command model, one project snapshot, one sprint journal, and one review engine.
- Keep every project cross-platform unless it is a marked minimal leaf OS adapter under ADR 0007.
- Defer work that has no MVP acceptance case or measured need.

## Progress

- [x] Stages 0–7 — Foundation, solution, updater, startup, providers, durable workflow, Git isolation, and routing.
- [ ] Stage 8 — Cross-platform Host and local control plane.
- [ ] Stage 9 — `.forge/` compiler and agent integration.
- [ ] Stage 10 — Reproducible context assembly.
- [ ] Stage 11 — `implementation-critical` workflow and UI parity.
- [ ] Stage 12 — Diagnostics, evaluations, and hardening.
- [ ] Stage 13 — MVP release and acceptance.

## Completed foundation

### Stage 0 — Contracts and boundaries

- [x] Freeze the Windows MVP distribution, cross-platform core, `.forge/` source of truth, trust boundaries, stable-release policy, state machines, capabilities, localization, configuration scopes, and recovery rules.

### Stage 1 — Solution skeleton

- [x] Create the .NET solution, Runtime, CLI, Desktop, neutral updater, Windows updater adapter, dependency pinning, formatting, analyzers, architecture tests, and CI gate.

### Stage 2 — Neutral self-updater

- [x] Implement release lookup, SemVer selection, target detection, checksum verification, staging, activation contract, restart handshake, and rollback orchestration without an OS dependency.

### Stage 3 — Windows update adapter

- [x] Implement per-user Windows installation, immutable versions, PATH/shortcut integration, restart coordination, x64/Arm64 publishing, rollback, and release tests.

### Stage 4 — Startup and project initialization

- [x] Implement fail-closed startup, scoped configuration, localization, explicit project-root initialization, atomic `.forge/` publication, status snapshot, suggestions, and both initial surfaces.

### Stage 5 — Provider toolchain

- [x] Implement read-only discovery, explicit vendor-native install/update, bounded version probes, absolute process invocation, Codex/Claude JSON normalization, redaction, and stable failures.

### Stage 6 — Durable workflow

- [x] Implement immutable sprint definitions, append-only workflow journal, optimistic concurrency, idempotency, crash recovery, DAG scheduling, attempts, human gates, findings, handoffs, results, retries, and cross-sprint isolation.

### Stage 7 — Git isolation and fallback

- [x] Implement integration/attempt worktrees, fast-forward integration, gated rebase, clean replay, cleanup recovery, fixed sprint retry budget, and provider/model/surface circuit breakers.
- [x] Simplify persistence: discover sprints from journal creation markers, omit runtime sprint ids from the manifest, record routing in the sprint journal, fold budget/breakers from it, and migrate legacy routing sidecars idempotently.

## Stage 8 — Cross-platform Host and local control plane

**Depends on:** Stages 4–7.

- [x] P8.1–P8.8 — Enforce ADR 0007: mark OS adapters; make CLI/shared tests portable; move Windows composition and directory-flush interop to minimal adapters; split reusable Desktop state from WinUI; audit the Windows updater adapter.
- [x] P8.9–P8.17 — Add portable `Forge.Host` and client SDK as the only workflow writer; implement discovery, start, reconnect, message limits, deadlines, correlation, version/capability handshake, and stable diagnostics.
- [x] P8.18–P8.24 — Use one asynchronous `System.IO.Pipes` transport and one named `Mutex` project lease through cross-platform BCL APIs; add no OS branch, TCP fallback, or transport package.
- [ ] P8.25–P8.33 — Implement `GetProjectSnapshot(detail, sprint_id?)` and `ReadControlEvents`. Project summary, next action, tree, sprint inspection, provider/integration status, CLI output, and Desktop views must be local projections of the snapshot.
- [ ] P8.34–P8.41 — Isolate release, Debug, and tests by instance id; test same-user enforcement, hostile clients, crashes, abandoned leases, stale clients, recovery, and three-OS behavior.
- [ ] P8.42–P8.47 — Add Host-owned `resume_not_before` scheduling, safe activity updates, notification projection, and idempotent timer recovery without sleeping executor slots.

**Gate:** neutral projects pass on Windows, Linux, and macOS; adapter tests pass on their OS; one Host owns mutations; active work survives client loss; both surfaces read the same snapshot/events; architecture checks reject platform leakage and alternate read models.

## Stage 9 — `.forge/` compiler and agent integration

- [ ] P9.1–P9.8 — Parse manifest/YAML/Markdown/frontmatter into validated semantic input with safe paths, references, scopes, and context limits.
- [ ] P9.9–P9.16 — Generate reproducible Claude/Codex-native outputs with source hashes, generator versions, drift detection, validation, and artifact-language metadata.
- [ ] P9.17–P9.24 — Generate, inspect, install, and remove one canonical Forge integration for both providers; document snapshot/events/commands and recovery; exclude human authority; never overwrite unknown files.

**Gate:** `.forge/` remains canonical, generated files are reproducible and owned, and agents cannot invoke human-only commands.

## Stage 10 — Reproducible context assembly

- [ ] P10.1–P10.8 — Build a versioned context manifest from rules, sprint specifications/decisions, accepted ADRs, structured handoffs, exact Git/file/`rg` reads, and a token budget.
- [ ] P10.9–P10.12 — Record source commit, selected paths, digests, truncation, and retrieval rationale; rebuild the same context without a transcript.
- [ ] P10.13–P10.20 — Add a versioned declarative context-query plan for bounded read-only Git/file/`rg` operations. Validate paths, operation and result limits, source commit, and profile capabilities; return only a selected structured result bundle, never execute model-authored scripts or shell pipelines as a context engine.

**Gate:** context is bounded, sprint-scoped, reproducible, and based on exact source evidence; query plans cannot mutate the project or widen capabilities, and their result bundles rebuild byte-for-byte from recorded inputs. The MVP owns no full-text, Tree-sitter, LSP, graph, SCIP, or semantic index.

## Stage 11 — `implementation-critical` workflow and parity

- [ ] P11.1–P11.12 — Implement intake, planning, threat/rule rubrics, task DAG, isolated implementation, deterministic tests, review, human approval, and finalization. Use behavior nodes and rubric data, not a seven-role catalog.
- [ ] P11.13–P11.20 — Freeze only planning, implementation, and review execution profiles, including capability allowlists. Every model node starts without a parent transcript and receives only its frozen context manifest and profile capabilities; a node cannot widen them or invoke human-only commands. Internal/external review share one engine and profile with distinct lineage/input; finalization is deterministic.
- [ ] P11.21–P11.31 — Implement the ASD review engine: separate design/implementation counters, fresh contexts, full-first/incremental-later scope, file/rubric coverage, same-iteration approval, severity floors, repeated normalized-finding detection, and human convergence gates.
- [ ] P11.32–P11.40 — Replace prompt arguments/buffered output with stdin, minimal child environments, bounded concurrent JSON/JSONL streams, safe tails, and typed activity. Require exactly one schema-valid terminal result for the owned attempt; zero exit without it, duplicates, and contradictions fail closed.
- [ ] P11.41–P11.47 — Add absolute/idle deadlines, distinct outcomes, and whole-process-tree cleanup verified on Windows, Linux, and macOS before any native containment adapter.
- [ ] P11.48–P11.55 — Implement durable rate-limit deferral and human-only attempt supersession with confirmation, version, idempotency, bounded instruction, cancellation, worktree discard, linkage, and clean replacement.
- [ ] P11.56–P11.66 — Complete CLI/TUI and Desktop projections, commands, attention navigation, human gates, recovery, English/Russian localization, configuration editors, accessibility, and parity tests.
- [ ] P11.67–P11.72 — Add best-effort local notifications for `awaiting_human`, `blocked`, `failed`, and `completed`, deduplicated from journal event ids and redacted.

**Gate:** both surfaces expose equivalent commands and snapshot projections; prompts/environment/output/processes are bounded; nodes inherit neither ambient context nor authority; process exit cannot self-certify completion; review follows ADR 0006; rate-limit, supersession, notification, and crash recovery are durable.

## Stage 12 — Diagnostics, evaluations, and hardening

- [ ] P12.1–P12.8 — Add safe OpenTelemetry traces/metrics, structured logs, and allowlisted `forge doctor --bundle`; omit source/diffs, prompts, provider output, raw commands, credentials, full environments, and unredacted personal paths.
- [ ] P12.9–P12.15 — Add updater/provider/bootstrap/workflow evaluations and model-policy gates that run through existing commands; keep evaluation orchestration out of presentation code.
- [ ] P12.16–P12.28 — Test release trust, injection, IPC identity, protocol confusion, environment leakage, stdin/output denial of service, orphan processes, notification disclosure, permissions, dependencies, licenses, accessibility, localization, parity, migration, routing fold, review convergence, and supersession.

**Gate:** no critical finding remains; diagnostic negative tests pass; safety, parity, accessibility, localization, compatibility, observability, and release-trust thresholds pass.

## Stage 13 — MVP release and acceptance

- [ ] P13.1–P13.10 — Produce reproducible Windows x64/Arm64 bundles containing neutral Host/CLI/presentation code and thin Windows adapters; publish matching version, checksums, SBOM, annotated tag, and release notes.
- [ ] P13.11–P13.24 — Run clean-profile install/update/rollback and end-to-end workflow acceptance: providers, project init, concurrent sprints, fallback, client restart/update, single writer, snapshot/events, human gates, supervised execution, deferral, supersession, review convergence, notifications, diagnostics, localization, and release/development isolation.

**Final gate:** every stage is complete, the signed Windows MVP is reproducible, architecture matches implementation, CI is green, and no blocking/high finding remains.

## Post-MVP learning backlog

- [ ] Derive an evidence-backed `KnowledgeProposal` only from a successful sprint, a corrected failure, or explicit operator instruction. Record applicability, canonical `.forge/` target, source event/artifact references, and a reviewable diff.
- [ ] Reuse the existing human-gate contract to inspect, approve, or reject each proposal. Approval updates canonical `.forge/` content and then regenerates owned provider views; agents cannot approve, directly edit generated skills, or learn from raw transcripts/provider prose.
- [ ] Evaluate an optional per-user hot-memory snapshot for personal preferences and verified environment facts. Keep it separate from project scope, hard-capped, frozen at attempt start, injection-scanned, credential-free, and explicit on overflow; never store project truth in it.
- [ ] Retrieve project knowledge from canonical files and content-addressed sprint artifacts. Add full-text or semantic recall only after an evaluation proves exact lookup insufficient and freshness against the source commit is enforceable.
- [ ] Ship learning only after evaluations demonstrate better task quality without regressions in reproducibility, prompt-injection resistance, approval integrity, privacy, or context cost; include proposal provenance, rollback, migration, and expiry tests.

## Intentionally not planned

- Model-authored executable tool pipelines; declarative context-query plans cover the useful case.
- Shadow Git checkpoints; isolated attempt worktrees and explicit supersession already provide rollback and replay.
- LLM goal judges or provider exit codes as completion authorities; deterministic workflow gates own completion.
- Conversation-history databases as project memory; transcripts are neither state nor canonical knowledge.
- Unrestricted lifecycle hooks or silent agent-managed skill writes; extensions and learned changes require validation, bounded authority, and explicit approval.

## Deferred

- Linux/macOS distribution and native Desktop hosts.
- `standard` and `fast` workflows.
- Full-text, Tree-sitter, LSP, graph, SCIP, or semantic indexes until measured need.
- Network notification channels and custom scripts.
- Distributed workers, SaaS, enterprise policy, package-manager, and machine-wide distribution.

## Evidence

| Stage | Canonical evidence |
|---:|---|
| 0 | ADR 0001; `docs/contracts/v1/`; Stage 0 contract gate |
| 1 | `Forge.slnx`; `src/`; `tests/Forge.Tests/`; `.github/scripts/test-stage1.ps1` |
| 2 | `src/Forge.Updater/`; updater tests |
| 3 | `src/Forge.Updater.Windows/`; Windows publish/installer tests |
| 4 | startup/configuration/initialization implementation and acceptance tests |
| 5 | ADR 0002; `src/Forge.Runtime/Providers/`; provider tests; PR #18 |
| 6 | ADR 0003; sprint journal/scheduler tests; PRs #19–20 |
| 7 | ADR 0004; Git/routing tests; PR #21 |
| 8 architecture | ADRs 0005 and 0007; project snapshot capability; Stage 8 gate above |
| 11 architecture | ADR 0006; supervised execution/review gate above |

## Resolved decisions

| ID | Decision | Resolution |
|---|---|---|
| D-005 | Provider installation/update strategy | ADR 0002 |
| D-006 | Host, local protocol, read-back, and process ownership | ADR 0005 |
| D-007 | Provider supervision and review convergence | ADR 0006 |
| D-008 | Cross-platform core and minimal OS adapters | ADR 0007 |
