# ADR 0054: Timeline user messages and agent summaries

- Status: Accepted
- Date: 2026-08-24
- Contract version: capabilities.json 1.11.0

## Context

ADR 0049 deliberately scoped Slice 4's `sprint.timeline` projection to the system-event half of plan
section 4.3's timeline ("if... building [a user-message/agent-summary artifact type] is a large
undertaking, it's reasonable to scope this slice to the system-projection half and note the gap"). A
post-release audit confirmed the gap: `SprintTimelineProjector` only ever produced items from the
existing `WorkflowEvent` journal, so a sprint's timeline could never show what a user actually typed
or what a provider actually reported back. This ADR closes both halves of that gap for real.

## Decisions

### User messages are a new `WorkflowEvent` type in the SAME per-sprint journal, not a second store

Plan section 6.3 says system items are "projections of the existing append-only workflow journal"
while "user messages and agent summaries are separate bounded artifacts" -- read narrowly, that could
mean a second, physically separate store. It is read here as distinguishing *kind of content*
(user-authored free text vs. an automatic state-transition consequence), not *physical storage*:
`WorkflowEvent.UserMessagePostedType` is a new entry in the SAME `events.jsonl` journal
(`FileSprintEventLog`) every other Slice 2/3 addition used
(`AttemptSuperseded`/`AttemptStopRequested`/`StageRevisionRecorded`), never a transition itself (no
`to_state`), matching `AttemptSupersededType`'s own non-transition shape. This is the smaller, safer
change and the one this codebase's own established pattern points to directly:

- It gets a dense, unique, strictly increasing `WorkflowEvent.Sequence` for free -- the exact property
  `SprintTimelineCursor`'s single-watermark paging already depends on. A second store would need its
  own ordering key merged against the journal's, reopening the "two cursors, one page" problem ADR
  0049's own remarks already flagged as unsolved.
- Redaction, restart-persistence, and the idempotency-key ledger `AppendStageRevisionRecordedAsync`
  already uses all apply unmodified -- no new mechanism, only a new event type and one new store
  method (`ISprintStore.AppendUserMessageAsync`).
- The bounded text lives in `WorkflowEvent.Arguments[message_text]`, sized like every other bounded
  free-text argument this journal already carries (`supersession_instruction`, `rewind_reason`) --
  well within anything that would justify a separate content-addressed artifact store.

Deduplication does not reuse `AppendTransitionAsync`'s expected-version/idempotency-key pair: a
message post never conflicts with concurrent workflow progress (unlike a rewind, which must never
double-apply against a moving sprint revision), so gating it on sprint version would only add
friction with no correctness benefit. Instead the caller-supplied message id IS the event's own
`EventId`, and `AppendUserMessageAsync` dedupes by scanning for that id already having landed --
matching `AppendAttemptSupersededAsync`'s own "recorded once" idiom, adapted from "once per attempt"
to "once per caller-supplied id". `ForgeApplication.PostSprintMessageAsync` mints that id itself, the
same "server-side derivation" `SupersedeAttemptAsync`'s own remarks describe -- a caller-level retry
after a genuine send failure can, in principle, double-post (there is no domain state to dedupe a
retry against, unlike an already-cancelled attempt). This is accepted, named debt: the same class of
risk an ordinary chat composer's "double click send" already carries, not a data-integrity concern.

`SprintScheduler.MaxUserMessageLength` aliases `MaxSupersessionInstructionLength` (4000 characters)
rather than a new bound, matching `ProjectCatalogStore.MaxDraftLength`'s own existing precedent for
"reuse the established bounded-free-text length, don't invent a new one."

### Agent summaries are projected from the existing `Handoff.Summary`, not a new artifact type

Investigation confirmed user-visible agent summary content already exists and is simply never
surfaced: `Handoff.Summary` (`docs/contracts/v1/schemas/handoff.schema.json`) is exactly "the
structured context one node leaves for whatever runs next" -- free text by contract, recorded by
`SprintScheduler.RecordHandoffAsync` whenever `PlanningExecutionHostedService`/
`ImplementationExecutionHostedService` completes a node with `outcome.Summary is not null`. No new
artifact type, storage, capability, or schema kind was needed -- only a new `SprintTimelineProjector`
branch that reads `ISprintStore.GetHandoffsAsync` and projects each non-superseded `Handoff` as its
own timeline item (`SprintTimelineProjector.AgentSummaryRecordedType`, `TimelineActor.Agent`).

A `Handoff` carries no timestamp or journal sequence of its own, so it cannot be merged into the
timeline's dense per-sprint order without one. Rather than adding a timestamp (which still would not
solve ordering relative to system events) or building a second cursor to merge, `Handoff` gains one
new field, `Sequence` (`docs/contracts/v1/schemas/handoff.schema.json`'s `sequence`, optional --
matching how `revision`/`superseded_at_revision` were already added post-1.0.0 without a schema
version bump). `RecordHandoffAsync` always runs immediately after the node's own completing
transition has already been appended (verified directly in both hosted services: `CompleteNodeAsync`
is called, checked for success, and only then is `RecordHandoffAsync` called), so the sprint state's
own `LastSequence` at that exact moment IS that transition's own sequence -- not an approximation.
`SprintTimelineProjector` anchors each handoff's projected item to the nearest real event at or
before that borrowed sequence (`FindAnchor`) for its `OccurredAt`, and merges/pages the two sources by
sequence (`MergeAndPage`). A handoff shares its sequence with exactly the one real event it anchors
to (each node completion has a unique sequence, and at most one handoff is recorded per completion),
so a page boundary is never allowed to fall strictly between the two: once the soft
`MaxItemsPerPage` bound is reached, taking continues only while the next candidate's sequence still
equals the last one taken, so a same-sequence pair can add at most one extra item to a page but is
never split across two -- the split would otherwise skip the trailing half forever, since its
sequence would never again exceed the advanced watermark. A superseded `Handoff` (a rewind
invalidated it) is never projected, matching how a superseded artifact is already excluded from
`SprintScheduler.IsTestWorkEligibleAsync`'s own eligibility check.

The specific provider/model identity is not recorded on `Handoff` today, so `TimelineActor.Agent`
carries no finer attribution than "not System, not Operator" -- the item's `TargetKind`/`TargetId`
name the producing node instead, the same granularity `AvailableActionProjector` already uses
elsewhere for a node-scoped fact.

### `sprint.post_message` stays a reserved capability, matching this codebase's own dominant pattern

`sprint.post_message` ships fully functional on Host, CLI, and Desktop (a new `PostSprintMessageKind`
`ControlProtocol` request/dispatch, `forge sprint message <id> <text>`, and a message composer in the
sprint workspace) but is deliberately NOT added to `CapabilityIds.Implemented` or
`RemoteForgeMutations.CapabilityByKind`. This is not a shortcut -- it is the same posture
`sprint.timeline`/`workflow.stop_operation`/`workflow.assess_stage_transition`/`sprint.move_stage`/
`provider.quota_status` already ship under (five of the eight most recently added capabilities): a
reserved capability is never gated, so an older client still works against a Host that already serves
it, and promoting it to `Implemented` is a separable follow-up requiring `SurfaceParityTests`'s fixed
dictionaries (`DesktopCapabilityCalls`, the reserved-capability-specific CLI-option tests) to widen --
deliberately kept out of this change's own diff.

### No confirmation gate

Plan section 6.4's `AvailableAction.safety_class` concept distinguishes destructive/history-invalidating
actions (which require a confirmation dialog naming their exact target and consequences) from
additive ones. Posting a message changes nothing about workflow state, is fully reversible in effect
(it is just a line in a chat-like timeline), and has no consequence to disclose -- the same reasoning
`CreateSprintAsync`/`RunSprintAsync`/`ResumeSprintAsync` already use for staying unconfirmable, unlike
`SupersedeAttemptAsync`/`CancelSprintAsync`/`MoveSprintToStageAsync`. `IForgeMutations.PostSprintMessageAsync`
therefore carries no `confirmed` parameter at all, and the Desktop composer shows no dialog.

### The message-composer draft is a parallel field, not a reuse of the rewind-reason draft

`ProjectCatalogEntry.SprintDrafts` is already committed to one specific free-text field (the
move-to-stage rewind reason) -- its own remarks say so explicitly ("the sprint workspace's
rewind-reason input, the one substantial new free-text field this slice adds"). Since a sprint can
have an in-progress rewind reason and an in-progress message draft open at the same time, reusing
that one slot would silently clobber whichever the user typed second. `MessageDrafts` is a new,
identically-shaped dictionary field (same key convention, same `MaxDraftLength` bound, same
empty-clears-the-entry convention) with its own `ProjectCatalogStore.SetSprintMessageDraftAsync` and
`SprintTimelineViewModel.LoadMessageDraftAsync`/`SaveMessageDraftAsync` pair.

### Rendering needs no new UI code on either surface

Both `CliApplication.WriteTimeline` and `SprintTimelineViewModel.ToView`/`WorkspaceShellPage`'s
timeline renderer already iterate `SprintTimelineItem.Type`/`Actor`/`MessageKey`/`Arguments`
generically (`SurfaceFormatting.Machine<TEnum>` renders any enum, including the new
`TimelineActor.Agent`, via reflection over its snake_case name). A posted message's `message_text`
argument and a projected summary's `summary` argument both flow through the existing per-item
argument line with zero new rendering branches -- confirming plan section 4.3's own expectation that
the timeline is "one chronological list" the existing generic path already renders.

## What stays deferred

- Promoting `sprint.post_message` to `CapabilityIds.Implemented` (see above) -- a separable follow-up.
- A dedicated `AvailableAction` entry for posting a message -- the composer is a fixed control in the
  sprint workspace, matching how `instructionEntry`/`definitionOfDoneEntry`/`justificationEntry` are
  already fixed controls rather than `AvailableAction`-described input fields.
- Recording which specific provider/model produced a `Handoff.Summary` -- `Handoff` has no such field
  today; adding one is a separate, larger contract change this ADR does not need.
- True at-most-once delivery for a client-level retry of a failed message send (see "accepted, named
  debt" above).

## Consequences

- `Forge.Domain` (`WorkflowEvents.cs`): `WorkflowEvent.UserMessagePostedType`/`UserMessageTextArgument`;
  `WorkflowFold`/`IsTransitionRecord` gain a non-transition branch for it, mirroring
  `AttemptSupersededType`. `Handoff` gains `Sequence` (default `0`, additive).
- `Forge.Application`: `ISprintStore.AppendUserMessageAsync` (+ `FileSprintEventLog` implementation and
  every test double); `SprintScheduler.MaxUserMessageLength`/`PostUserMessageAsync`/
  `PostSprintMessageResult`; `RecordHandoffAsync` now captures and passes `Sequence`;
  `WorkflowRecordCodec`'s `WireHandoff`/`ValidateHandoff` carry it through schema validation;
  `SprintTimelineProjector` gains `TimelineActor.Agent`, `AgentSummaryRecordedType`, the
  `MergeAndPage`/`FindAnchor`/`ToItem(Handoff, WorkflowEvent?)` merge logic, and reads
  `GetHandoffsAsync` alongside `GetEventsAsync`; `ProjectCatalogEntry.MessageDrafts`/
  `ProjectCatalogStore.SetSprintMessageDraftAsync`; `ForgeApplication`/`IForgeMutations`/
  `RemoteForgeMutations` gain `PostSprintMessageAsync`; `DiagnosticCodes.UserMessageTooLong`/
  `UserMessageRequired`.
- `Forge.Host.Client`: `ControlProtocol.PostSprintMessageKind`/`PostSprintMessageRequest`.
- `Forge.Host.Runtime`: `ControlPlaneHostedService.DispatchPostSprintMessageAsync`.
- `Forge.Cli`: `forge sprint message <id> <text>`.
- `Forge.Desktop.Presentation`: `MainPageViewModel.PostMessageAsync`;
  `SprintWorkspaceViewModel.PostMessageAsync`; `SprintTimelineViewModel.LoadMessageDraftAsync`/
  `SaveMessageDraftAsync`.
- `Forge.Desktop`: a message composer (`Entry` + send `Button`) in `WorkspaceShellPage.SprintWorkspace.cs`,
  routed through the shared `ShellRenderGate`/`RunAsync` mutation guard exactly like every other
  contextual action on that page.
- `docs/contracts/v1/schemas/handoff.schema.json`: optional `sequence` field, schema_version unchanged
  (1.0.0), matching `revision`'s own earlier precedent.
- `capabilities.json` moves from 1.10.0 to 1.11.0: a new reserved `sprint.post_message` entry; the
  existing `sprint.timeline` entry's note is corrected to no longer claim user messages/agent
  summaries are unrepresented.
- `VERSION` moves from 0.71.0 to 0.72.0 (MINOR: new feature, no breaking change).

## References

- Plan sections 4.3 (sprint workspace timeline composition), 6.3 (timeline contract), 6.4 (available
  action safety class)
- ADR 0049 (the original Slice 4 scoping decision this ADR closes)
- ADR 0006 (the bounded-instruction-artifact precedent `MaxUserMessageLength` reuses)
- ADR 0045 (the rewind-supersession `Revision`/`Superseded` convention `Handoff.Sequence` follows)
- ADR 0053 (the capability-negotiation gate `sprint.post_message` deliberately stays outside)
