# ADR 0049: Project catalog and workspace reads

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.9.0

## Context

ADR 0043 reserved `workspace.summary`, `sprint.timeline`, and `workspace.available_actions` (plan
sections 6.1-6.4) and explicitly deferred every read model, storage port, and Host wiring to Slice 4.
This ADR records the decisions that implementation actually had to make, and one deliberate scoping
decision the plan itself invited ("if no user-message/agent-summary artifact type exists yet... it's
reasonable to scope this slice to the system-projection half and note the gap").

## Decisions

### The project catalog is a plain JSON file beside the existing user configuration file, with no capability id

`ProjectCatalogStore` persists to
`<LocalApplicationData>/Forge/<InstanceId>/catalog.json` — the exact directory
`ConfigurationStoreFactory.UserPath` already uses for `config.json`, reusing the same
instance-isolation guarantee (release/Debug/test processes never share one file) without a new
namespacing rule. It is deliberately not routed through `IConfigurationRegistry`/
`ConfigurationSchemaCodec`: those exist to validate a fixed, registered *setting* key set (ADR 0008),
and a catalog entry is a different kind of thing (a list of rows with their own identity), so forcing
it through that machinery would mean either inventing a schema-validated list-valued setting (no
precedent) or bypassing validation entirely (defeating the point of using it). Persistence reuses
`Forge.Configuration.AtomicConfigurationFile.WriteAsync` directly (a same-assembly `internal`
primitive) rather than a second temp-file-then-replace implementation.

Matching ADR 0043's own reasoning: the catalog has no Host protocol surface and therefore no
`capabilities.json` entry or `CapabilityIds` constant at all — every `forge project` subcommand reads
and writes the catalog file directly, never through `IForgeMutations`/`ControlProtocol`/a project's
Host. This is not an oversight; a project's Host is scoped to exactly one project (ADR 0005) and has
no reason to ever know about a Desktop installation's list of other projects.

### A catalog entry only exists for an already-initialized project

`ProjectCatalogEntry` is keyed by the project's own manifest `project_id` (`ProjectIdentity.ReadProjectIdAsync`),
never a second, catalog-local identity. `forge project add <root>` therefore requires the target root
to already be an initialized Forge project; targeting an uninitialized directory is refused with
`project_not_initialized` rather than silently running `forge init` on the caller's behalf. This
keeps the catalog's only real invariant simple (one row per durable project id) and avoids inventing
a placeholder identity for a project that does not have one yet. The Desktop convenience of "pick an
uninitialized folder and offer to initialize it from the sidebar" is a UI-layer flow for a later
slice (folder picker plus `forge init`, then `forge project add`), not a reason to weaken this
slice's own identity model.

### Relink verification reuses the same primitives `AddAsync` already uses

`RelinkAsync` never trusts the caller's claim that a project id and a new root refer to the same
project: it resolves the new root exactly like `AddAsync` (`ProjectRootResolver.ResolveAsync`, then
`ProjectIdentity.ReadProjectIdAsync`) and compares the manifest's own id against the catalog entry
being relinked, rejecting a mismatch with `project_catalog_relink_mismatch` before writing anything.
An unknown entry id is rejected first, before even resolving the new root, with
`project_catalog_entry_not_found` — the same "cheapest check first" ordering
`StopOperationCoordinator.RequestStopAsync` already uses for its own rejection reasons.

### `WorkspaceSummary`/`SprintTimeline`/`AvailableActions` are Host-dispatched per project; the CLI's own cross-project fan-out lives entirely on the client side

A Host is scoped to exactly one project (ADR 0005), so `workspace.summary`'s Host-side query
(`ForgeApplication.GetWorkspaceSummaryAsync`) only ever answers for that Host's own project root —
it is deliberately catalog-agnostic (`ProjectWorkspaceSummary` has no alias field; a project's Host
has no notion of the local catalog at all). `forge workspace summary` itself lists the catalog, then
calls this same bounded per-project computation once per entry and pairs each result with that
entry's own alias/last-route — matching ADR 0043's own framing ("one lightweight query per known
project") literally rather than inventing a second, catalog-aware Host query. `sprint.timeline` and
`workspace.available_actions` stay single-project, exactly like `AssessStageTransition`: a
`ControlProtocol` kind and `ControlPlaneHostedService` dispatch case exist for a future Desktop
client, but the CLI reads directly against `ForgeApplication` (ADR 0048's own "the durable, file-based
journal is the sole source of truth regardless of whether a separate Host process is also running").

### `WorkspaceSummary`'s per-sprint stage/progress reuses `WorkflowFold`/`StageTransitionAssessor.ResolveCurrentStageId` directly, never `StatusAdvisor`

`StatusAdvisor.CreateSnapshotAsync`'s `SnapshotDetail.Summary` mode already folds every sprint's
state to build the sprint list, but does not expose per-sprint node/attempt data unless a specific
sprint is requested at `SnapshotDetail.Full` (which also loads findings and routing detail —
more than the sidebar needs). Rather than widening `StatusAdvisor` to leak that intermediate detail,
`WorkspaceSummaryProjector` independently reloads the journal (`SprintJournal.LoadAllAsync`,
`WorkflowFold.Apply`) and reuses `StageTransitionAssessor.ResolveCurrentStageId` (now `internal`
instead of `private`, so it is the same code, not a duplicated heuristic) to derive current stage and
progress. This never fetches findings, handoffs, or routing detail — only node state already folded
in memory — so a project with many completed sprints costs no more than its currently non-terminal
ones (terminal sprints are skipped before any per-sprint work happens at all).

### Active-operation detection is a small, deliberately duplicated read, not a shared helper

`ActiveOperationLookup.FindActive` (internal, `Forge.Application`) reads the same three durable facts
`StopOperationCoordinator.RequestStopAsync` already checks before accepting a stop request (sprint
running, node running with a current attempt, that attempt non-terminal and not already
stop-requested). It is intentionally not extracted into a shared method the coordinator itself calls:
that method's own check is inline and tightly coupled to its per-branch diagnostic codes, and
refactoring an already-reviewed saga for a read-only projection's convenience is not a change this
slice should make. Both places will silently drift out of sync only if this codebase's core
"one live attempt per running node" invariant itself changes, at which point the coordinator's own
tests would already need touching.

### `AvailableAction` wraps two existing computations; it does not replace either

Plan section 6.4 asked to "reuse the existing suggested-actions concept... if one already exists"
(`SuggestedAction`/`StatusAdvisor.Recommend`, confirmed present) and to extend or wrap it into the
richer contract "if that's the right relationship." It is: `AvailableActionProjector.ForProject`
re-shapes `SuggestedAction` verbatim (rank collapses since ordering is already applied by
`Recommend`; every field with no `SuggestedAction` counterpart — confirmation requirement, input
fields, enabled state, blockers — gets a fixed value, since a suggestion is only ever offered once
already actionable). `SuggestedAction` itself is untouched: `forge next` keeps rendering it directly,
and `ProjectSnapshot.SuggestedActions` keeps its own existing shape and 1.0.0 contract version, which
`AvailableAction`'s own 1.0.0 version does not affect.

The sprint-scoped half is a genuinely different computation with no existing counterpart: lifecycle
actions (resume/run/cancel) gated on `SprintState` alone, stop gated on `ActiveOperationLookup`, and
one stage-move candidate per node in the frozen graph other than the current stage, each backed by a
real `StageTransitionAssessor.AssessAsync` call — never a duplicated prerequisite check. This is
bounded by the workflow's own declared node count (the built-in `implementation-critical` graph has a
handful of nodes), not by timeline size, matching plan section 6.2's own boundedness language even
though 6.4 does not repeat it verbatim. No stage-move candidate is offered while a sprint carries an
unconverged rewind (`SprintSnapshot.PendingRewindTargetStageId`) — every assessment for that sprint
already reports `stage_transition_rewind_in_progress` uniformly, reused rather than re-derived.

### User messages and agent summaries are out of scope for this slice

Plan section 6.3 calls user messages and user-visible agent summaries "separate bounded artifacts"
distinct from the system-projected timeline items this slice builds. No such artifact type exists
anywhere in this codebase today — the closest analog, `IArtifactStore`, remains ADR 0048's own "empty
marker," and nothing records a user-authored chat message or an agent-produced summary durably at
all. Building that artifact type, its storage, and its own redaction/timeline-merge story is a
substantial addition on its own (a new durable record kind, a new capability, new CLI/Host surface)
that the plan's own wording anticipated might not exist yet ("if... building one is a large
undertaking, it's reasonable to scope this slice to the system-projection half"). This slice therefore
implements only the system-event half: `SprintTimelinePage`/`SprintTimelineItem` project the existing
append-only `WorkflowEvent` journal exclusively. A future slice that adds a real user-message/
agent-summary artifact type can merge it into the same timeline cursor space (by occurrence time,
alongside the existing system items) without changing this slice's own contract shape.

### Timeline redaction is enforced as two independent passes, reusing `SecretRedactor` for both

`SprintTimelineProjector.ToItem` calls `Infrastructure.SecretRedactor.RedactProperties` on every
event's arguments once, before the `SprintTimelineItem` is ever returned — this is "before
persistence" in the sense that if a future caching layer ever materializes this projection, it would
only ever see already-redacted content. `CliApplication.WriteTimeline` calls `SecretRedactor.Redact`
again on the fully formatted line immediately before it reaches `TextWriter` — an independent second
pass, so a gap in one pass alone cannot leak a raw secret to a rendered surface (plan 12.3: "redact...
before persistence and again before rendering"). No new redaction rule was written; both passes reuse
ADR 0039's existing chokepoint. `WorkflowEvent`'s two genuinely free-text arguments —
`supersession_instruction` (attempt supersession) and `rewind_reason` (a stage-move rewind) — are the
only realistic vector for an operator accidentally pasting a credential into workflow state; a
dedicated test proves neither survives the projection.

### Actor classification is a closed, bounded heuristic, not a new durable fact

`WorkflowEvent` carries no actor field. `TimelineActor.Operator` is assigned only to the three event
types that exist solely as a direct consequence of a human-only mutation
(`AttemptSuperseded`/`AttemptStopRequested`/`StageRevisionRecorded`); every other event type,
including the convergence markers those same mutations append later
(`AttemptStopConverged`/`StageTransitionConverged`), is `System`. This is a presentation-layer
classification over already-durable facts, not a new source of truth — a future slice that wants
finer-grained attribution (e.g., which specific operator) would need a real actor-identity field on
`WorkflowEvent` itself, which this codebase has never recorded and this slice does not add.

### No new JSON Schema files

Matching Slice 2/3's own precedent (neither `StopOperationResult`/`StageTransitionAssessment`/
`MoveStageResult` has a `docs/contracts/v1/schemas/*.schema.json` file), `ProjectWorkspaceSummary`,
`SprintTimelinePage`/`SprintTimelineItem`, and `AvailableAction` get no new schema file either. The
existing schema directory covers a curated subset of contracts from earlier stages; C# record shapes
plus `StatusJson`'s snake_case serialization remain the actual wire contract for every capability
added since.

### `CapabilityIds.Implemented` is not touched, following ADR 0047/0048's own precedent exactly

`capabilities.json`'s `public_requires_both_surfaces` rule and `SurfaceParityTests.DesktopControls`
(a fixed dictionary indexed unconditionally by `CapabilityIds.Implemented`) would throw for a
capability with no Desktop control, which this slice deliberately does not ship. All three reserved
entries move from "reserved, not implemented" to "implemented on Host and CLI, Desktop deferred" in
their `note` fields (contract version 1.8.0 -> 1.9.0), matching `workflow.stop_operation`'s own
wording. No `CapabilityIds` constants were added for them either, for the same reason ADR 0047/0048
added none. Three new dedicated `SurfaceParityTests` (mirroring
`StopOperationDocumentedCliOptionsMatchTheirActualRequiredness`) close the CLI-option-requiredness gap
`CliExposesEveryDocumentedCapabilityCommand` cannot reach for a reserved capability.

## What stays deferred

- User-message and agent-summary timeline artifacts, and their merge into the same timeline cursor
  space (see above).
- `ProviderQuotaSnapshot` (plan section 6.5) — Slice 7, unaffected by this ADR.
- Desktop sidebar/project-overview/sprint-workspace consumption of any read model this ADR adds
  (Slices 5-6); `CapabilityIds.WorkspaceSummary`/`SprintTimeline`/`WorkspaceAvailableActions` wait for
  that Desktop parity, matching ADR 0037's CLI-first rhythm.
- A folder-picker-driven "add an uninitialized project and offer to initialize it" flow (Desktop-only,
  Slice 5) — `forge project add` requires an already-initialized root in this slice.
- Cross-process locking for `catalog.json` writes: concurrent `forge project` invocations use the same
  best-effort, unlocked read-modify-write posture `forge config user` already accepts for
  `config.json` — a real but pre-existing, not newly introduced, risk.

## Consequences

- `Forge.Application` gains `ProjectCatalog.cs` (`ProjectCatalogEntry`/`ProjectCatalogStore`),
  `WorkspaceSummary.cs` (`ProjectWorkspaceSummary`/`SprintWorkspaceSummary`/`ActiveOperationLookup`/
  `WorkspaceSummaryProjector`), `SprintTimeline.cs` (`SprintTimelinePage`/`SprintTimelineItem`/
  `SprintTimelineCursor`/`SprintTimelineProjector`), and `AvailableActions.cs`
  (`AvailableAction`/`AvailableActionTarget`/`AvailableActionProjector`).
- `ForgeApplication` gains three query methods (`GetWorkspaceSummaryAsync`, `GetSprintTimelineAsync`,
  `GetAvailableActionsAsync`) and three new constructor dependencies; `ForgeHost.AddForgeCore`
  registers the new projectors and `ProjectCatalogStore` as singletons. None of `IForgeMutations`,
  `RemoteForgeMutations`, or the `TestEnvironment.cs` fakes needed any change — every new operation
  here is either a query or entirely local to the catalog file.
- `StageTransitionAssessor.ResolveCurrentStageId` is now `internal` instead of `private`.
- `ControlProtocol` gains `GetWorkspaceSummaryKind`/`GetSprintTimelineKind`/`GetAvailableActionsKind`
  and their request records; `ControlPlaneHostedService` gains matching dispatch cases.
- `capabilities.json` moves from 1.8.0 to 1.9.0.
- `CliApplication.CreateRootCommand` gains an optional trailing `ProjectCatalogStore? catalog = null`
  parameter; `forge project` is added only when one is supplied (`CliHost` always supplies the real,
  DI-registered one). `forge workspace <summary|actions>` and `forge sprint timeline` are added
  unconditionally.

## References

- Plan sections 6.1-6.4 (project catalog, workspace summary, timeline contract, available actions),
  section 11 Slice 4, section 12.1/12.3 (acceptance criteria)
- ADR 0043 (the five-projection decision and reservation this ADR implements)
- ADR 0039 (redaction chokepoint reused for both timeline redaction passes)
- ADR 0047/0048 (the CLI-first / reserved-capability precedent this ADR follows)
- ADR 0005 (Host-per-project scope; `project.snapshot`'s own read-model conventions)
