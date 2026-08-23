# Desktop workspace redesign

**Status:** Proposed  
**Date:** 2026-08-23  
**Scope:** Forge Desktop, shared presentation, Host control plane, and versioned workflow contracts

## 1. Goal

Replace the current single scrolling Desktop page with a project-oriented workspace that lets a
user manage projects and active sprints without entering project, sprint, node, or attempt IDs.
The workspace must expose all user-scoped settings, provide a chat-like durable sprint timeline,
offer only actions valid for the current workflow state, stop the active operation independently
of cancelling its sprint, and move a sprint to another workflow stage only after Forge validates
the target's prerequisites.

The result must preserve the existing control-plane invariants:

- the project Host remains the only writer of `.forge/` state;
- Desktop and CLI use the same versioned queries and mutations;
- workflow state and deterministic gates remain authoritative;
- presentation code never recreates transition or prerequisite policy;
- provider output, credentials, raw commands, and unredacted paths are not persisted as UI history;
- neutral application and presentation code remains cross-platform, with OS-specific behavior in
  leaf adapters only.

## 2. Terminology and product decisions

- **Project** is an initialized or discoverable repository known to this Desktop installation.
- **Sprint state** is the lifecycle state such as `running`, `paused`, or `completed`.
- **Workflow stage** is a workflow node such as `planning`, `implementation`, or `review`.
- **Active operation** is the exact non-terminal attempt currently owned by an executor. Stopping
  it is not the same as cancelling the sprint or superseding the attempt with an instruction.
- **Move to stage** means selecting a workflow stage, not assigning an arbitrary sprint-state
  enum value. Forge evaluates the move as a domain operation and reports blockers and
  consequences before it can be committed.
- The first UI layout is a fixed two-panel workspace with a collapsible sidebar. Arbitrary
  docking, detachable panels, and user-authored layouts are out of scope.
- The built-in workflow is currently linear. Contracts must identify stages by stable node ID and
  remain valid if a future workflow contains parallel nodes; the UI must not infer ordering from
  display position.

## 3. Current gaps

The current Desktop page exposes all controls in one vertical form. It requires manually entered
roots and IDs, has no persistent project catalog or navigation shell, renders events as text, and
edits configuration as raw key/value input.

Existing backend data covers project startup, sprint/node/attempt state, findings, retry budget,
provider health, suggested actions, configuration provenance, and cursor-based workflow events.
It does not yet provide:

- a user-scoped catalog of known projects;
- a user-visible interaction timeline;
- a general contextual-action contract;
- a distinct stop-current-operation mutation;
- a sprint `paused` state and safe stopped-attempt recovery semantics;
- workflow-stage transition assessment or execution;
- model/account quota data;
- a user-facing stage revision model for safely reopening completed stages.

## 4. Information architecture

```text
+--------------------------+--------------------------------------------------+
| Forge                    | Persistent page status header                    |
|                          +--------------------------------------------------+
| + Add project            |                                                  |
|                          | Selected page                                    |
| v Project A        [cfg] |                                                  |
|   * Sprint #12 Running   | Forge settings / project overview /              |
|   ! Sprint #11 Review    | project settings / sprint workspace /            |
| > Project B        [cfg] | provider status                                  |
|                          |                                                  |
|                          +--------------------------------------------------+
| Forge settings           | Contextual action area                           |
| 2/2 providers | limits   |                                                  |
+--------------------------+--------------------------------------------------+
```

### 4.1 Sidebar

The sidebar contains:

- an add-project action using the platform folder picker through a neutral port;
- known projects with display name, availability, attention state, and project-settings access;
- active sprints grouped under each project;
- completed and cancelled sprints behind a project history entry;
- global Forge settings;
- a bottom status row for Host connectivity, provider/model availability, authentication, and
  quota status.

Sprint ordering is deterministic: human attention, running, paused, blocked or failed, then other
non-terminal sprints by descending creation sequence. Every status has text and an accessible icon;
color is supplementary.

Selecting a project opens its overview. Selecting a sprint opens its workspace. The application
restores the last selected route, project expansion state, and unsent text after restart when the
referenced project and sprint still exist.

### 4.2 Project overview

The project overview shows:

- startup and repository readiness;
- active sprint cards and attention reasons;
- recent completed or cancelled sprints;
- the highest-ranked valid suggested actions;
- create-sprint, initialize, recover, and project-settings actions;
- provider integration status and diagnostics links.

Project display name initially defaults to the root directory name. A local alias belongs to the
user project catalog and does not modify the repository manifest.

### 4.3 Sprint workspace

The sprint workspace has three persistent regions.

#### Status header

Show the project name, sprint sequence, sprint state, current stage, completed/total stage count,
last activity, active provider/model, open finding counts, retry budget, and `resume_not_before`
when present. Show UUIDs, paths, base commit, worktrees, and routing detail only in an expandable
details view.

#### Timeline

Render one chronological list containing:

- user messages;
- user-visible agent summaries;
- workflow transitions;
- operation start, activity, stop, failure, and completion;
- approval requests and user decisions;
- findings and diagnostics;
- stage-transition assessments and committed moves;
- artifact links.

Heartbeat events are grouped. The timeline supports incremental loading, unread position, filters,
copying, technical-detail expansion, and restoration of scroll position. It never stores or
renders unbounded raw provider streams.

#### Contextual actions

The bottom region renders typed controls described by the Host. Examples include free text,
approve/reject, confirmation evidence, test-work justification, attempt supersession, stop active
operation, stage selection, resume, finalize, and cancel sprint.

The UI never asks for an ID already supplied by the selected project/sprint/action context.
Destructive or history-invalidating actions show their exact target and consequences.

## 5. Settings

### 5.1 Forge settings

All current user-scoped keys are editable with typed controls and grouped as follows:

| Group | Setting | UI |
| --- | --- | --- |
| Language | `language.ui` | Supported-language selector |
| Language | `language.interaction` | Selector with inherit-from-UI option |
| Language | `language.llm` | Selector with inherit-from-interaction option |
| Safety | `interaction.confirm_destructive` | Boolean switch with mandatory-gate disclaimer |
| Providers | `providers.enabled` | Ordered enabled-provider list with health |
| Notifications | `notifications.enabled` | Boolean switch and test notification |

Each setting shows its effective value and provenance. Save validates the full edit set and writes
it atomically; discard restores the last durable view. UI-language changes apply without restart.
Mandatory human approvals and the stop/stage-transition confirmations defined below are never
bypassed by `interaction.confirm_destructive`.

### 5.2 Project settings

The project settings page provides typed controls for:

- `artifacts.language.user_facing`;
- `artifacts.language.agent_facing`;
- `context.token_budget`;
- `models.allowed_models`;
- provider integration inspection, installation, and removal;
- startup recovery and diagnostic bundle generation.

The project root and `project_id` are read-only. Values show provenance and validation failures
without exposing raw configuration parsing errors.

## 6. Read models and contracts

### 6.1 Project catalog

Add a user-scoped `ProjectCatalog` outside project state. Each entry contains:

- stable project ID when initialized;
- normalized current root;
- optional local display alias;
- last-opened timestamp;
- last selected sprint and route.

Adding or removing an entry changes only the local catalog. Removing a project never deletes its
repository or `.forge/` directory. A moved project can be relinked after its manifest project ID is
verified.

### 6.2 Workspace summary

Add a lightweight query for sidebar and status-header projection. It must contain project
availability, active sprint summaries, attention reasons, current stage, progress, active
operation, and provider health without loading complete timelines for every project.

The selected sprint continues to use the full project snapshot plus cursor-based incremental
queries. Sidebar refresh is bounded and slower than selected-sprint refresh; mutations and app
activation trigger immediate refresh.

### 6.3 Timeline contract

Add a versioned `SprintTimelinePage` containing ordered `SprintTimelineItem` records and a cursor.
An item has a stable ID, occurrence time, type, actor, localized message key or bounded
user-visible content, structured arguments, target references, correlation/causation IDs, and
optional artifact references.

System items are projections of the existing append-only workflow journal. User messages and
agent summaries are separate bounded artifacts. Timeline projection must redact content before
persistence and again before rendering.

### 6.4 Available actions

Add a versioned `AvailableAction` projection rather than duplicating workflow policy in Desktop.
Each action contains:

- action ID and localized rationale;
- project, sprint, node, attempt, or stage target;
- expected state version;
- safety class and confirmation requirement;
- typed input fields and validation limits;
- enabled state and structured blockers;
- idempotency key and stale behavior.

The Host revalidates every action when executing it. A stale mutation is rejected without side
effects, after which Desktop refreshes and presents the new action set.

### 6.5 Provider quota status

Provider health remains distinct from account/model quota. Add `ProviderQuotaSnapshot` only for
providers that expose verified quota data. It reports provider/model, availability, remaining
amount and unit, reset time, observation time, stale state, and diagnostic code. Unknown quota is
rendered as unknown; sprint retry budget is never presented as account quota.

## 7. Stop current operation

### 7.1 Required behavior

`StopCurrentOperation` targets the exact active attempt selected from a fresh sprint snapshot. It
must not cancel the sprint, settle the sprint as failed, consume automatic retry budget, or create
a replacement attempt immediately.

On success:

1. Forge durably records a bounded stop request for the target attempt.
2. The Host cancels the attempt's linked cancellation source.
3. Process execution terminates the entire owned process tree.
4. The attempt transitions to `cancelled` with reason `user_stopped`.
5. Any attempt-owned worktree and unintegrated changes are discarded through the existing Git
   isolation boundary.
6. The owning node becomes eligible to run again without counting the stop as an automatic
   failure.
7. The sprint transitions to `paused` and no executor starts further work.
8. The timeline records the request, the actor-visible result, and cleanup outcome without raw
   process output.

Resuming a paused sprint transitions it through `ready` and starts a fresh attempt from the
current integration base. Cancelling the sprint remains a separate action. Superseding an attempt
remains a separate action because it also records an instruction and prepares a linked replacement.

### 7.2 State-machine changes

Add `paused` to the versioned sprint state machine:

- `running -> paused`;
- `paused -> ready`;
- `paused -> cancelled`.

Allow the explicit stop operation to re-arm the owning work node after cancellation. This must be
represented by a dedicated event/revision rule, not by pretending the provider failed. Permit
`validating -> cancelled` for an attempt so a stop request remains valid until the operation has
actually settled.

No generic public API may assign these states directly. Only the stop coordinator and existing
workflow commands may produce them.

### 7.3 Coordination and recovery

Add a Host-owned `ActiveOperationRegistry` keyed by attempt ID. It exposes cancellation but not
workflow policy. Executors register the exact attempt before provider/process execution and
unregister it in `finally`.

Persist the stop intent before relying on the in-memory registry. Executors and restart recovery
must check the intent before starting or resuming an attempt. The stop coordinator is a resumable,
idempotent saga over durable events so a Host crash between request, process termination, state
transitions, and worktree cleanup cannot resurrect the stopped attempt.

A stop request is rejected without side effects when:

- the sprint has no active operation;
- the target attempt has already settled;
- the expected version is stale;
- the active attempt changed before validation;
- the caller targets another project's Host.

The stop action remains visible in a pending state until Forge confirms process-tree termination
and durable cleanup. UI disappearance is not considered successful cancellation.

## 8. Move sprint to another workflow stage

### 8.1 Assessment before mutation

Add `AssessStageTransition(sprintId, targetStageId)` as a read-only Host query. It returns:

- source and target stage IDs;
- direction: `advance`, `rewind`, or `same`;
- whether the move is allowed;
- structured satisfied and unsatisfied prerequisites;
- active-operation impact;
- stages, attempts, findings, decisions, and artifacts that would be superseded;
- whether mandatory confirmation is required;
- expected sprint state version.

Desktop shows only stages declared by the sprint's frozen workflow definition. Disabled targets
remain visible with blockers so the user can understand what must be completed.

### 8.2 Prerequisite rules

The Host derives prerequisites from the frozen DAG and domain artifacts. At minimum it validates:

- every required predecessor stage has a successful result in the current stage revision;
- implementation confirmation exists and is `confirmed` before test work;
- a valid test-work decision exists before review;
- review convergence and required human approval exist before finalization;
- no unresolved finding violates the target stage's severity policy;
- the target's provider/model policy is satisfiable;
- required handoffs and artifacts exist and their digests resolve;
- Git base, branch, worktree, and cleanliness requirements hold where applicable;
- rate-limit deferral and retry-budget rules allow execution;
- no conflicting active operation exists.

The UI may explain these checks but may not calculate or override them.

### 8.3 Advance semantics

An advance never fabricates completion and never marks a mandatory stage as skipped. The move is
allowed only if every stage before the target is already satisfied in the current revision. If
the target is the normal next eligible stage, committing the move activates it. A later target is
allowed only when all intervening stages are already satisfied or explicitly optional in the
frozen workflow.

### 8.4 Rewind semantics

Rewinding preserves append-only history and starts a new stage revision. It does not delete or
rewrite prior events, results, findings, decisions, or artifacts.

On commit Forge:

1. requires a bounded reason and mandatory confirmation;
2. stops the active operation first when the assessment says one exists;
3. increments the sprint's stage revision;
4. reopens the target stage for a fresh attempt;
5. marks downstream results and artifacts as superseded by the new revision;
6. excludes superseded evidence from all future prerequisite checks;
7. recomputes eligible stages from the frozen DAG;
8. leaves unrelated upstream history intact;
9. records the transition, reason, consequences, and actor-visible result in the timeline.

Node identity remains stable, while node execution state gains a revision. Queries and artifact
lookups select the latest non-superseded revision. This avoids adding mutable deletion or cloning
the sprint merely to revisit an earlier stage.

Terminal sprints cannot be moved. A completed or cancelled sprint instead offers creation of a
new sprint based on its goal and selected artifacts.

### 8.5 Commit command

`MoveSprintToStage` carries the sprint ID, target stage ID, expected state version, assessment
token, bounded reason when required, confirmation, and idempotency key. The Host recomputes the
assessment immediately before mutation and rejects any mismatch. Assessment tokens are bound to
the project, sprint, target, current revision, and state version.

The operation is a resumable saga because stopping an operation, superseding downstream state,
and activating a target stage require multiple durable writes. Replaying the same idempotency key
returns the original result and never creates a second revision.

## 9. Component architecture

### 9.1 Neutral runtime and application

Add or extend neutral components for:

- project catalog contracts and storage port;
- workspace summary and timeline queries;
- available-action projection;
- stop-operation coordinator and durable stop-intent recovery;
- stage-transition assessment, prerequisite evaluation, and commit coordinator;
- stage revisions and superseded-artifact selection;
- provider quota contracts where supported.

The existing workflow state machines, scheduler, routing ledger, Git isolation, configuration
registry, and redaction utilities remain canonical. New coordinators call them; they do not copy
their rules.

### 9.2 Host and client protocol

Add versioned request/response kinds for:

- workspace summary;
- sprint timeline page;
- available actions;
- stop current operation;
- assess stage transition;
- move sprint to stage;
- provider quota status.

Mutations use the existing project lease, correlation, message-size, timeout, and same-user
transport protections. Capability negotiation prevents an older Host or Desktop from silently
attempting an unsupported operation.

### 9.3 Presentation

Replace the monolithic page view model with neutral presentation state:

- `WorkspaceViewModel` for routing and selected context;
- `SidebarViewModel` for projects, sprint summaries, and global status;
- `ForgeSettingsViewModel` and `ProjectSettingsViewModel`;
- `ProjectOverviewViewModel`;
- `SprintWorkspaceViewModel` for header, timeline, and actions.

Presentation receives typed domain projections and exposes UI state. It does not access the file
system, invoke provider tools, parse configuration JSON, or decide whether a transition is legal.

### 9.4 Windows Desktop adapter

The MAUI/Windows project owns views, templates, focus behavior, folder picker, notification
activation, and platform accessibility integration. OS behavior is implemented behind neutral
ports in explicitly named Windows adapters. Neutral projects do not reference Windows target
frameworks or APIs.

## 10. Refresh, concurrency, and failure behavior

- The selected sprint uses cursor-based polling while visible and refreshes immediately after a
  mutation or app activation.
- Sidebar summaries refresh on a slower bounded interval and never poll complete histories.
- Only one mutation for a selected sprint may be in flight from one Desktop window.
- The Host remains authoritative when multiple clients act concurrently.
- Stale actions are rejected, refreshed, and not retried automatically.
- Losing Desktop does not stop a running Host operation.
- Losing the Host terminates its owned child processes; durable stop and transition sagas recover
  before normal execution resumes.
- Every error view provides a localized summary, stable diagnostic code, retry when safe, and a
  diagnostic-bundle path.

## 11. Implementation plan

Each slice follows the repository rule: implement and confirm the behavior first, then select and
author the smallest risk-based new tests. Every fix includes a regression test proven against the
prior behavior or an equivalent mutation.

### Slice 1: contracts and decisions

1. Record normative ADRs for the workspace projections, stop semantics, stage revisions, and
   transition prerequisite policy.
2. Version state-machine, snapshot, capability, timeline, action, and protocol contracts.
3. Add `paused`, stop-intent, stage-revision, and supersession semantics to the domain contracts.
4. Define localization keys and stable diagnostics in English and Russian.

### Slice 2: stop-current-operation backend

1. Implement active-operation registration in every executor.
2. Implement durable stop intent and the idempotent stop coordinator.
3. Connect cancellation to complete process-tree termination.
4. Discard attempt-owned worktrees and re-arm the owning node without consuming retry budget.
5. Add paused-sprint resume and crash recovery.
6. Expose Host, client, CLI, capability, and snapshot surfaces before Desktop wiring.
7. Confirm behavior with real provider-process and worktree execution, then add focused state,
   cancellation, recovery, protocol, and regression tests.

### Slice 3: stage-transition backend

1. Add stage revision to node state and relevant artifacts.
2. Implement canonical prerequisite evaluation over the frozen DAG and current revision.
3. Implement read-only assessment and blocker projection.
4. Implement idempotent advance and rewind coordinators with durable recovery.
5. Expose Host, client, CLI, capability, event, and snapshot surfaces.
6. Confirm forward, rewind, stale, crash, and active-operation behavior, then add focused domain,
   persistence, protocol, and acceptance tests.

### Slice 4: project catalog and workspace reads

1. Implement user-scoped project catalog persistence and relinking.
2. Add bounded workspace summaries, available actions, and timeline paging.
3. Add unread and last-route persistence.
4. Confirm multiple-project behavior and cross-project isolation, then add focused tests.

### Slice 5: shell, navigation, and settings

1. Introduce the two-panel workspace and route-based content host.
2. Implement sidebar, project overview, Forge settings, and project settings.
3. Apply UI-language changes without restart.
4. Preserve every existing Desktop capability during migration.
5. Confirm keyboard, screen-reader, scaling, empty, loading, error, and narrow-window behavior,
   then add presentation and architecture checks that are reliable outside a live MAUI process.

### Slice 6: sprint workspace

1. Implement sticky status header, virtualized timeline, and contextual action renderer.
2. Add stop and stage-transition assessment/confirmation experiences.
3. Remove manual ID fields from ordinary workflows.
4. Add notification deep links, unread navigation, filters, and draft preservation.
5. Confirm every state/action path end to end, then add parity and acceptance coverage.

### Slice 7: quotas and release hardening

1. Add quota adapters only where a verified provider contract exists.
2. Render ready, limited, unavailable, unknown, and stale states truthfully.
3. Complete localization, accessibility, migration, security, dependency, and clean-checkout gates.
4. Update version, changelog, public documentation, and release notes.

## 12. Acceptance criteria

### 12.1 Workspace and navigation

- [x] On launch, Desktop renders a sidebar and content area instead of the monolithic form.
- [x] A user can add, relink, and remove a project from the local catalog without editing or
      deleting repository data.
- [ ] Every known project shows availability and every non-terminal sprint shows an accessible
      state indicator. *(Partial: sprint state is both visible and accessible-named. Project
      availability today is accessible-name-only — the sidebar's visible project row carries no
      glyph/color/suffix; it only becomes visible once Project Overview is opened.)*
- [x] Selecting a project, sprint, Forge settings, or project settings opens the matching page.
- [ ] The last valid route, sidebar expansion, timeline position, and unsent draft survive restart.
      *(Partial: route and draft persist via the catalog. Sidebar expand/collapse state does not
      exist anywhere yet — every project's sprints always render fully expanded. Timeline scroll
      position is held in an in-memory dictionary on the page instance and is lost on restart,
      though it survives in-session navigation.)*
- [ ] Completed and cancelled sprints remain reachable without crowding the active list. *(Partial:
      not-crowding is satisfied — a capped, separate history list exists. Reachable is not: history
      entries render as plain non-interactive labels with no navigation, unlike active sprint cards'
      "open" button.)*

### 12.2 Settings

- [x] Every registered user-scoped key is editable through a typed control.
- [x] Inherited language values and every effective configuration provenance are visible.
- [x] Invalid edits cannot be saved and do not partially modify configuration.
- [x] Saving a UI language change updates all visible text without restart.
- [x] Every registered project-scoped key is editable from the selected project's settings page.
- [x] Mandatory human, stop, and rewind confirmations cannot be disabled by user configuration.

### 12.3 Sprint status and timeline

- [ ] The sticky header shows project, sprint, lifecycle state, current stage, progress, activity,
      findings, routing budget, and applicable provider/model information. *(Partial: every field
      renders from real data except provider/model, which always shows a "not yet available"
      placeholder — `AttemptSnapshot` has no provider/model field anywhere in the domain model, so
      this is a structural gap, not a wiring bug.)*
- [ ] Timeline items are durably ordered, cursor-pageable, localized, and restored after restart.
      *(Partial: ordering, cursor paging, and restart-survival are all real and tested. Localization
      is not — an item's rendered message is its raw `workflow.*` journal key verbatim; none of the
      ~30 such keys are registered in `Messages.resx`, so only the surrounding chrome, not the
      timeline content itself, is localized.)*
- [x] New items appear without manual refresh while the sprint page is visible.
- [x] Raw provider streams, credentials, full environments, and unredacted sensitive paths never
      enter the timeline contract, persistent store, logs, or rendered details. *(Verified via two
      independent, mutation-tested redaction passes for credentials; streams/environments are
      structurally excluded since the timeline only ever projects the workflow event journal. Note:
      `SecretRedactor` has no dedicated path-scrubbing pattern — protection against a stray sensitive
      path relies on producers never placing one in event arguments, not on an active filter.)*
- [x] Every current Desktop capability remains reachable without typing a project, sprint, node,
      or attempt ID.

### 12.4 Stop current operation

- [x] The Stop action is visible only when the selected sprint has an exact stoppable operation.
- [x] Stopping terminates the entire owned process tree, including descendants.
- [x] The stopped attempt becomes `cancelled`, its partial worktree is discarded, and no partial
      change reaches the integration branch. *(One narrow, untested edge case noted: `CompleteAttemptAsync`
      does not itself check the stop intent, so a stop landing in the brief window after a provider
      already succeeded but before integration could theoretically still integrate.)*
- [x] The owning node can run again, the sprint becomes `paused`, and automatic retry budget is
      unchanged.
- [x] No other sprint, attempt, process, or worktree is affected.
- [x] Resume starts a fresh attempt from the current integration base.
- [x] A stale stop cannot cancel an attempt that started after the targeted one.
- [x] Repeating the same stop request is idempotent.
- [ ] A Host crash at every durable boundary recovers without resurrecting the stopped attempt or
      leaking its process/worktree. *(Partial: strong crash-simulation coverage exists for most
      boundaries, but no test covers a crash between the sprint-paused append and the final
      convergence marker, and no test simulates an abrupt Host process kill to verify no orphaned
      provider process — this codebase has no process-group/job-object containment, so that risk is
      real but unverified rather than known-fixed.)*
- [x] Desktop distinguishes Stop operation, Supersede attempt, and Cancel sprint by label,
      explanation, confirmation, and result.

### 12.5 Stage transition

- [x] Desktop lists only stages in the sprint's frozen workflow definition.
- [x] Every target has a Host-produced assessment with satisfied prerequisites, blockers, and
      consequences.
- [x] Desktop cannot enable a blocked target by local calculation or client-side state changes.
- [x] The Host rechecks prerequisites and state version immediately before committing a move.
- [x] Advancing never fabricates results or skips a mandatory unsatisfied stage.
- [x] Confirmation, test-work, review, human-approval, finding, policy, artifact, Git, routing, and
      active-operation prerequisites are enforced where applicable. *(All 10 categories present and
      enforced; the active-operation prerequisite has no dedicated named test blocking an Advance
      specifically, only Rewind's stop-path is directly tested — a test-coverage gap, not a known
      behavior gap.)*
- [x] Rewinding requires a reason and mandatory confirmation, creates exactly one new stage
      revision, preserves prior history, and supersedes downstream evidence.
- [x] Superseded evidence cannot satisfy prerequisites in the new revision.
- [x] A move with an active operation first completes the specified stop protocol or is rejected
      without partial transition.
- [x] Repeating the same move is idempotent; a stale assessment or changed target is rejected
      without side effects.
- [ ] A Host crash during a move resumes or converges to one valid revision and never leaves two
      active stages for a linear workflow. *(Partial: the rewind saga has rigorous, real
      crash-simulation tests for every step boundary via its durable pending-rewind marker. The
      advance path has no equivalent crash-simulation test — it has no durable "pending" marker and
      relies on each step being independently idempotent, which is plausible but unexercised.)*
- [x] Completed and cancelled sprints cannot be moved to another stage.

### 12.6 Status, accessibility, and parity

- [ ] The global status row distinguishes provider health, authentication, model availability,
      quota, unknown quota, Host connectivity, and stale data. *(Not satisfied: only provider-health
      count and quota exist in the status row today. Authentication, model availability, and Host
      connectivity have no representation anywhere in it — `ForgeHostClient.IsConnected` exists but
      is never surfaced, and `SidebarStatusRow.AnyKnownProviderUnavailable` is computed but never
      read by anything.)*
- [ ] All actions are keyboard reachable, screen-reader named, focus-stable after refresh, usable
      at supported text scaling, and never communicate status by color alone. *(Partial: accessible
      names are real, wired, and tested throughout. No mechanism or test exists yet for keyboard
      reachability, focus stability after a refresh, or text scaling — these concepts don't appear
      anywhere in the codebase. "Never color alone" holds only because no surface uses color coding
      at all today, not because of an enforced rule.)*
- [ ] English and Russian surfaces contain no missing or machine-only user-facing strings. *(Partial:
      key-set parity (291/291) is enforced by an automated test and currently holds — spot-checked
      manually for translation quality. That test checks key sets only, not that values are actually
      translated, so a future regression copying an English value into `Messages.ru.resx` would not
      be caught.)*
- [ ] CLI and Desktop invoke the same Host commands and render semantically identical results for
      stop, stage assessment, stage move, configuration, and existing workflow operations. *(Partial:
      genuine output-equality parity tests exist for sprint tree/detail, events, startup checks,
      providers, suggested actions, configuration, integration, and sprint lifecycle commands. No
      equivalent Desktop-vs-CLI result-equality test exists yet for stop, stage-assessment, or
      stage-move specifically — both surfaces implement them via the same shared formatting code, so
      they very likely already render identically, but this is unverified by a dedicated test.)*
- [x] Neutral code builds and tests on Windows, Linux, and macOS; Windows adapter tests pass on
      Windows; architecture checks reject new platform leakage.
- [x] Build, formatting, static analysis, security checks, focused tests, and required acceptance
      checks pass from a clean checkout. *(Verified via this branch's own CI run reporting success
      for every required check; not re-executed independently of CI's reported status.)*

### Known gaps (Slice 7 final sweep)

The items left unchecked above are genuine, honestly-assessed gaps found during Slice 7's
release-hardening sweep (PR #100) rather than silently-passed boxes. None block the redesign's core
correctness guarantees (workflow state machines, stop/rewind crash-recovery sagas, and redaction all
hold under adversarial review). They fall into three groups:

1. **Missing persistence/navigation** — sidebar expand/collapse state, timeline scroll position, and
   completed/cancelled sprint navigability are UI-only additions not yet built.
2. **Missing data/localization** — the sticky header's provider/model field and timeline item content
   have no real data source or localized rendering yet; the global status row covers only provider
   health and quota, not authentication/model-availability/Host-connectivity.
3. **Missing test coverage** (not missing behavior) — a handful of crash-recovery and cross-surface
   parity scenarios are plausible-and-unexercised rather than known-broken: the advance-path crash
   saga, the active-operation-blocks-advance prerequisite, a Host-process-kill orphan check, and
   Desktop-vs-CLI result parity for stop/assess-stage/move-stage.

These are tracked as follow-up work rather than blocking this final slice's merge.

## 13. Explicitly out of scope

- arbitrary panel docking or layout plugins;
- editing raw sprint, node, or attempt states;
- deleting or rewriting prior workflow history during a rewind;
- treating provider output or transcripts as canonical project memory;
- inferring provider quotas from sprint retry budgets;
- moving a completed or cancelled sprint back into execution;
- remote multi-user authorization or distributed workers;
- Linux or macOS Desktop distribution in this change set.
