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

### Agent summaries are projected from a real journal event, not a borrowed sequence

Investigation confirmed user-visible agent summary content already exists and is simply never
surfaced: `Handoff.Summary` (`docs/contracts/v1/schemas/handoff.schema.json`) is exactly "the
structured context one node leaves for whatever runs next" -- free text by contract, recorded by
`SprintScheduler.RecordHandoffAsync` whenever `PlanningExecutionHostedService`/
`ImplementationExecutionHostedService` completes a node with `outcome.Summary is not null`.

**This section originally shipped a different design, replaced end-to-end by the PR #104 review
(finding 1) before this ADR's first release.** The original design gave `Handoff` a `Sequence` field
holding the sprint's `LastSequence` watermark at record time, and had `SprintTimelineProjector` anchor
each handoff to "the nearest real event at or before" that borrowed value (`FindAnchor`), merging the
two sources by sequence (`MergeAndPage`). That was unsound on two independent counts, both found in
the same review round:

- **Write-ordering hole (finding 1).** The events journal and the handoff store are two separate,
  non-atomic writes. A timeline page fetched between the node's completing transition landing and the
  handoff write landing would set its `nextWatermark` to that transition's own sequence -- correctly,
  since the handoff did not exist yet. Once the handoff then landed carrying that same borrowed
  sequence, `handoff.Sequence > watermark` was false for every future page: the summary was
  permanently, silently unreachable by any cursor already past it. This was not a rare race; it was
  the ordinary outcome of any poll landing in the (routine) gap between the two writes.
- **Anchoring-correctness hole (finding 6).** `LastSequence` is the journal's current head at read
  time, not necessarily the sequence of the specific transition the handoff belongs to. Any other
  append landing in the gap -- another tick loop's own transition, or this very ADR's own
  `AppendUserMessageAsync` -- could anchor the handoff to a completely unrelated, later event.

Both holes trace to the same root cause: borrowing an existing sequence number instead of giving the
summary its own real append. This mirrors a lesson this codebase had already learned once, for the
same reason, in the user-messages half of this same ADR (an event in the SAME journal, never a
borrowed value from a different one) and in earlier PRs' stop-intent/stage-revision events -- the
fix here follows that established pattern precisely rather than patching the borrowed-sequence
design. `WorkflowEvent.AgentSummaryRecordedType` is now a real journal entry, appended by
`ISprintStore.AppendAgentSummaryRecordedAsync` at the exact moment `RecordHandoffAsync` runs, on the
producing node's own aggregate, carrying the summary text (`AgentSummaryTextArgument`) and the owning
`Handoff.HandoffId` as the event's own `CorrelationId`. Its `Sequence` is assigned atomically by the
same append this store already guarantees for every other event type -- there is nothing left to
borrow, and nothing left to anchor: `SprintTimelineProjector.ToItem` builds the timeline item directly
from the event, the same generic path every other event type already uses. `Handoff.Sequence` itself
is removed (along with `docs/contracts/v1/schemas/handoff.schema.json`'s `sequence` and
`WorkflowRecordCodec`'s corresponding wire field) -- nothing needs it once summaries are real events;
keeping it would have been unused debt.

`MergeAndPage` still reads `ISprintStore.GetHandoffsAsync`, but now only to build a superseded-id set:
a rewind invalidates a `Handoff` by setting its mutable `Superseded` field, something the immutable,
already-landed `AgentSummaryRecordedType` event cannot itself carry. An event whose `CorrelationId`
names a superseded handoff is filtered out before projection, matching how a superseded artifact is
already excluded from `SprintScheduler.IsTestWorkEligibleAsync`'s own eligibility check. Because every
candidate is now a single `WorkflowEvent` with its own real, dense, globally-unique `Sequence`, two
candidates can never tie -- the original `FindAnchor`/tie-break logic (round 1's `MaxItemsPerPage`
soft-bound special case) is gone entirely, and `MergeAndPage` collapses back to the simple
`Where -> OrderBy -> Take -> Select` shape system-only projection used before agent summaries existed
(PR #104 review, finding 3: the interim two-source design projected and redacted every candidate above
the watermark before applying the page bound at all, an unbounded-work regression this redesign also
undoes for free).

The specific provider/model identity is not recorded on `Handoff` today, so `TimelineActor.Agent`
carries no finer attribution than "not System, not Operator" -- the item's `TargetKind`/`TargetId`
name the producing node instead, the same granularity `AvailableActionProjector` already uses
elsewhere for a node-scoped fact.

### A loaded summary can be superseded; Desktop must refresh, not just append

`MergeAndPage`'s filter guarantees "absent from any future page fetched after supersession" -- it
does not retract an item a client already rendered from an earlier page. Agent summaries are this
timeline's first retractable item (a rewind can supersede one after it was already served), and
`SprintTimelineViewModel`'s `loaded` list only ever grows (`LoadMoreAsync`'s `AddRange`). Desktop
closes this gap at the one place a supersession is actually caused client-side:
`WorkspaceShellPage.SprintWorkspace.cs`'s `MoveToStageAsync` now asks `RefreshAllAsync` for a full
timeline `InitializeAsync` (which clears `loaded` and restarts from a fresh cursor) rather than an
incremental `LoadMoreAsync`, whenever the move it just committed was a rewind. This does not close the
gap for every conceivable path to a superseded summary (a rewind driven from the CLI or another
Desktop session, observed only through the poll, still leaves the stale item in place until the next
navigation) -- named as remaining, accepted debt below, not solved by this ADR.

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
- Retracting a superseded summary already rendered in a Desktop session other than the one that
  triggered the rewind, or on the CLI's own one-shot `forge sprint timeline` render -- the CLI never
  accumulates a `loaded` list to retract from, and the triggering Desktop session's own
  `MoveToStageAsync` fix (above) only refreshes the workspace that performed the move.

## Consequences

- `Forge.Domain` (`WorkflowEvents.cs`): `WorkflowEvent.UserMessagePostedType`/`UserMessageTextArgument`;
  `WorkflowEvent.AgentSummaryRecordedType`/`AgentSummaryTextArgument`; `WorkflowFold`/`IsTransitionRecord`
  gain a non-transition branch for each, mirroring `AttemptSupersededType`. `Handoff.Sequence`, added and
  then removed within this same ADR (see "Agent summaries" above) -- `Handoff` ships unchanged from
  before this ADR.
- `Forge.Application`: `ISprintStore.AppendUserMessageAsync`/`AppendAgentSummaryRecordedAsync` (+
  `FileSprintEventLog` implementation and every test double); `SprintScheduler.MaxUserMessageLength`/
  `PostUserMessageAsync`/`PostSprintMessageResult`; `RecordHandoffAsync` now appends the summary event
  right after saving the `Handoff`, instead of stamping a borrowed sequence onto the `Handoff` itself;
  `SprintTimelineProjector` gains `TimelineActor.Agent`, `AgentSummaryRecordedType`, an injectable
  `maxItemsPerPage` constructor parameter (default `MaxItemsPerPage`, PR #104 review finding 4 --
  lets a test force a real page boundary), and reads `GetHandoffsAsync` alongside `GetEventsAsync`
  only to filter out a superseded summary's event; `ProjectCatalogEntry.MessageDrafts`/
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
  contextual action on that page; `RefreshAllAsync` gains a `resetTimeline` parameter, set by
  `MoveToStageAsync` whenever the just-committed move was a rewind, so a superseded summary does not
  linger in that session's already-loaded timeline (PR #104 review, finding 5).
- `docs/contracts/v1/schemas/handoff.schema.json`: no change from before this ADR -- an optional
  `sequence` field was added and then removed within this same ADR (see "Agent summaries" above);
  schema_version stays `1.0.0` throughout.
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
