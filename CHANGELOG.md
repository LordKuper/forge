# Changelog

User-facing Forge changes are listed by release, newest first.

## v0.12.1

### Fixed

- A crash exactly mid-append to a sprint's durable event log could previously
  leave the file with an incomplete trailing line; any later append onto that
  file silently lost its own event while still reporting success, and a
  second append could make the whole sprint permanently unreadable. This
  covered a crash that dropped part of the written JSON and, separately, one
  that flushed a complete event but not its own line terminator. Reads now
  discard any such incomplete trailing content immediately, so the file is
  clean again the moment anyone next touches it; genuine corruption anywhere
  else in the file is still reported rather than silently dropped.
- Rejecting a human gate node left its sprint stuck in `running` forever —
  neither blocked nor able to finish — since a rejected gate never
  auto-retries the way a failed work node does. A rejected gate now blocks
  its sprint immediately, matching every other unrecoverable node failure.
- A node result with malformed data (for example a corrupt digest) could
  previously be reported as durably succeeded with no result actually
  recorded, wedging the sprint. Malformed results are now rejected before
  anything becomes durable.
- Retrying a failed node a second time with the same request now genuinely
  replays the first outcome instead of re-running the action. Resolving a
  human gate always reaches a real decision on retry rather than silently
  reporting success without one, though a retry after an interrupted
  resolution may act through a fresh internal attempt rather than resuming
  the original one.
- Node and attempt transitions are now validated against their frozen state
  machines by the durable store itself, not only by callers, closing a gap
  where an illegal transition could otherwise have been persisted silently.
- Two operations on the same sprint running concurrently in one process can
  no longer both act on the same version and corrupt its history.
- A handoff is now identified by its own id rather than by node id, so a
  second handoff for the same node no longer silently overwrites the first.

### Changed

- Node identifiers in a sprint's graph are now constrained to a safe
  lowercase alphanumeric form, since they are used to name files on disk.

## v0.12.0

### Added

- A sprint now freezes its conversation language (from the user's
  `language.llm` preference) and an artifact-policy snapshot hash
  separately from each other and from the project's artifact-language
  configuration, so a personal interaction language can never leak into a
  project's shared, committed artifact language.
- This completes Stage 6 (durable independent sprints and the workflow
  engine): concurrent sprints in the same project now stay fully isolated
  and both resume deterministically after a restart, with no in-memory or
  transcript dependency. No real node executor exists yet (Stage 7 provides
  isolated Git worktrees for one), and there is still no CLI/Desktop
  surface for any sprint capability.

### Security

- Durable sprint/node/attempt/finding transitions store only localization
  keys and structured arguments, never rendered text (structured handoffs
  remain the one contractually free-text record, written for a model to
  read rather than shown as localized UI), and stay identical regardless
  of the host's culture setting.

## v0.11.0

### Added

- Added the deterministic sprint node/attempt scheduler: a sprint's frozen
  graph advances nodes to `ready` only once their declared dependencies have
  succeeded or been skipped, work-node failures automatically retry up to a
  bounded limit before blocking the sprint, and a sprint automatically
  reaches `ready_to_finalize` once every node has settled successfully.
- Added human gate nodes: as soon as one becomes eligible while its sprint is
  running, it moves straight to awaiting a decision; approving or rejecting
  it is durable and schema-validated like every other transition.
- Added manual node retry (matching the existing `retry_failed_node`
  recommendation), letting an exhausted node be re-armed after a fix.
- Added findings (record/resolve) and structured handoffs (record/read) per
  sprint, and node results recording each attempt's digest-based outcome,
  all validated against their existing frozen v1 schemas.
- Added the `sprint_graph_invalid`, `sprint_not_running`, `node_not_found`,
  `node_kind_mismatch`, `node_transition_invalid`, `finding_not_found`, and
  `workflow_record_invalid` diagnostic codes.

### Fixed

- Corrected node identity to the stable, workflow-assigned string
  `node-result.schema.json` specifies (e.g. `"spec"`), rather than a random
  value — the earlier persistence slice used the wrong shape before any node
  actually existed to expose the bug.

This is the scheduling slice of Stage 6. It is a deterministic engine only:
no real node executor exists yet, and there is still no CLI/Desktop
surface for any of it.

## v0.10.0

### Added

- Sprint creation now freezes its immutable inputs once and for good: the
  current Git commit (resolved through `git rev-parse HEAD`, never a shell
  string), the workflow contract version, and a snapshot of the project's
  effective configuration at that moment. Later configuration or Git changes
  never retroactively affect an existing sprint.
- Added sprint dependency declarations. A dependency on a raw immutable Git
  commit is always accepted; a dependency on another sprint's artifact is
  accepted only once that sprint has reached `completed`, rejecting mutable
  cross-sprint input before any sprint is created.
- Added the `repository_head_unavailable` and `sprint_dependency_not_terminal`
  diagnostic codes.

## v0.9.0

### Added

- Added durable, event-sourced sprint persistence: every sprint/node/attempt
  transition is an append-only, localization-safe event under
  `.forge/sprints/{id}/events.jsonl`, and current state is always folded from
  that log, so a crash can never leave state inconsistent with its own
  history. Concurrent mutation is rejected through optimistic concurrency on
  each aggregate's own version, and a retried command with the same
  idempotency key is a safe no-op instead of a duplicate transition.
- Added `SprintOrchestrator`, creating sprints and advancing them through the
  frozen `sprint` state machine (create, run, cancel, resume) one legal
  transition at a time. Created sprints are registered in the project
  manifest's `sprints` list.
- Added the `sprint_not_found`, `sprint_transition_invalid`, and
  `workflow_event_conflict` diagnostic codes.
- Added the `node`/`attempt` state machines and durable event contract to the
  domain model, mirrored exactly from the frozen v1 state-machine contract.

This is the persistence slice of Stage 6. `forge sprint`/Desktop sprint
management, node/attempt DAG scheduling, retries, human gates, findings, and
handoffs land in subsequent Stage 6 work.

## v0.8.0

### Added

- Added Forge-managed provider toolchain installation for the Codex and Claude
  Code CLIs, using each vendor's own recommended native Windows install/update
  mechanism at its documented fixed path, bounded by a timeout so a hung
  installer cannot block indefinitely.
- Added the `forge models` command (`GetProviderHealth`), which reports
  read-only provider discovery by default, with culture-invariant `--json`
  output; `forge models --refresh` installs or updates any provider that is
  not ready.
- Added the `provider_update_failed` diagnostic code for provider install,
  update, or recheck failures, mapped to CLI exit code 7.
- The startup `Providers` check now performs real, read-only, offline
  discovery instead of always blocking; sprint work stays blocked until both
  providers report `ready`.
- Added provider execution adapters for Codex and Claude Code: prompts and
  output never pass through a shell, JSON/JSONL output is parsed against each
  vendor's documented event shape, and process failures normalize into a
  stable, redacted failure category (authentication, rate-limited, quota,
  policy, transient, malformed output, or unknown).

## v0.7.0

### Added

- Added the ordered fail-closed startup sequence shared by the CLI and Desktop
  surfaces, reporting user configuration, language, platform, update strategy,
  release, provider, and project checks with stable diagnostic codes. A failed
  check refuses project mutations and leaves recovery as the only safe action.
- Added explicit project-root verification and confirmed `.forge/`
  initialization that stages a complete tree, publishes it atomically, stays
  idempotent, discards staging on cancellation, and never overwrites an unknown
  project directory.
- Added the versioned project status snapshot with deterministic recovery and
  initialization recommendations, stable idempotency keys, and rejection of a
  stale expected state version without side effects.
- Added the `forge doctor`, `forge init`, `forge status`, `forge next`, and
  `forge config` commands with culture-invariant `--json` output, contract exit
  codes, and diagnostics on standard error.
- Added startup recovery that quarantines unreadable configuration, keeping every
  unusable revision for diagnosis and falling back to the built-in defaults,
  available as `forge doctor --recover` and from the Desktop surface. Readable
  configuration is never moved.
- Added the Desktop startup, project, recommendation, diagnostic, and
  configuration views, including an explicit project root and configuration
  scope selection, restored from durable application state.
- Added scoped configuration reading and editing with provenance, cross-scope
  rejection, key-typed value parsing, and project artifact languages independent
  of the user language. `interaction.confirm_destructive` now controls whether a
  mutation requires explicit confirmation.
- Added application of the configured interface language to both surfaces
  instead of the ambient operating-system culture.

### Fixed

- Fixed the shared host registering an empty configuration key registry, which
  made every scoped configuration lookup fail.

## v0.6.5

### Fixed

- Disabled unavailable ReadyToRun optimization during Windows bundle publishing.

## v0.6.4

### Fixed

- Removed brittle exact-version checks for runner-provided tools.

## v0.6.3

### Fixed

- Stabilized Windows process-lifecycle tests in CI.
- Refreshed the pinned Windows release-runner image.

## v0.6.1

### Changed

- Consolidated the runtime and test projects while preserving separate CLI,
  Desktop, and platform-neutral/Windows update boundaries.
- Replaced automatic Codex review with a dedicated independent agent review for
  every pull request.
- Replaced the obsolete non-English architecture source with a concise English
  research summary that links to canonical architecture and planning documents.

## v0.6.0

### Added

- Added a per-user Windows installer and update staging flow with rollback
  protection for versioned bundles.

### Removed

- Removed release provenance and publisher-identity verification; release assets
  are now checked only for name, size, and SHA-256 consistency.

## v0.5.1

### Changed

- Standardized repository text formatting, LF line endings, pre-PR validation,
  and automatic Codex review completion rules.

## v0.5.0

### Added

- Added a Windows updater foundation that re-verifies a released bundle while
  staging it under the per-user version layout, atomically switches the current
  version, and restores the prior version on rollback.

## v0.4.1

### Changed

- Added scoped Codex code-review rules, including full-scope review for the
  first three iterations and critical findings only thereafter.

## v0.4.0

### Added

- Added a platform-neutral self-update core that detects and normalizes update
  targets, resolves exactly one strategy before release access, selects newer
  stable GitHub releases with ETags, verifies release assets, and coordinates
  staging, restart handshakes, and rollback.
- Added updater contract, architecture, and regression coverage for unsupported
  platforms, release selection, verification failures, restart context, and
  rollback.

## v0.3.0

### Added

- Added the .NET 10 SLNX solution with layered runtime, updater, provider,
  presentation, configuration, localization, bootstrap, CLI, and MAUI Desktop
  projects.
- Added a shared English/Russian localization catalog and a localized CLI status
  command and Desktop startup page.
- Added scoped user/project configuration registries, provenance resolution,
  independent migrations, scope enforcement, and atomic writes with recovery.
- Added unit, integration, acceptance, architecture, security, and installer
  tests plus Windows x64/Arm64 publish profiles.
- Added CI validation for locked restore, formatting, warnings-as-errors builds,
  tests, and high/critical dependency vulnerabilities.

### Fixed

- Made persisted user and project configuration conform to the published v1
  schemas, reject invalid writes, durably flush atomic replacements, and recover
  validated previous revisions.
- Made the MAUI Desktop restore graph deterministic for Windows x64 and Arm64.
- Prevented Generic Host configuration from consuming Forge CLI options such as
  `--help`, and normalized repository text files to LF across clean checkouts.
- Aligned sprint states with the v1 workflow contract and ensured cancellation
  terminates child process trees.

### Security

- Expanded structured and value-based secret redaction to cover every credential
  category required by the v1 contract, including nested payloads.

## v0.2.1

### Added

- Restored the complete original research and target-system design document as a
  verbatim Russian-language source artifact.

### Changed

- Clarified the relationship between the complete source design, the canonical
  English architecture overview, and the implementation plan.

## v0.2.0

### Added

- Defined the accepted MVP boundaries, trust model, state machines, diagnostics, localization, scoped configuration, and presentation parity contracts.
- Added versioned JSON Schemas and machine-readable capability, recommendation, configuration, and lifecycle registries.
- Added an automated contract gate that validates schema identity, closed state transitions, surface parity, recommendation safety, and configuration ownership.
- Added locked Draft 2020-12 meta-schema, reference-resolution, and valid/invalid compatibility-fixture validation.

### Changed

- Required PowerShell 7.6.3 or newer for release validation.
- Required publication workflows to open ready-for-review PRs unless draft status is explicitly requested.
- Required autonomous automatic-review cycles until every actionable finding is resolved.
- Replaced non-English source documents with concise English architecture and implementation-plan artifacts.

## v0.1.0

### Added

- Published the project development and contribution rules.
- Added automatic release validation, version tagging, and GitHub Release publication.

### Fixed

- Prevented releases from reusing the `main` version or publishing from non-`main` branches.
- Enforced semantic version increments and breaking-change declarations while safely publishing concurrent releases.
