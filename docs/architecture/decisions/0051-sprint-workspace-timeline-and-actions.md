# ADR 0051: Sprint workspace timeline paging, unread tracking, and transition-legality guarantee

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.9.0 (unchanged)

## Context

`docs/plans/desktop-workspace-redesign.md` section 11 Slice 6 replaces the sprint-workspace route's
placeholder controls (ADR 0050's own "functional-but-unstyled" stub) with the plan section 4.3
deliverable: a sticky status header, a chronological timeline with incremental loading/unread
tracking/filters, and a typed contextual-action renderer covering stop-current-operation (ADR
0044/0047) and stage transition (ADR 0045/0046/0048) alongside the already-existing human-only
gates. Slices 1-4 already shipped every read model and backend capability this slice consumes
(`SprintTimelinePage`, `AvailableAction`, `StageTransitionAssessment`, `StopCurrentOperationAsync`,
`MoveSprintToStageAsync`); this ADR records the presentation-layer decisions Slice 6 itself had to
make.

## Decisions

### Unread tracking keys on the journal's own sequence number, not occurrence time

Plan section 4.3 requires "unread position tracking" for the timeline. This ADR originally keyed the
watermark on `SprintTimelineItem.OccurredAt` (UTC ticks) instead of widening `SprintTimelineItem`
with `WorkflowEvent.Sequence`, on the premise that every event for one sprint is appended — and its
`OccurredAt` assigned — in strictly increasing order. Round 1 review of PR #99 (finding 4) disproved
that premise: `OccurredAt` comes from `IClock.UtcNow`, whose resolution is not guaranteed finer than
two calls made moments apart (already documented as reachable in this codebase,
`SprintScheduler.cs`'s own remarks on `RecordedAt`), so a tie could leave a genuinely new item born
already-read — silently, with no way for the user to notice.

`SprintTimelineItem` now carries `Sequence` (the underlying `WorkflowEvent.Sequence` it was projected
from — a dense, strictly increasing per-sprint counter with no such gap), and
`SprintTimelineViewModel` persists the maximum `Sequence` the user has acknowledged instead of a
timestamp. This does widen the wire contract (`SprintTimelinePage.ContractVersion` moves to `1.1.0`,
additively — every existing field is unchanged), which the original decision explicitly wanted to
avoid; ties silently breaking the feature's own core guarantee is judged the worse outcome, and
before `1.0.0` a contract replacement needs no aliasing or deprecation period. The watermark still
only ever advances forward (`ProjectCatalogStore.SetTimelineWatermarkAsync` ignores a lower value
than the one already recorded), so a stale render racing a fresher one can never mark newer items
unread again. `ProjectCatalogEntry.TimelineReadWatermarks` needs no migration: it was introduced in
this same PR and never shipped storing the old ticks-based value.

### The read watermark and the rewind-reason draft live on `ProjectCatalogEntry`, keyed by the sprint id's string form

Plan section 11 Slice 6 item 4 asks for unread navigation and draft preservation across a restart.
`ProjectCatalogEntry` (ADR 0049) already persists per-project, user-scoped state outside `.forge/`
entirely — the same location, not a new store. Two additive, optional dictionary fields
(`TimelineReadWatermarks`, `SprintDrafts`) are keyed by the sprint id's `"D"` string form (matching
every other id already on this record's wire shape) rather than a native `Guid` dictionary key,
which `System.Text.Json` round-trips less predictably. Both are additive on an already-unversioned,
non-schema-validated record (ADR 0049 chose no JSON Schema file for the catalog), so no migration is
needed for an existing `catalog.json`.

Draft scope is deliberately narrow: only the rewind-reason input, the one substantial new free-text
field this slice adds. Every other typed input already in the sprint workspace (gate/confirm/
test-work justification, attempt-supersession instruction) is a one-shot decision entered and
submitted within a single dialog turn, not a value worth surviving a restart — widening draft
preservation to all of them is a separable follow-up, not required by the plan's own "unsent draft"
language, which section 4.1 already scoped to "unsent text," singular.

### The timeline's "load more" affordance, not literal infinite scroll, and no `CollectionView`

The task's own allowance ("a live MAUI virtualization list is fine, doesn't need to be literally
infinite-scroll... a 'load more' affordance backed by the real cursor is an acceptable, honest
implementation") is taken as written. `SprintTimelineViewModel.LoadMoreAsync` treats "the last page
returned fewer than `SprintTimelineProjector.MaxItemsPerPage` items" as "caught up for now," and
distinguishes that from "the next new item will show up on the bounded poll" rather than trying to
promise true infinite scroll. `WorkspaceShellPage.SprintWorkspace.cs` renders the list as a plain
`VerticalStackLayout` rebuilt on each refresh, matching every other dynamic list this shell already
renders (the sidebar, provider rows, node/attempt trees) — introducing `CollectionView` here would be
the first of its kind in this codebase and adds real complexity (a code-only `DataTemplate` factory)
for a payload size (`MaxItemsPerPage` = 500 rows at most, typically far fewer for one sprint) that
does not need virtualization to stay responsive.

### The poll interval matches the executor tick cadence, not a new number

Plan section 10 asks for a "bounded interval" for the selected sprint's live refresh, without naming
one. Every `*ExecutionHostedService` in `Forge.Host.Runtime` already ticks at a 15-second default
(`PlanningExecutionOptions.Interval` et al.) — the fastest cadence anything in this codebase actually
produces a new timeline event at. `WorkspaceShellPage.SprintWorkspace.cs`'s own
`TimelinePollInterval` reuses that exact value via a `Dispatcher.CreateTimer()` scoped to the sprint
workspace page, started when the page renders and stopped on every route change and on
`OnDisappearing` — polling faster would only add Host round-trips without ever finding a new item
sooner. A poll only refreshes the timeline pane, never the status header or contextual actions (plan
section 10's own split: sidebar/summary refresh is slower and separate from the selected sprint's
own cursor-based polling), so it can never reset an in-progress action's own typed input mid-keystroke.

### `control.events` stays reachable in the sprint workspace, alongside the new sprint-scoped Timeline

`SprintTimelinePage` (this sprint only) and `control.events`/`ReadControlEventsAsync` (the whole
project, across every sprint) are different capabilities with different scope — the new Timeline
does not subsume the older one. `WorkspaceShellPage.SprintWorkspace.cs` keeps a small "poll events"
button alongside the Timeline (reusing `SprintWorkspaceViewModel.PollEventsAsync`, unchanged) so plan
12.1's "every current Desktop capability remains reachable" still holds for `control.events` even
though the placeholder page's other raw controls (manual node/attempt id entry, run/resume/cancel as
always-on buttons) are gone.

### Stop and stage-move never trust a render that is already on screen; a fresh read gates every confirmation and every commit

Plan 12.4/12.5's central risk — "the UI must not locally compute or cache whether a target is
enabled" — is enforced structurally, not by convention:

- The Stop button is rendered from a normal `AvailableAction` list load, but clicking it re-calls
  `SprintActionsViewModel.FindFreshStopTargetAsync` (a fresh `GetAvailableActionsAsync`) *before*
  building the confirmation dialog. If the stop action is no longer present (the operation already
  settled since the last render), the dialog never shows at all — the UI reports that plainly and
  refreshes, rather than confirming an action that would then be rejected.
- A stage-move button's `Enabled` state and blockers always come from the same
  `GetAvailableActionsAsync` read that rendered the row — never a client-side recomputation of ADR
  0046's ten prerequisite categories, which this slice's presentation code never touches at all (the
  same structural guarantee ADR 0046 already established: "no prerequisite-evaluation code is added
  to `Forge.Desktop*`"). Clicking the button re-assesses fresh
  (`SprintActionsViewModel.AssessMoveAsync`) before the confirmation dialog is built, and the commit
  (`MoveAsync`) is issued using that exact fresh assessment's `ExpectedStateVersion`/
  `AssessmentToken` — never a value cached from an earlier render. The Host still recomputes and
  rejects a mismatch itself (`StageTransitionCoordinator.MoveAsync`, ADR 0048); Desktop's own
  re-fetch is a second, independent guarantee against the same class of staleness, not a
  substitute for the Host's.
- A rewind's reason is validated non-empty client-side only after the fresh assessment confirms the
  direction is actually `Rewind` — the assessment's own `Direction`/`ConfirmationRequired` fields
  decide this, never a locally cached guess derived from the row's rationale key text.

### Every new destructive action passes the dialog's own answer, never a literal `true`

`SprintActionsViewModel.StopAsync`/`MoveAsync` both take `confirmed` as a plain `bool` parameter and
forward it verbatim to `IForgeMutations.StopCurrentOperationAsync`/`MoveSprintToStageAsync` with no
internal override. `WorkspaceShellPage.SprintWorkspace.cs` supplies that argument only from
`DisplayAlertAsync`'s own return value, reproducing the exact fix shape ADR 0050 already had to apply
twice for the five pre-existing human-only gates (finding 9 of that slice's own review). A dedicated
acceptance test (`SurfaceParityTests.HumanOnlyGatesPassTheDialogsOwnAnswerInsteadOfALiteralTrue`,
extended by this slice) pins both new call sites in the source text, and unit tests
(`SprintActionsViewModelTests`) prove both a `true` and a `false` caller answer reach the fake
mutation double unchanged — a test that would fail immediately if either method hardcoded the
argument.

### Node and attempt ids are resolved from context, never re-added as manual entry fields

Plan 11 Slice 6 item 3 ("remove manual ID fields from ordinary workflows") is satisfied two
different ways, matching what each capability actually needs:

- Gate/confirm/test-work/finalize already default their node id to the built-in graph's own
  canonical node when `null` is passed (`MainPageViewModel`'s existing behavior, unchanged since
  Slice 1). `WorkspaceShellPage.SprintWorkspace.cs` now always passes `null` — the raw `Entry` these
  four controls each had is deleted outright, and each control itself is shown only when its
  canonical node's own current state is `ready` (`SprintDetails.Nodes`, already part of the existing
  full-detail snapshot; not a new query, not a stage-transition-prerequisite recomputation).
- Attempt supersession has no fixed canonical id (attempts are minted per execution), so its target
  is instead the sprint's current running attempt
  (`SprintWorkspaceViewModel.FindActiveAttemptId`, scanning the same already-loaded
  `SprintDetails.Attempts`). The control is hidden entirely when nothing is running, rather than
  shown with an id field that could be left blank or mistyped.
- Stop's target and stage-move's target both come from `AvailableAction.Target`/
  `StageTransitionAssessment`, which the Host already computed — Desktop never derives either.

A rewind's target stage id and reason remain genuinely typed input (the plan's own "a stage target
picked from a list... a rewind reason" carve-out) — picked from the list of `AvailableAction` rows
the Host offered, and typed free text for the reason, never a raw id.

## What stays deferred

- Real per-attempt provider/model data for the status header: no durable field exists anywhere in
  this codebase today (`AttemptSnapshot` carries none), so `ActiveProviderModelText` always renders
  the honest "not yet available" placeholder — the same posture Slice 5/7 already apply to account
  quota. Introducing this would need a new durable field and its own ADR, not a Slice 6 UI
  workaround.
- Localized prose for a stage assessment's `StagePrerequisite.MessageKey`: still rendered as the
  same raw machine text `forge sprint assess-stage` prints (parity, plan 12.6). None of the eleven
  `stage_transition.*` keys the assessor emits are registered in `Messages.resx` today; authoring
  localized prose for them is a separable content task, not blocking this slice. (The timeline's own
  `SprintTimelineItem.MessageKey` half of this deferral closed in PR #107: every `workflow.*`/
  `routing.*` key the journal emits is now registered and resolved through
  `TimelineMessageFormatter` on both surfaces, including `SurfaceFormatting.EventLines`, and a static
  test fails if a future producing key is ever added without a matching resx entry.)
- Promoting `workflow.stop_operation`/`workflow.assess_stage_transition`/`sprint.move_stage`/
  `workspace.summary`/`sprint.timeline`/`workspace.available_actions` from reserved to
  `CapabilityIds.Implemented`, following ADR 0047/0048/0049/0050's own repeated precedent: doing so
  requires updating `SurfaceParityTests.DesktopControls`'s fixed dictionary and widening every
  capability-parity test keyed off `CapabilityIds.Implemented`, which is real but separable cleanup
  that does not gate this slice's functional correctness (every one of these six capabilities is
  already reachable from Desktop through the same local, in-process calls `SidebarViewModel`/
  `ProjectOverviewViewModel` already use, per ADR 0050's own reasoning).
- True CollectionView-based virtualization and literal infinite scroll (see above) — the "load more"
  affordance is the plan's own explicitly accepted alternative.
- Live visual, keyboard-navigation, screen-reader, and text-scaling verification of the new controls:
  no MAUI control can be instantiated headlessly in this repository's test suite (ADR 0050's own
  limitation, unchanged). Coverage here is the neutral view-models (header projection, timeline
  paging/filtering/unread, action rendering, confirmation forwarding) and static text-based checks
  against the Windows adapter's source (`SurfaceParityTests`), matching every prior slice's own
  testing discipline.
- Per-target-stage rewind-reason drafts (one shared draft per sprint, not one per candidate rewind
  target) and scroll-position restoration across a full app restart (only across navigation within
  one running session) — both accepted as minimal, honestly-scoped implementations of "keep it
  minimal" rather than a broader mechanism nothing in the plan specifically required.

## Consequences

- `Forge.Application` (`ProjectCatalog.cs`) gains `ProjectCatalogEntry.TimelineReadWatermarks`/
  `SprintDrafts` (additive, optional) and `ProjectCatalogStore.SetTimelineWatermarkAsync`/
  `SetSprintDraftAsync`/`MaxDraftLength`, plus `DiagnosticCodes.ProjectCatalogDraftTooLong`. No schema
  file changed (ADR 0049's own "no new JSON Schema files" precedent); no migration needed.
- `SprintTimelineItem` (`SprintTimeline.cs`) gains `Sequence`; `SprintTimelinePage.ContractVersion`
  moves to `1.1.0` (additive). Round 1 review of PR #99 (finding 4) — see the corrected "Unread
  tracking" decision above.
- `Forge.Desktop.Presentation` gains `SprintStatusHeaderData`/`SprintStatusHeaderProjector`,
  `TimelineItemView`/`TimelineState`/`SprintTimelineViewModel`, and `SprintActionsViewModel`;
  `SprintWorkspaceViewModel` gains `Timeline`/`Actions` properties, `RefreshHeaderAsync`, and the
  static `FindActiveAttemptId`/`HasPendingGate` context-derivation helpers, and its constructor grows
  three parameters (`ForgeApplication`, `ProjectCatalogStore`, `SurfaceText`) to support them.
- `Forge.Desktop` replaces `WorkspaceShellPage.SprintWorkspace.cs` entirely (sticky header, timeline,
  contextual actions) and adds a `StickyHeaderHost` row to `WorkspaceShellPage.xaml` (a genuinely
  non-scrolling region above the timeline, not merely styling); every raw node/attempt-id `Entry` the
  previous stub page had is deleted.
- `Forge.Localization` gains the new Slice-6 message keys (English and Russian). This includes the
  `workspace_action.*` rationale keys `AvailableActionProjector` has computed since Slice 4 — Round 1
  review of PR #99 (finding 9) found the first version of this slice declared and translated them,
  and `ActionStaleRefreshed`/`TimelineFilterLabel`, without any surface actually rendering any of the
  nine; the contextual-action renderer, the stale-move-target refresh path, and the timeline filter's
  accessible name were fixed to resolve all of them, so this slice is genuinely the first surface to
  render the rationale keys.
- `tests/Forge.Tests` gains `SprintStatusHeaderProjectorTests`, `SprintTimelineViewModelTests`,
  `SprintActionsViewModelTests`, extends `SprintWorkspaceViewModelTests` and
  `ProjectCatalogStoreTests`, and updates `SurfaceParityTests` for the new file shape (a raw-id-entry
  regression test is replaced with a context-derivation one, matching what actually changed).
- `VERSION` moves to `0.68.0` (MINOR: new Desktop capability surface, no breaking contract change).

## References

- Plan sections 4.3, 6.3, 6.4, 7, 8, 10, 11 (Slice 6), 12.3, 12.4, 12.5, 12.6
- ADR 0043/0049 (the read models this slice renders; the reserved-capability reasoning this ADR
  extends unchanged)
- ADR 0044/0047 (stop semantics/implementation this slice's Stop control consumes)
- ADR 0045/0046/0048 (stage revision/prerequisite policy/implementation this slice's move-to-stage
  control consumes, and whose "UI never calculates prerequisites" guarantee this ADR keeps
  structural)
- ADR 0050 (the shell and confirmation-dialog discipline this slice extends; the exact
  confirmation-bypass bug class this ADR's own tests guard against again)
