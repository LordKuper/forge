# Changelog

User-facing Forge changes are listed by release, newest first.

## v0.9.1

### Fixed

- Sprint creation is now crash-safe and idempotent: a sprint's id is derived
  deterministically from the project's own stable id (not its path, so a
  relocated project directory or a differently cased Windows path still
  resolves to the same sprint) and the caller's idempotency key. Every
  creation write is safe to repeat, and a sprint stays invisible to listing
  until a completion marker lands last, so a crash at any point never leaves
  an orphaned or partially visible sprint. A retried creation call also
  repairs a missing manifest registration and reuses an already-frozen
  definition instead of re-deriving one from a possibly different retry.
- Starting an attempt, completing an attempt, and resolving a human gate now
  resume correctly after an interrupted prior call instead of risking a
  stuck node or a lost result, and report failure (instead of silently
  continuing with stale data) when a durable write conflicts. A retried
  command converges on the same terminal outcome instead of abandoning the
  interrupted attempt or silently flipping an already-committed
  success/failure or approve/reject decision to its opposite.
- A sprint can no longer reach `ready_to_finalize` while it has an open
  finding of any severity; resolving the last open finding lets an
  already-settled sprint advance immediately, whether it is still `running`
  or was moved to `blocked` by a finding that arrived after every node had
  already settled.
- Sprint dependencies are now validated as canonical, immutable references: a
  commit dependency must be a full lowercase object id (never a branch name,
  an abbreviated sha, or one matched with a trailing newline), an artifact
  dependency must be a full `sha256:` digest, and an artifact dependency
  that names its source sprint is always rejected, since Forge cannot yet
  durably verify what that sprint published.
- Concurrent finding and node-result writes in the same sprint no longer
  risk losing one update to another's racing write; a node result that
  already exists for an attempt is compared, not assumed identical, and a
  genuinely different result for the same attempt is rejected instead of one
  silently overwriting the other.
- An attempt now records which node it belongs to; completing an attempt for
  the wrong node is rejected instead of silently settling the wrong pair.
- A workflow transition can no longer be redirected to an unintended state
  through a caller-supplied extra argument.
- A sprint now enters and leaves `awaiting_human` alongside its human gates
  and correctly returns to `running` or moves to `blocked` once every gate
  resolves — including two or more gates that become eligible one after
  another, and after a restart mid-sequence. A gate promotion interrupted
  between its two durable writes now resumes automatically on any later
  graph advance instead of leaving the gate stuck at `running` forever.
- Fixed the project manifest's registered-sprints list failing to survive a
  fresh read, which could silently drop previously registered sprints the
  next time the manifest was written.
- Findings recorded before this release (stored in a single shared
  `findings.json`) are now migrated automatically and safely to the new
  per-finding-file layout on first access, instead of silently disappearing.
- A sprint `blocked` for any reason now requires the operator's explicit
  `resume_sprint`/`run_sprint` decision to advance again. Resolving a
  finding only advances a sprint whose block was actually caused by that
  finding; it can no longer also advance one blocked by a stuck node or a
  rejected human gate just because every node happens to look settled at
  that moment (for example after the node was separately retried and
  skipped).

### Added

- Added the `sprint_dependency_invalid`, `sprint_dependency_not_published`,
  and `attempt_ownership_mismatch` diagnostic codes.

### Removed

- Removed the `sprint_dependency_not_terminal` diagnostic code: an artifact
  dependency naming its source sprint is now always rejected regardless of
  that sprint's state, so it is never produced.

### Changed

- Findings are now stored one file per finding under
  `.forge/sprints/{id}/findings/` instead of a single shared `findings.json`.
- A sprint with a human gate awaiting a decision now halts starting new work
  on any other node in the same sprint, even one entirely independent of
  that gate, until every pending gate is resolved. Previously the sprint
  stayed `running` and independent work could continue in parallel.

## v0.9.0

### Added

- Added Stage 6: durable, independent sprints and the workflow engine.
  Every sprint/node/attempt transition is an append-only, localization-safe
  event under `.forge/sprints/{id}/events.jsonl`; current state is always
  folded from that log, so a crash can never leave state inconsistent with
  its own history, and a crash exactly mid-write is discarded cleanly
  rather than corrupting recovery. Concurrent mutation is rejected through
  optimistic concurrency on each aggregate's own version (serialized against
  concurrent operations on the same sprint), validated against the frozen
  sprint/node/attempt state machines by the store itself, and a retried
  command with the same idempotency key is a safe no-op instead of a
  duplicate transition. Concurrent sprints in the same project stay fully
  isolated and both resume deterministically after a restart, with no
  in-memory or transcript dependency.
- Added `SprintOrchestrator`, creating sprints and advancing them through the
  frozen `sprint` state machine (create, run, cancel, resume) one legal
  transition at a time. Created sprints are registered in the project
  manifest's `sprints` list.
- Sprint creation freezes its immutable inputs once and for good: the
  current Git commit (resolved through `git rev-parse HEAD`, never a shell
  string), the workflow contract version, a snapshot of the project's
  effective configuration, and the user's conversation language
  (`language.llm`) kept separate from the project's own artifact-language
  configuration. Later configuration or Git changes never retroactively
  affect an existing sprint.
- Added sprint dependency declarations: a dependency on a raw immutable Git
  commit is always accepted; a dependency on another sprint's artifact is
  accepted only once that sprint has reached `completed`, rejecting mutable
  cross-sprint input before any sprint is created.
- Added the deterministic sprint node/attempt scheduler: a sprint's frozen
  graph advances nodes to `ready` only once their declared dependencies have
  succeeded or been skipped, work-node failures automatically retry up to a
  bounded limit before blocking the sprint, and a sprint automatically
  reaches `ready_to_finalize` once every node has settled successfully.
- Added human gate nodes: as soon as one becomes eligible while its sprint is
  running, it moves straight to awaiting a decision; approving or rejecting
  it is durable and schema-validated like every other transition, and a
  rejected gate blocks its sprint immediately rather than leaving it stuck
  with nothing left to do.
- Added manual node retry (matching the existing `retry_failed_node`
  recommendation), letting an exhausted node be re-armed after a fix.
- Added findings (record/resolve) and structured handoffs (record/read,
  identified by their own id, so a second handoff for the same node no
  longer overwrites the first) per sprint, and node results recording each
  attempt's digest-based outcome — all validated against their frozen v1
  schemas before anything becomes durable.
- Added the `sprint_not_found`, `sprint_transition_invalid`,
  `workflow_event_conflict`, `repository_head_unavailable`,
  `sprint_dependency_not_terminal`, `sprint_graph_invalid`,
  `sprint_not_running`, `node_not_found`, `node_kind_mismatch`,
  `node_transition_invalid`, `finding_not_found`, `workflow_record_invalid`,
  `workflow_transition_invalid`, `workflow_store_busy`, and
  `workflow_log_corrupted` diagnostic codes.

This is a deterministic engine only: no real node executor exists yet
(Stage 7 provides isolated Git worktrees for one), and there is no
CLI/Desktop surface for any sprint capability yet — both are separate,
later work.

### Security

- Durable sprint/node/attempt/finding transitions store only localization
  keys and structured arguments, never rendered text (structured handoffs
  remain the one contractually free-text record, written for a model to
  read rather than shown as localized UI), and stay identical regardless
  of the host's culture setting.

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
