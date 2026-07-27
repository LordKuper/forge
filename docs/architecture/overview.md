# Forge architecture overview

**Status:** target MVP architecture
**Updated:** 2026-07-27

Forge is a local control plane that coordinates Claude Code CLI and Codex CLI
through durable, isolated, policy-controlled software-delivery workflows. Models
provide judgment; deterministic code owns state, routing, retries, Git,
permissions, validation, and release gates.

Normative Stage 0 decisions and machine contracts live in:

- [`decisions/0001-stage-0-foundation.md`](decisions/0001-stage-0-foundation.md)
- [`../contracts/v1/`](../contracts/v1/)
- [`../../implementation-plan.md`](../../implementation-plan.md)

## MVP boundary

The MVP is a per-user Windows application with two equal interaction surfaces:

- `forge` CLI/TUI;
- .NET MAUI Desktop on WinUI 3.

Both surfaces call the same application commands, queries, immutable DTOs, typed
events, permission policies, and status advisor. Neither surface contains
business orchestration or directly calls provider CLIs, Git, SQLite, or updater
implementations.

The only MVP workflow is `implementation-critical`. Linux, macOS, package-manager
distribution, machine-wide installation, lightweight workflows, distributed
workers, SaaS, and enterprise policy are deferred.

## System boundary

```text
CLI/TUI ─┐
         ├─ Presentation contracts ─ Application ─ Domain
Desktop ─┘                              │
                                       ├─ Workflow engine
                                       ├─ Status advisor
                                       ├─ Configuration/localization
                                       └─ Interfaces
                                           ├─ Provider adapters
                                           ├─ Git/worktrees
                                           ├─ SQLite/CAS
                                           ├─ Files/process/network
                                           └─ Platform update strategy
```

The dependency direction points inward. Infrastructure implements application
interfaces. Platform-neutral updater code never references the Windows strategy.

## Startup pipeline

Every public surface uses the same ordered bootstrap:

1. load and migrate user configuration;
2. resolve UI, interaction, and LLM languages;
3. detect OS and architecture;
4. select a platform update strategy;
5. verify the latest stable Forge release;
6. stage, verify, activate, restart, and roll back when required;
7. discover, update, and recheck provider CLIs;
8. verify an explicit project root;
9. load or explicitly initialize `.forge/`;
10. validate and synchronize project configuration;
11. build a versioned project status snapshot;
12. rank safe next actions and open the requested surface.

Update or provider uncertainty is fail-closed for project and sprint work.
Recovery diagnostics remain available.

## Self-update

The updater core uses the immutable
`UpdateTarget(OsFamily, Architecture, Packaging)` tuple and resolves exactly one
`IPlatformUpdateStrategy` before network or filesystem mutation. The MVP
registers only `WindowsUpdateStrategy`.

Forge accepts only a newer published stable SemVer release from
`github.com/LordKuper/forge`. Assets require name, size, SHA-256, and
GitHub/Sigstore provenance verification against built-in policy. Activation uses
an immutable version directory, atomic current pointer, self-test, one-use
restart token, startup handshake, and rollback.

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
lifecycles. Deterministic gates decide pass/fail; models never self-certify tests.

## Provider execution and fallback

Provider adapters execute official CLIs without shell-string concatenation,
consume versioned JSON/JSONL, validate schema-constrained output, and normalize
quota, rate-limit, authentication, policy, transient, and malformed-output
errors.

Fallback uses provider/model/surface circuit breakers and a shared retry budget.
A failed write attempt is never continued in place by another model. Fallback
replays from the original base commit in a clean worktree. Authentication and
policy failures are never disguised as transient failures.

## Memory and code intelligence

Durable state does not live in transcripts. Context is assembled through
progressive disclosure:

1. always-on rules and workflow contracts;
2. sprint-scoped specifications, decisions, and handoffs;
3. project knowledge and accepted ADRs;
4. exact Git/file/ripgrep lookup;
5. Tree-sitter and LSP symbol context;
6. optional graph or SCIP indexes.

Indexes are derived caches and must prove freshness against source commit and tool
version. Critical references require file or language-server evidence.

## Status and next actions

`ProjectStatusSnapshot` is immutable and versioned. The active sprint is an
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

## Security and observability

Project content, provider output, generated files, skills, hooks, and prompts are
untrusted data. Policy and permission checks precede execution. Logs and
diagnostic bundles pass through structured redaction before persistence or
display; full environment dumps and raw credentials are forbidden.

OpenTelemetry traces cover startup, updates, providers, routing, attempts,
workflow transitions, recommendations, and reviews. Metrics and diagnostics use
stable codes and never contain localized user content or secrets.

## Release acceptance

The release gate requires:

- reproducible `win-x64` and `win-arm64` bundles;
- matching source, artifact, tag, and release versions;
- checksums, provenance, and SBOM;
- clean-profile installer and update tests;
- CLI/TUI and Desktop capability parity;
- English/Russian localization completeness;
- scoped-configuration and artifact-language acceptance;
- updater, provider, workflow, fallback, recovery, and security suites;
- no unresolved blocking review findings.
