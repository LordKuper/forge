# Changelog

User-facing Forge changes are listed by release, newest first.

## v0.73.0

### Added

- Sprint timeline entries are now shown in the user's own language on both Desktop and the CLI,
  instead of the raw internal event key (e.g. "Sprint completed." / "Спринт завершён." instead of
  `workflow.sprint_completed`). Entries that carry an operator- or agent-authored value -- a posted
  message, a node's summary, a supersession instruction, a rewind's reason, or a routing decision --
  still show that exact text inside the localized sentence.

### Fixed

- Added an automated check that catches an English string accidentally left untranslated in the
  Russian surface (previously only the presence of every key was verified, not that its value was
  actually translated).

## v0.72.0

### Added

- The sprint workspace timeline can now show what actually happened, not just system events: you can
  post a free-text message to a sprint (`forge sprint message <sprint-id> "<text>"`, or the new
  message composer in the Desktop sprint workspace), and it appears in the timeline durably,
  redacted, and ordered alongside every workflow event. A sprint's timeline also now surfaces the
  summary an agent leaves behind when it completes a planning or implementation step, attributed to
  the node that produced it, and never lingers past a stage rewind that invalidates it. The message
  composer remembers an unsent draft across app restarts,
  independently of the existing move-to-stage reason draft.

## v0.71.0

### Added

- Desktop's workspace sidebar can now be collapsed to a narrow icon-only rail, giving the content
  area the full window width, and expanded back. The toggle is keyboard-reachable and screen-reader
  named ("Collapse sidebar" / "Expand sidebar", switching with its state); the collapsed rail keeps
  the toggle itself visible as its own re-expand affordance. The chosen state persists across app
  restarts, independently of any one project.

## v0.70.0

### Added

- Capability negotiation is now enforced on the client side: before sending a mutation, the CLI and
  Desktop check it against the capability set the connected Host actually advertised during the
  handshake. If an older Host does not yet support the operation, the request is never sent — it is
  rejected immediately with a clean, structured diagnostic instead of the Host's generic "unknown
  request kind" error. On the main workspace surface, Desktop shows a localized message explaining
  that the project's Host needs to be upgraded; elsewhere on Desktop (e.g. project settings) and on
  the CLI, the outcome carries the `capability_not_supported` diagnostic code it already reports for
  other failures, and the CLI's exit code (14, `compatibility`) now distinguishes it from a generic
  internal error. This only changes behavior when a client is newer than the Host it talks to — Host
  and client shipping together (the common case today) is unaffected.

## v0.69.1

### Fixed

- A stop request could still lose its race against an implementation/planning/review attempt that
  was about to succeed: the durable stop intent was only checked once, before the provider ran, so
  an attempt that finished right as the stop landed could still commit and integrate its changes
  (implementation), or complete the node and let the sprint advance (planning/review), instead of
  being cancelled. Each executor now re-checks the durable stop intent right before its own point of
  no return and honors a stop that landed in between, including when a concurrent rewind converges
  that same stop before the re-check runs. Implementation also re-checks a second time immediately
  before publishing to the sprint's integration branch, closing the remaining window between
  committing to the attempt's own branch and that publish.
- Rewinding a sprint to an earlier stage could, under a genuinely concurrent conflicting change, mark
  the rewind as durably converged before it had actually finished walking the sprint back to
  `ready`/`blocked`, leaving it stuck with no way to resume. The rewind now only marks itself
  converged once that final walk actually completes; a conflict now correctly resumes on the next
  attempt instead of being silently sealed as done.
- A sprint whose rewind was interrupted by a genuine conflict had no way to be unstuck from Desktop:
  the workspace offered no action at all while the rewind was pending, and the CLI was the only
  surface that could resume it. Desktop now offers a dedicated action to resume and finish an
  interrupted rewind.

## v0.69.0

### Added

- Added `forge models quota [--json]` and a Desktop sidebar quota status row, both truthfully
  reporting `unknown` for every provider today: neither Claude Code's nor Codex's CLI exposes a
  verified account/model quota signal (see ADR 0052). The full `ready`/`limited`/`unavailable`/
  `unknown`/`stale` state set is wired end to end with distinct text and an accessible name for
  every state, so a future provider that does publish real quota data needs no rendering changes.
  Quota is never inferred from a provider's own retry/rate-limit failures, and a sprint's retry
  budget is never presented as account quota.

### Fixed

- The Desktop sidebar's provider-quota status label now has a screen-reader accessible name; it
  previously showed only visible text.
- `forge models quota` now writes its worst-case diagnostic code to the diagnostics channel, like
  every other query command (e.g. `forge next`); it previously exited `0` without ever emitting a
  machine-readable code, leaving scripted callers with only the human-readable row text.
- The Desktop sidebar no longer issues a second, uncached provider-toolchain probe (spawning extra
  `--version`/authentication child processes) to compute the quota row on every render; it now
  reuses the toolchain check the same render already performed.

## v0.68.0

### Added

- Added a chronological sprint timeline: incremental "load more" paging backed by the real cursor,
  automatic polling for new items while the page is open, filtering by item type, per-item
  copy-to-clipboard, collapsed-by-default technical detail (correlation/causation ids, structured
  arguments), unread-position tracking that survives a restart, and a "mark all read" action.
  Timeline rows never render raw provider output — only the existing redacted, bounded event
  projection.
- Added a typed contextual-action renderer driven by the Host's own action list: sprint
  run/resume/cancel now show and enable themselves from that list rather than always-on raw
  buttons.
- Added a stop-current-operation control, visible only when the sprint has an exact stoppable
  operation. Its confirmation names the exact node and attempt being stopped and is clearly
  distinct from attempt supersession and sprint cancellation in label, explanation, and result.
- Added a stage-transition (move-to-stage) control that lists only stages in the sprint's own
  frozen workflow, shows the Host's fresh assessment (satisfied/unsatisfied prerequisites and, for
  a rewind, exactly what would be superseded) before confirming, and re-validates immediately
  before committing — a target is never shown as available from a locally cached or computed
  guess. Rewinding requires a reason, which is preserved as a draft across an app restart.

### Changed

- Replaced the sprint workspace's placeholder controls with a real sticky status header showing
  project name, sprint sequence, lifecycle state (including `paused`), current stage, stage
  progress, last activity, open finding count, retry budget, and `resume_not_before`; UUIDs, the
  project root, base commit, and workflow id now live behind an expandable "details" toggle instead
  of crowding the main row.

### Removed

- Removed every remaining manual project/sprint/node/attempt-id entry field from the sprint
  workspace: gate, confirm, test-work, finalize, and attempt supersession now resolve their target
  from the already-selected sprint's own current state.

## v0.67.0

### Added

- Replaced the single scrolling Desktop form with a two-panel workspace shell: a sidebar listing
  every cataloged project, its non-terminal sprints (ordered by attention, then running, paused,
  blocked/failed, and other active sprints, newest first), a completed/cancelled history entry, an
  add-project action, a global Forge settings entry, and a bottom status row for provider readiness
  and quota (reported as not yet available until a later release adds real quota data).
- Added a project overview page showing startup/repository readiness, active sprint cards with
  attention reasons, recent completed/cancelled sprints, the highest-ranked suggested actions, and
  initialize/recover/create-sprint actions.
- Added a Forge settings page with typed controls for interface language, interaction language,
  provider language (each with an inherit option and its effective source), confirm-destructive,
  enabled providers with health, and notifications — every value shows where it came from, and an
  interface-language change applies immediately without restarting Desktop.
- Added a project settings page with typed controls for artifact languages, context token budget,
  and allowed models (each with its own source), plus a read-only project root/id, a local display
  alias, folder relinking, removing the project from the local catalog, provider integration
  inspection/install/removal, startup recovery, and diagnostic bundle generation.
- Added a folder picker for adding or relinking a project from the sidebar/project settings, backed
  by the real Windows folder-picker dialog.
- The last selected project, sprint, and page now survive a Desktop restart.

## v0.66.0

### Added

- Added a local, user-scoped project catalog: `forge project add <root>` registers an
  already-initialized project by its own manifest id; `forge project list [--json]` shows every
  known project with its display alias, last-opened time, and last-selected sprint/route;
  `forge project alias <id> <alias>` sets or clears a local display alias without touching the
  project's own configuration; `forge project relink <id> <new-root>` re-points a catalog entry to a
  moved project only after verifying the new root's manifest id actually matches; `forge project
  remove <id>` drops the catalog entry only, never the repository or its `.forge/` directory; and
  `forge project select <id> [--sprint <id>] [--route <text>]` records the last selected sprint and
  route so a future navigation shell can restore it after restart.
- Added `forge workspace summary [--json]`, a bounded query across every cataloged project reporting
  availability, active sprints with their current stage and progress, attention reasons, active
  operation presence, and provider health — without loading any project's full sprint timeline.
- Added `forge sprint timeline <id> [--after <cursor>] [--json]`, a cursor-paged, chronologically
  ordered projection of a sprint's existing workflow history (transitions, stop requests, stage
  revisions) suitable for incremental loading. Content is redacted before being handed back and again
  before being rendered, so a credential or secret recorded in an operator-authored instruction or
  rewind reason never reaches the timeline.
- Added `forge workspace actions [--sprint <id>] [--json]`, listing the concrete actions available
  right now for a project or a specific sprint (resume, run, cancel, stop the active operation, or
  move to another workflow stage), each with its safety class, whether confirmation is required, any
  blocking reasons, and a stable key so a repeated or stale request never has an unintended effect.

### Changed

- `workspace.summary`, `sprint.timeline`, and `workspace.available_actions` move from reserved to
  implemented on the Host and CLI; Desktop support for all three is deferred to a later release.

### Fixed

- `workspace.available_actions`' `resume_sprint`/`run_sprint`/`cancel_sprint` rows now report the
  expected version and idempotency key the sprint lifecycle mutation itself actually validates
  against, instead of the sprint's whole journal position — submitting a reported action back
  unmodified was previously rejected as stale as soon as any node/attempt event had ever been
  appended.
- `forge sprint timeline`'s redaction now applies uniformly to plain-text output, `--json` output,
  and the Host's wire response alike, and covers every free-text event field, not only its
  arguments — closing a gap where `--json`/Host consumers received no second redaction pass at all.
- A `forge sprint timeline` cursor is now bound to the sprint it was issued for; reusing one against
  a different sprint is rejected instead of silently skipping that sprint's own early items.
- `forge project list`/`forge workspace summary` now report "No projects in the catalog yet." for an
  empty catalog instead of the unrelated "No sprints yet." message.
- The local project catalog (`catalog.json`) now serializes concurrent reads/writes within one
  process, recovers from its `.previous` backup instead of throwing when corrupted, and rejects a
  catalog written by an unrecognized schema version instead of silently discarding its unknown
  fields on the next write.

## v0.65.0

### Added

- Added `forge sprint assess-stage <id> --target-stage <stage-id> [--json]`, a read-only check that
  reports whether a sprint can move to another workflow stage: the direction (advance or rewind),
  which prerequisites are satisfied or still blocking, what an active operation or a rewind would
  affect, and whether a bounded reason and confirmation are required.
- Added `forge sprint move-stage <id> --target-stage <stage-id> [--reason <text>] [--yes]`, which
  commits an already-assessed move. Advancing to a later stage only ever activates a target whose
  prerequisites already hold — it never fabricates a result or skips a mandatory stage. Rewinding to
  an earlier stage requires a bounded reason (4000 characters) and confirmation, stops every active
  operation in the affected downstream stages first (including a parallel workflow's concurrently
  running branches), starts a new stage revision, and marks every downstream result, decision,
  finding, and artifact as superseded — prior history is never deleted or rewritten, and superseded
  evidence can no longer satisfy a later prerequisite check.
- The Host always re-checks a stage move's prerequisites and expected state immediately before
  committing it; a stale or mismatched request is rejected without any partial change, and repeating
  the same move (advance or rewind) is safe and never records a second stage revision. A Host crash
  at any point during a rewind is safe: the sprint durably remembers an unconverged rewind
  independently of its ordinary node/sprint state, so the very next stage-move or assess-stage
  request against that sprint automatically finishes converging it — regardless of which step the
  crash interrupted, and regardless of whether that request carries the original (now-stale) tokens,
  fresh ones, or an unrelated target. The sprint also refuses to finalize while a rewind has not yet
  converged, so a crash can never let a half-finished rewind reach `completed`, and `assess-stage`
  reports the in-progress rewind directly rather than misreporting a stage's direction while one is
  pending.
- A blocked prerequisite (an open finding, a dirty integration worktree, an exhausted retry budget,
  or a disallowed model policy) never blocks rewinding to an earlier stage — rewinding past the
  affected work is the intended way to resolve it. These prerequisites still gate advancing to a
  later stage.

## v0.64.0

### Added

- Added a `forge attempt stop <attempt-id> --sprint <id> [--yes]` command that stops the sprint's
  current active operation without cancelling the sprint. Stopping terminates the running
  provider's entire process tree, discards its partial worktree so no unfinished change reaches
  the integration branch, and leaves the sprint `paused` with the interrupted step ready to run
  again — without spending any of its automatic retry attempts.
- Resuming a `paused` sprint (`forge sprint resume`) now returns it to `ready`; running it again
  starts a fresh attempt from the project's current integration point.
- A Host crash at any point during a stop is safe: on restart, Forge finishes converging the stop
  instead of resurrecting the interrupted attempt or leaking its process or worktree.

## v0.63.0

### Added

- Added a `paused` sprint state to the workflow contracts, reached only by
  cancelling a sprint's active operation without ending the sprint — the
  foundation for an upcoming Stop action that lets you interrupt a running
  step and safely resume it later, independent of cancelling the sprint.
- Added an append-only stage revision concept to the workflow domain model,
  enabling a future safe way to reopen a completed stage without losing or
  rewriting its prior history.
- Reserved seven new capability ids (workspace summary, sprint timeline,
  available actions, stop current operation, stage-transition assessment,
  move-sprint-to-stage, and provider quota status) in the versioned
  capability contract. None is implemented yet, so today's Host and Desktop
  behave exactly as before; the reservation lets a future release add each
  capability without an older Host or Desktop silently attempting an
  operation it does not support.
- Added English and Russian localization for the paused sprint state label.

## v0.62.3

### Fixed

- Fixed console windows briefly flashing on Windows for every helper process
  Forge runs, including `git` commands, provider version/authorization
  checks, provider agent runs, and provider install/update.

## v0.62.2

### Fixed

- Fixed the Windows command shim discarding all CLI arguments, including
  `--version`, `--help`, and subcommands.
- Rerunning the bootstrap installer now upgrades an existing installation and
  automatically migrates the affected `0.62.0` and `0.62.1` command shim.

## v0.62.1

### Fixed

- Fixed the PowerShell 7 bootstrap entry point failing because its local
  platform variable collided case-insensitively with the read-only
  `$IsWindows` automatic variable.

## v0.62.0

### Added

- Added a one-command Windows bootstrap script that selects the x64 or Arm64
  stable release, verifies its published size and SHA-256 checksum, and runs
  the existing per-user Forge installer.

## v0.61.1

### Changed

- Confirmed and documented that OpenTelemetry tracing/metrics are
  deliberately out of scope for the MVP (every standard exporter either
  phones home over the network or needs a local collector this project
  doesn't have), closing out an internal planning item. `forge doctor
  --bundle` and the redacted structured-log file already cover the
  diagnosability need. No functional change.

## v0.61.0

### Added

- Added `forge eval`, a pass/fail evaluation of the updater, provider,
  bootstrap, and workflow subsystems, printed as JSON.
- Added an optional project model policy: `forge config project
  models.allowed_models '["<provider>:<model>", ...]'` restricts which
  model a provider may use. Creating a sprint now refuses up front if a
  provider's model is not on the configured list; an unlisted provider
  stays unrestricted. Reported by `forge eval` too, with no sprint needed.
  A policy entry naming a provider that isn't enabled (a typo, or a
  provider not yet turned on) is flagged by `forge eval` rather than
  silently doing nothing.

## v0.60.0

### Added

- The Host process now writes a redacted, persistent structured log
  (`host_started`/`host_stopped` lifecycle events so far) to a local
  per-instance file, so an operator can inspect what happened after the
  console that started a headless Host has already closed. Never sent
  anywhere off the machine.

## v0.59.6

### Added

- Added tests proving the Windows updater's host self-check correctly
  reports success and failure against a real child process, closing the
  last untested path of the update pipeline's self-check step.

## v0.59.5

### Changed

- Confirmed and documented that release bundles are already reproducible,
  version-matched, checksummed, and SBOM-covered, closing out an internal
  planning item. No functional change.

## v0.59.4

### Changed

- Extended automated test coverage proving every internal versioned data
  contract rejects an unsupported schema version, and added a check that
  keeps that guarantee from silently lapsing as new contracts are added.
  No functional change.

## v0.59.3

### Security

- Added an independent defense-in-depth check for the internal file path a
  test-work review-floor decision is recorded under, so it can never
  resolve outside its sprint's own directory even if a future change ever
  bypassed the existing node-id validation that already prevents this in
  practice.

### Changed

- The automated release publication step now has a manual retry path for
  the rare case where it does not start on its own after a change lands,
  so a release is never stuck unpublished with no way to recover it.

## v0.59.1

### Fixed

- The Desktop app's provider toolchain list, startup checks list, suggested
  actions list, and configuration values list now indent each row the same
  way the CLI does (`forge models`, `forge doctor --startup`, `forge
  status`, `forge config show`), matching the CLI's own formatting instead
  of rendering flush-left.

## v0.59.0

### Added

- `forge doctor --bundle` produces an allowlisted, redacted diagnostic
  bundle as JSON: Forge/protocol/provider versions, startup checks, project
  state, event log integrity, worktree registrations, circuit-breaker and
  retry-budget state, and writable-directory probes. It never includes
  prompts, provider output, diffs, source contents, raw command lines,
  credentials, environment values, or unredacted paths — a section that
  cannot be safely collected is omitted and named rather than guessed at.

## v0.58.0

### Added

- The Desktop app can now record a definition-of-done confirmation, a
  test-work decision, and a sprint finalization — the same three human-only
  decisions previously available only from `forge confirm`/`forge
  test-work`/`forge finalize`. Each reuses the sprint id already entered for
  gate/attempt actions, defaults to its capability's canonical node, and
  shows a confirmation dialog naming exactly what it is about to record
  before applying it.

## v0.57.0

### Added

- A running sprint's `finalization` node can now be resolved by a human
  operator: `forge finalize --sprint <id> --yes` merges the sprint's real
  code changes into the project's own default branch (a fast-forward-only
  merge, refusing if the working directory is dirty, on the wrong branch,
  or has diverged — never running a branch checkout on its own) and marks
  the sprint completed. This is the step that actually lands a sprint's
  work; every earlier stage kept it isolated. Requires an interactive
  session and explicit `--yes`, matching every other human-only command.

### Changed

- A new sprint now also freezes the branch checked out in the project's
  working directory at creation time, alongside its base commit, so
  finalization later knows where to land the sprint's changes. Creating a
  sprint while the project's working directory has no branch checked out
  (a detached `HEAD`) is no longer allowed.

## v0.56.0

### Added

- A running sprint's `test_work` node can now be resolved by a human
  operator: `forge test-work added --sprint <id> --justification <text>
  --yes` records that new tests were added to protect the scope (or
  `forge test-work no-new-tests` for a justified decision that none were
  needed) and settles the node. Requires an interactive session and
  explicit `--yes`, matching every other human-only command.

## v0.55.0

### Added

- A running sprint's `confirmation` node can now be resolved by a human
  operator: `forge confirm confirmed --sprint <id> --definition-of-done
  <text> --evidence-kind <inspection|execution|existing-check> --evidence
  <text> --yes` records whether the implementation meets its definition of
  done (or `forge confirm not-confirmed` for the opposite verdict) and
  settles the node. A confirmed verdict lets the sprint's test-work node
  become eligible; a not-confirmed verdict blocks the sprint for further
  human attention. Requires an interactive session and explicit `--yes`,
  matching every other human-only command.

## v0.54.0

### Added

- A running sprint's fourth node (`review`) now actually executes: once
  `test_work` finishes, Forge runs the sprint's frozen review provider
  against the diff between the sprint's base and its current integration
  tip, asking it to approve the change or request changes. An approval, or
  the change getting the same review verdict twice in a row, completes the
  review node and (on repeated rejection) blocks the sprint for human
  attention. An ordinary "changes requested" verdict keeps the review node
  open and runs another round on the next tick, rather than counting
  against the sprint's normal per-node retry limit. A provider failure, an
  idle/session timeout, or an unreadable verdict is recorded as a failure
  and automatically retried, matching every other node's retry policy.

## v0.53.0

### Added

- A running sprint's third node (`implementation`) now actually executes:
  once `planning` finishes, Forge runs the sprint's frozen provider inside
  an isolated worktree with the plan and the project's admitted rules and
  knowledge, this time inviting file edits. If the provider actually
  changes anything, Forge commits it (authored as Forge itself) and
  integrates it into the sprint. A provider that reports success but
  changes nothing is recorded as a failure, since a role whose job is
  producing an edit that produces none has not done its job. A provider
  failure, an idle/session timeout, or an isolation failure is recorded
  and automatically retried up to twice, matching every other node's
  retry policy.

## v0.52.0

### Added

- Forge can now commit an attempt's file changes onto its own isolated
  branch, always authored as Forge itself rather than the project's own
  configured git identity (or lack of one). Internal infrastructure only
  in this release — no command or sprint node uses it yet.

## v0.51.0

### Added

- A running sprint's second node (`planning`) now actually executes: once
  `intake` finishes, Forge runs the sprint's frozen provider inside an
  isolated, throwaway worktree with a prompt built from the project's
  admitted rules and knowledge, asking it to research and reason about
  the change without editing any file. Its response is recorded as a
  structured handoff for the (not-yet-built) `implementation` node to
  read. A provider failure, an empty response, an idle/session timeout,
  or an isolation failure is recorded and automatically retried up to
  twice before the node is marked failed, matching every other node's
  retry policy.

## v0.50.1

### Fixed

- Bumped the pinned release-build .NET SDK to 10.0.303 (from 10.0.302, no
  longer available on current CI runner images).

## v0.50.0

### Added

- A new project-scoped `context.token_budget` configuration key controls
  how much of a project's `.forge/` rules and knowledge content the
  `intake` node admits into a sprint's context manifest before truncating.
  Settable via `forge config project set context.token_budget <value>` (a
  positive integer); defaults to `32000` when unset, matching the
  previous fixed behavior. An unreadable or invalid project configuration
  falls back to the same default rather than failing the node.

### Fixed

- Writing an integer project configuration value (only reachable through
  this release's new `context.token_budget` key) could silently fail to
  persist: a pre-existing YAML round-trip step treated every configured
  value as text, so a written number failed re-validation on the very
  next read and was silently discarded in favor of the prior value.
- A hand-edited project manifest (`.forge/manifest.yaml`) containing an
  explicit YAML float tag with a value like `.inf` or `.nan` in any field
  could crash the background service that keeps sprints moving, or in
  rarer cases the whole local Host process, until it was restarted. Such
  a manifest is now rejected as invalid configuration instead.

## v0.49.0

### Changed

- A running sprint's first node (`intake`) now actually executes: Forge
  parses the project's `.forge/` rules and knowledge and freezes the
  sprint's context manifest automatically. Previously this node stayed
  `ready` forever with nothing to advance it, so `forge tree`/`forge sprint
  inspect` and the Desktop sprint views now show real progress on a freshly
  run sprint instead of a permanent stall. A malformed `.forge/` document is
  recorded as a diagnostic on the node result rather than blocking
  progress. Every other node role (planning, implementation, review,
  confirmation, test work, finalization) still has no executor.

## v0.48.0

### Added

- The Desktop app can now create, run, resume, and cancel a sprint
  ("Create sprint", "Run sprint", "Resume sprint", "Cancel sprint"),
  matching `forge sprint create|run|resume|cancel`.

### Changed

- `sprint.manage`'s documented capability no longer lists `rebase`;
  `forge sprint rebase` is not implemented and now has its own separate
  capability entry (`sprint.rebase`) explaining why (no node executor
  exists yet to trigger the git-level recovery it would perform).

## v0.47.0

### Added

- The Desktop app can now preview, install, and remove the AI-provider
  agent integration ("Preview integration", "Install integration",
  "Remove integration"), matching `forge integration skill
  generate|install|remove`.

## v0.46.0

### Added

- The Desktop app can now poll for recent workflow events ("Poll events"),
  matching `forge events`.

## v0.45.0

### Added

- Forge Host now sends a best-effort local notification when a sprint
  reaches `awaiting_human`, `blocked`, `failed`, or `completed`. Each
  event notifies at most once, and content is redacted before delivery.
  Disable with the new `notifications.enabled` configuration key
  (`forge config set notifications.enabled false`).

## v0.44.0

### Security

- `forge gate approve|reject` and `forge attempt supersede` now refuse to
  run unless invoked from an interactive terminal, reporting
  `permission_denied` (exit code 8). This is the first real technical
  control behind their human-only requirement, beyond mandatory
  confirmation alone. Piping the replacement instruction via
  `--instruction-file -` is unaffected; deliberately redirecting the
  command's own output (e.g. `| tee log.txt`) is refused the same way a
  non-interactive invocation is, even for a real human.

## v0.43.0

### Added

- The Desktop app can now supersede a non-terminal attempt with a
  replacement instruction, matching `forge attempt supersede`.
  Confirmation is always required.

## v0.42.0

### Added

- The Desktop app can now approve or reject a gate node awaiting a human
  decision, matching `forge gate approve|reject`. Confirmation is always
  required.

## v0.41.0

### Added

- `forge sprint create` creates a sprint from the project's canonical
  implementation-critical graph.
- `forge sprint run` advances a sprint one legal hop (`draft` to `ready`,
  then `ready` to `running`).
- `forge sprint resume` un-blocks a blocked sprint back to `ready`.
- `forge sprint cancel` cancels a sprint, requiring confirmation (`--yes`,
  or `interaction.confirm_destructive` disabled).

## v0.40.0

### Added

- `forge gate approve`/`forge gate reject` resolves a gate node awaiting a
  human decision.
- `forge attempt supersede <attempt-id> --instruction-file <path|->` cancels
  a non-terminal attempt and creates a linked replacement carrying a
  bounded instruction, reading the instruction from a file or standard
  input. Both commands require explicit confirmation (`--yes`).

## v0.39.0

### Added

- A rate-limited attempt is now durably deferred instead of retried
  immediately: it releases its slot, records a resume time, and leaves its
  node ready but blocked from starting again until that time passes, without
  bypassing the sprint's shared retry budget.
- An operator can now supersede a non-terminal attempt with a confirmed,
  versioned, idempotent command carrying a bounded replacement instruction.
  The superseded attempt is cancelled, linked to a freshly created
  replacement attempt on the same node, and the node is re-armed to start
  the replacement.

## v0.38.0

### Added

- Provider attempts now support two frozen deadlines -- an absolute
  session limit and a sliding idle limit that resets on any activity --
  with a distinct, durable outcome for an idle timeout versus a session
  timeout versus an ordinary provider failure.
- Process-tree cleanup on cancellation or a deadline is now verified to
  terminate an entire multi-generation process tree (not just the directly
  spawned child) on Windows, Linux, and macOS.

## v0.37.0

### Changed

- Provider execution now sends the prompt over standard input instead of a
  command-line argument, runs each provider child in a minimal Forge-owned
  environment instead of the host's own, and reads provider output as a
  bounded, concurrently-consumed stream instead of buffering it fully after
  the process exits. A run now requires exactly one valid terminal result:
  a process that exits cleanly without emitting one, or emits more than
  one, is reported as a failure instead of a silent success.

### Fixed

- The Codex adapter no longer misclassifies its `turn.started` progress
  marker as a completed result.

## v0.36.0

### Added

- Review can now record a design or implementation verdict per iteration,
  with an independent counter per dimension and a rising severity floor
  (all findings on iteration 1, then progressively only medium, high, and
  finally critical-only). Findings below the current floor are still
  recorded, just not left open. Two consecutive identical external finding
  sets, or reaching the iteration budget, now blocks the sprint for an
  explicit operator decision; choosing to continue pins the floor at
  critical for the rest of that dimension's review.

## v0.35.0

### Added

- Every sprint now freezes a planning, implementation, and review execution
  profile at creation -- provider, model, effort, sandbox and permission
  policy, capability allowlist, and deadlines. Review prefers a provider
  different from implementation's when more than one is enabled, recording
  whether that separation was achieved; a single enabled provider still
  completes review.

## v0.34.0

### Added

- Every managed project's sprint now gets Forge's canonical
  `implementation-critical` graph by default -- intake, planning,
  implementation, confirmation, test-work, review, human approval, and
  finalization as separate, isolated nodes.
- A confirmation node can now record a judgment against its definition of
  done, with evidence from inspection, execution, or existing checks. A
  test-work node can never become eligible to run until its confirmation
  dependency has recorded a confirmed judgment; an attempt to start it early
  is rejected, and a not-confirmed judgment blocks the sprint until an
  operator explicitly resumes it.

## v0.33.0

### Added

- Forge can now build a reproducible context manifest for a sprint from its
  always-on rules, accepted ADRs, and project knowledge -- ordered
  deterministically, budgeted by token count, with dropped items recorded
  rather than silently discarded. A knowledge document can now declare an
  optional `status` (`accepted`, `proposed`, `rejected`, `superseded`); only
  accepted or unstatused documents are admitted to the manifest.
- Forge can now validate and run a bounded, read-only, declarative
  context-query plan against one pinned Git commit -- reading a file's exact
  content or searching for a pattern -- and return a reproducible result
  bundle. A plan can never widen its own read access: every operation is
  checked against an explicit capability allowlist before anything runs, and
  one disallowed or malformed operation rejects the whole plan.

## v0.32.0

### Added

- `forge integration skill generate|install|remove` manages the generated
  Claude Code (`CLAUDE.md`) and Codex (`AGENTS.md`) integration files.
  `generate` previews what would be written for every enabled provider
  without touching disk, reporting whether each target file is missing,
  up to date, would change, or already exists as a file Forge did not
  create. `install` writes it (confirmation required); `remove` deletes
  it. Neither ever overwrites or deletes a file that isn't recognizably
  Forge's own -- an unrelated or hand-written file at the same path is
  left untouched and reported instead.

## v0.31.0

### Added

- Forge can now generate the native Claude Code (`CLAUDE.md`) and Codex
  (`AGENTS.md`) integration files from a project's compiled rules and
  knowledge documents. Both files carry a source-digest ownership marker so a
  later step can tell whether the canonical `.forge/` content has changed
  since generation, or whether an installed copy has drifted from what Forge
  generated. Generation is refused (never silently degraded) when the
  project's agent-facing language isn't one Forge's catalog supports. Nothing
  installs these files into a project yet -- generation only.

## v0.30.0

### Added

- Forge can now parse authored `.forge/rules/*.md` and `.forge/knowledge/*.md`
  documents (Markdown with a YAML frontmatter block) into validated project
  knowledge. Each document declares a stable id, title, scope, optional
  references to other documents, and an optional per-document context-token
  limit; unsafe references (path traversal, absolute paths, symlink escapes,
  or a target outside the parsed document set) and oversized documents are
  reported individually rather than blocking the rest of the project's rules
  and knowledge. Nothing consumes this yet — it lands ahead of the provider
  integration generator and context assembly that will read it.

## v0.29.1

### Changed

- Internal: closed the Stage 8 implementation plan item for the Host's
  `resume_not_before` resume scheduler. The timer, activity-update, and
  snapshot-projection surfaces buildable without an attempt-execution
  engine already existed; resuming a deferred attempt requires starting
  one, which is Stage 11's attempt-execution engine, so that remaining
  semantics are formally re-scoped there. No shipped behavior, contract,
  or configuration changes.

## v0.29.0

### Added

- The Desktop surface now shows per-sprint detail instead of only a
  general project overview. A sprint tree nests each attempt under its
  owning node, and a detail section lists that sprint's nodes, attempts,
  findings, and routing state. Both are local projections of the same
  project snapshot the CLI reads, and render the exact lines `forge
  tree` and `forge sprint inspect` render.
- A sprint-id box selects which sprint to expand. Empty expands the
  active sprint; a value that is not a known sprint id is reported as
  `sprint_not_found` under Diagnostics rather than quietly showing the
  active sprint's detail in its place.

### Changed

- The sprint tree and sprint detail rendering moved into the shared
  surface formatter, and an acceptance test now asserts that the CLI and
  Desktop render identical text for one project, so the two projections
  cannot drift apart. CLI output is unchanged.
- Every Desktop text box now carries a screen-reader name and a visible
  placeholder — project root, sprint id, and both configuration boxes.
  None of them was labeled before.

## v0.28.1

### Fixed

- Forge's project lease and provider-install lock now work for standard
  (non-administrator) Windows users. Both previously always constructed
  their named mutex in the OS-wide `Global\` namespace, which Windows
  only lets administrators and service accounts create — a non-admin
  user's Host process failed outright the moment it tried to acquire the
  project lease at startup. Windows now uses session-scoping instead,
  which every account can create; non-Windows platforms keep the
  stronger `Global\`-equivalent guarantee unchanged, since they never had
  this privilege problem to begin with. (Two intermediate designs were
  tried and rejected: a per-process capability check that decided the
  namespace from the process's elevation token rather than the account,
  so the same admin user's elevated and non-elevated Forge processes
  could silently stop excluding each other; and uniform session-scoping
  everywhere, which unnecessarily weakened the guarantee on platforms
  that never needed it.) A new CI check (added while adding same-user
  isolation coverage for the lease) caught the original failure by
  exercising the real primitive as a genuine non-admin local Windows
  account. Known trade-off: Windows session-scoping does not extend
  across two concurrent sessions of the same user (e.g. console + a
  simultaneous RDP session).

### Security

- Added CI coverage proving a *different* local OS user cannot acquire
  the project lease (`MutexProjectLease`, `NamedWaitHandleOptions
  .CurrentUserOnly`) of the identical name the current user already
  holds — access is denied, the same isolation guarantee already
  covered for the control-plane pipe. This closes the last gap the
  2026-08-15 audit found in same-user isolation coverage.

## v0.28.0

### Added

- `forge tree` shows the project's sprint hierarchy with each sprint's
  attempts nested under their owning node, instead of the flat separate
  lists `forge status` prints.
- `forge sprint inspect <id>` is a dedicated entry point for one sprint's
  full node/attempt/finding/routing detail.

### Fixed

- An attempt's last-activity heartbeat now survives into the project
  snapshot's machine contract (`last_activity_at`, separate from
  `updated_at`) instead of being dropped during projection. Visible in
  `--json` output from `forge status --detail full` and the new
  `tree`/`sprint inspect` commands; no text renderer prints it yet.

## v0.27.0

### Added

- Forge Host now installs, repairs, or updates an enabled provider automatically
  at startup instead of only reporting that it needs attention, matching what
  `forge models --refresh` already did explicitly. Routine startup respects the
  existing 24-hour/1-hour release-check cache windows; `--refresh` still bypasses
  them to force a fresh check.

## v0.26.0

### Added

- A sprint now freezes its ordered list of usable providers at creation time
  from the currently enabled providers, instead of leaving that resolved only
  implicitly. Creating a sprint with no enabled, registered provider now fails
  immediately with a clear diagnostic instead of creating a sprint that could
  never make progress.

## v0.25.1

### Fixed

- Reading a sprint's journal now retries briefly on a transient file-sharing
  conflict (e.g. a virus scanner or search indexer momentarily holding the
  file right after it's written) instead of failing the read immediately.

## v0.25.0

### Added

- Forge Host now periodically re-derives sprint node readiness in the
  background, so a node whose dependency settled while nothing else was
  actively working the sprint no longer stays stuck until some unrelated
  call happens to touch it again.

## v0.24.0

### Changed

- The CLI and Desktop now independently validate every Host handshake response
  (the echoed correlation id and protocol version), rather than trusting a
  well-formed-looking response at face value. A mismatched or incompatible
  response is now rejected as a protocol error instead of silently accepted.
- The Host's handshake response now advertises its real supported capability
  set instead of always reporting none.

## v0.23.1

### Changed

- Internal: added an automated CI check proving the Host's control-plane pipe
  (`PipeOptions.CurrentUserOnly`) actually blocks a connection attempt from a
  different local OS user, not just a different instance id. No shipped
  behavior, contract, or configuration change.

## v0.23.0

### Changed

- Desktop now also routes startup recovery and project-scope configuration
  changes through the project's Host, matching the CLI (ADR 0005): the two
  clients no longer mutate `.forge/` independently, closing the window for a
  concurrent CLI/Desktop write to race. User-scope configuration is
  unaffected.

## v0.22.0

### Changed

- The Forge Host is now the sole writer of a project's `.forge/` state for
  startup recovery (`forge doctor --recover`) and project-scope configuration
  (`forge config project`): the CLI routes both through the project's Host
  over the control-plane protocol instead of mutating `.forge/` in its own
  process, starting the Host first if none is already running (ADR 0005).
  User-scope configuration (`forge config user`) and first-time project
  initialization (`forge init`) are unaffected — a Host cannot exist before a
  project has an id to key its lease on.

## v0.21.2

### Changed

- Internal: corrected the Stage 8 implementation plan to reflect actual
  progress after an architecture audit found several sub-items marked
  complete before their acceptance criteria were met (Host mutation
  ownership, automatic provider startup maintenance, deferred-attempt
  wake-up, snapshot/CLI/Desktop parity, and provider-constraint freezing).
  No shipped behavior, contract, or configuration changes.

## v0.21.1

### Changed

- Forge's own test suite now follows the implementation-first, risk-based test
  strategy: duplicate, tautological, and implementation-coupled tests were
  removed and near-identical cases were merged into data-driven theories. No
  shipped behavior, contract, or configuration changes; every risk the removed
  tests touched stays covered by a stronger existing test, and the Windows
  bundle publisher's reproducibility contract is now asserted explicitly.

## v0.21.0

### Added

- `forge models`, the project snapshot, and the Desktop overview now list every
  registered provider, including one the user's `providers.enabled` selection
  disables — shown read-only, without probing it, alongside its enabled
  siblings' version, update-availability, and authentication state.

### Changed

- `forge models --json` now returns the versioned `provider-health` envelope
  (`schema_version` plus a `providers` array) instead of a bare array missing
  the `registered`/`enabled` fields the schema requires. Its body no longer
  includes the aggregate `ready`/`diagnostic_code`/`shared_diagnostic_code`
  fields the old, non-conformant shape happened to leak — that readiness
  summary is still reported the same way it always has been, through the
  command's exit code and diagnostic-stream output, never the JSON body.
- The provider-health contract moves to `1.1.0` and the project snapshot
  contract (which embeds the same provider entries) moves to `1.2.0`: a
  provider entry's `state` can now be `null` for a disabled provider.

## v0.20.0

### Added

- Forge now checks whether each enabled provider (Codex, Claude Code) is
  authenticated at every startup and before model work, instead of only
  checking whether it is installed. A provider that is not logged in blocks
  sprint work with a clear diagnostic instead of failing partway through an
  attempt; Forge never initiates sign-in itself.
- Forge now checks for provider updates automatically, at most once every 24
  hours (or once an hour after a failed check), and only actually applies an
  update through `forge models --refresh` or when repairing a missing/broken
  install — routine use no longer re-checks or reinstalls a provider that is
  already current. `--refresh` always checks for the latest version instead
  of waiting out the cache, but still only updates when one is actually
  available.
- Concurrent Forge processes no longer race to install or update the same
  provider at the same time.
- Claude Code's own background auto-updater is now disabled while Forge runs
  it normally, so Claude Code never updates itself outside of Forge's own
  update check.

## v0.19.1

### Changed

- Forge's development policy and planned managed-project workflow now require
  each scoped implementation to be completed and confirmed against its
  definition of done or user expectations before selecting and authoring the
  smallest risk-based set of new tests. Fixes still require a regression test
  proven against the prior defect.

## v0.19.0

### Added

- The `providers.enabled` user setting now actually controls which providers
  are probed, installed, or updated — previously it was accepted and stored
  but had no effect. Omitting it still enables every built-in provider;
  setting it to an empty list disables all of them (blocking model work
  until at least one is enabled); listing specific providers enables only
  those, in the given order, and disabled providers are never touched.
- Setting `providers.enabled` to an id Forge does not recognize is now
  rejected with the same diagnostic as other invalid configuration, instead
  of being silently accepted.

## v0.18.0

### Changed

- Continued the ADR 0007/0008 portability migration: the Codex and Claude
  Code integrations and the Forge Host now live behind clearly separated
  Windows adapters, and the Host's process/lease/protocol behavior is
  verified on Windows, Linux, and macOS in CI (previously Windows-only).
  `forge models`, `forge doctor`, and the shipped `Forge.Host.exe` behave
  the same as before.

## v0.17.0

### Added

- User configuration accepts an ordered `providers.enabled` list, ahead of
  the upcoming explicit provider registration/enablement work: omitting it
  keeps today's behavior (every registered provider runs), and an empty list
  is preserved distinctly (blocks model work) rather than being treated the
  same as "not configured."
- The project snapshot now always reports `startup_checks` (the same checks
  `forge doctor --startup` prints) and `providers` (each provider's
  registered/enabled/state/version), so a client no longer needs a separate
  call to see them.
- Added the versioned `provider-health`, `startup-check`, `diagnostic-bundle`,
  and `execution-profile` JSON schemas (`docs/contracts/v1/schemas/`), ahead
  of the stages that will populate the latter two (provider update/
  authentication detection and `forge doctor --bundle` collection; frozen
  sprint execution profiles).
- Reserved the `provider_authentication_required` and
  `provider_authentication_check_failed` diagnostic codes for the upcoming
  provider authentication check.

### Fixed

- Configuration files written by a prior Forge version (with no
  `providers` section) now explicitly validate against the current schema
  instead of relying on undocumented tolerance; the contract's stated
  "producers write the latest, consumers accept a compatible older minor"
  policy is enforced for the first time rather than only described.

## v0.16.0

### Added

- The project snapshot's routing status now reports `resume_not_before` when
  a sprint has hit a provider rate limit: the wait is derived fresh from the
  durable routing history on every read, so it survives a Forge Host restart
  exactly (no in-memory timer to lose or double-fire), and the same key keeps
  being preferred once the wait elapses rather than falling back to another
  provider.
- Added a durable, throttle-friendly way for a running attempt to report
  activity (a heartbeat) without persisting any provider content — the
  future basis for resetting an attempt's idle deadline while it works.
- Added `NotificationProjector`, mapping durable sprint events to the four
  attention kinds (`awaiting_human`, `blocked`, `failed`, `completed`)
  Desktop/OS notifications will surface, deduplicated by event id.

## v0.15.0

### Fixed

- Fixed a bug where the project write lease was accidentally scoped per
  instance (release/Debug/test) instead of per project: a Debug build and the
  installed release, or two ephemeral test Hosts, could previously become
  concurrent writers of the same `.forge/` tree. The lease is now shared
  across every instance of one project, exactly as ADR 0005 requires; only
  the IPC pipe name stays instance-scoped.
- User configuration and sprint worktrees are now isolated per instance id
  (release, Debug, and each ephemeral test instance), so they no longer
  collide on one shared `%LOCALAPPDATA%\Forge\config.json`/worktree tree. An
  existing user configuration file from before this change is migrated
  automatically (copied, not moved) into its new instance-scoped location the
  first time an instance starts.
- Forge Host no longer lets an unparseable (non-JSON) request payload escape
  unhandled, including as the very first (handshake) message: the offending
  connection is closed cleanly with a logged diagnostic instead of hanging
  silently.

### Changed

- Sprint worktrees created before this release live under the old, unscoped
  `%LOCALAPPDATA%\Forge\wt\` path and are not automatically migrated or
  cleaned up (unlike user configuration, above). If you ran a sprint before
  upgrading to this version, confirm no attempts are in progress, then remove
  that directory manually and run `git worktree prune` in your project to
  reclaim the old worktree registrations.

### Added

- Host connection deadlines (handshake and idle-request) are now
  configurable, and the control plane has new test coverage for hostile and
  stale clients (garbage payloads, a client that never handshakes, a client
  that disconnects mid-idle) and for recovering a project lease abandoned by
  a Host that crashed rather than shut down cleanly.

## v0.14.0

### Added

- Forge Host now serves the authoritative project read model over the local
  control-plane protocol: `GetProjectSnapshot(detail, sprint_id?)` reports
  every known sprint (creation order, state, base commit), the active sprint
  (only when exactly one is non-terminal), sprints needing attention, and,
  at `full` detail or for an explicitly named sprint, that sprint's
  nodes, attempts, findings, and retry budget. `ReadControlEvents` reads the
  same durable per-sprint journals incrementally through an opaque cursor
  that discovers new sprints and never silently rebaselines on a stale or
  malformed cursor.
- `forge status` gained `--detail <summary|full>` and `--sprint <id>` and now
  prints the sprint list and (when requested) sprint detail alongside the
  existing startup and recommendation output; both the human and `--json`
  output are pure projections of the one snapshot.
- Added `forge events [--after <cursor>] [--follow] [--json]` to read
  incremental workflow events, with bounded short polling in follow mode.

### Fixed

- `forge status --sprint <id>` now reports a diagnostic and a non-zero exit
  code for a malformed or unknown sprint id instead of silently behaving like
  no sprint was requested.
- `forge events` against a project that has never been initialized now
  reports that explicitly instead of looking identical to "caught up, no new
  events."
- A `ReadControlEvents` cursor is never silently treated as "no cursor
  supplied" when the request itself is malformed, and its watermark can no
  longer skip an event under a non-monotonic system clock.
- Forge Host no longer drops a client connection silently when a snapshot or
  events request hits an unreadable journal file; it now reports a safe
  diagnostic and keeps the connection usable.

## v0.13.1

### Changed

- Contributors now get PR feedback and releases significantly faster: pull
  request checks no longer re-run in full on an edited title or description,
  superseded runs cancel instead of queuing, and a release publishes right
  after merge instead of re-validating already-passed checks. No functional
  behavior changed for Forge itself.

## v0.13.0

### Added

- Added the portable Forge Host (`Forge.Host`) and its client SDK
  (`Forge.Host.Client`): a per-user, headless process that will become the
  only workflow writer. The local control-plane protocol is a versioned,
  length-prefixed JSON envelope over one asynchronous `System.IO.Pipes`
  transport (Windows named pipes, Unix-domain sockets on Linux/macOS), gated
  by a version/capability handshake, bounded message size and deadlines, and
  correlation ids. A named `Mutex` project lease, keyed by the project's
  stable id, ensures only one Host owns a project at a time: a second Host
  started against the same project exits instead of mutating or serving it,
  and surfaces an abandoned prior owner instead of hiding it. The client SDK
  covers discovery, starting the Host process, and reconnecting after a
  drop, all with stable diagnostic codes instead of raw exceptions.

### Changed

- Fixed a thread-affinity hazard in the project lease: a named `Mutex`'s
  ownership is tracked per OS thread, which async/await code cannot
  guarantee across an `await`. The lease now runs its acquire/release pair
  on one dedicated thread for its whole held lifetime.

## v0.12.0

### Changed

- Migrated the ADR 0007 cross-platform boundary: the CLI, durability, Desktop, and updater code that reused
  Windows APIs now lives behind three thin, explicitly marked Windows adapters (`Forge.Cli.Windows`,
  `Forge.Runtime.Windows`, `Forge.Updater.Windows`), while the CLI commands, Desktop presentation logic, and
  update policy/orchestration are cross-platform. An automated architecture gate enforces the boundary, and the
  neutral projects now build and test on Windows, Linux, and macOS in CI. The Windows MVP distribution and its
  behavior are unchanged.
- Extended the target workflow with reproducible declarative context-query plans, fresh per-node context and
  capability boundaries, and explicit terminal results. Recorded approval-gated knowledge proposals and bounded
  personal memory as post-MVP candidates while excluding transcript-backed project memory, model-judged
  completion, shadow checkpoints, unrestricted hooks, and silent skill mutation.

## v0.11.0

### Added

- The target MVP architecture now includes a cross-platform, per-user headless
  Forge Host as the only workflow writer. CLI/TUI and Desktop will use a
  versioned local protocol over the cross-platform .NET named-pipe API so provider
  attempts survive client restarts. A current-user .NET named mutex prevents
  competing writers. Forge needs no Host OS adapters; the Windows MVP remains
  unchanged.
- Added a planned shared project snapshot and cursor-based durable event read-back,
  an attention-oriented accessible dashboard, contextual human-gate decisions,
  canonical Claude Code/Codex skill generation, allowlisted diagnostic bundles,
  and isolated release/development/test instances.
- Added target supervised provider execution with stdin-only prompts, minimal
  child environments, bounded streaming, session/idle watchdogs, whole-process-
  tree cleanup, durable rate-limit resumption, human attempt supersession,
  three execution profiles, local notifications, and independently
  eligible reviewers.
- Added bounded review convergence based on Agentic Software Development:
  independent phase counters, fresh reviewer contexts, coverage ledgers, rising
  severity floors, repeated external-finding escalation, and a human decision at
  the iteration limit. Git/diff stalemate heuristics are explicitly excluded.

### Changed

- Simplified the target and implemented architecture around one project snapshot,
  one sprint journal, one three-profile review engine, and exact Git/file/`rg`
  context retrieval. Removed runtime sprint registration from project manifests
  and separate routing persistence; legacy routing sidecars migrate
  idempotently, including reconciliation of the old retry-budget snapshot after
  interrupted writes. The pre-1.0 query names and state-machine edges replaced by
  this architecture are removed without deprecated aliases; persisted routing
  data and the old manifest sprint registry still migrate safely without being
  exposed as current contracts. Semantic indexes are deferred until exact
  retrieval shows a measured gap.
- Established a repository-wide cross-platform code rule: only marked, minimal
  leaf OS adapters may use platform APIs. Added a Stage 8 migration and automated
  architecture gate for existing CLI, durability, Desktop, updater, and test
  coupling while keeping the MVP distribution Windows-only.
- Inserted the host/control-plane work as Stage 8 and resequenced the compiler,
  memory, implementation workflow, hardening, and release stages. Cookbook and
  terminal-emulator features remain outside the MVP.
- Revised the target provider invocation contract from prompt arguments and
  buffered output to redirected stdin and supervised incremental streams before
  real workflow execution is enabled.

### Fixed

- Sprint journals now reject transition records missing `to_state` instead of
  folding stale state, and finding recovery resumes safely after interruption at
  every append boundary.

## v0.10.0

### Added

- Sprints now isolate every write attempt in its own Git worktree, separate
  from a sprint's integration worktree and from the user's own project
  checkout, so nothing an attempt does is ever visible until it is
  successfully integrated. Integration only ever fast-forwards, checked
  against the integration branch's actual current state immediately before
  merging; a stale attempt is recovered only through an explicit rebase, and
  a rebase that conflicts is aborted and reported rather than left
  half-finished. A failed or abandoned attempt's worktree and branch are
  always discarded outright, so retrying always starts clean.
- Fallback between providers now goes through per-provider/model/surface
  circuit breakers with a cooldown, and a retry budget shared by an entire
  sprint, so a flapping or failing provider cannot be retried without bound.
  Every routing decision is recorded durably. Authentication and policy
  failures are never treated as retryable.

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
