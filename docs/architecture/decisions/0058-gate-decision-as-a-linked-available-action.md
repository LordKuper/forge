# ADR 0058: The gate decision as a timeline-linked available action

- Status: Accepted
- Date: 2026-08-27
- Contract version: AvailableAction 1.1.0; capabilities.json 1.13.0

## Context

`docs/plans/desktop-design-parity-review.md` finding D2: a decision Forge is waiting on is rendered
in a bottom action panel rather than in the event that asked for it. The contract half of that gap is
larger than the rendering half. `IForgeMutations.ResolveGateAsync` has always existed and works, but
the gate appeared nowhere in `AvailableAction`: `AvailableActionProjector.ForSprintAsync` emitted only
`resume_sprint`/`run_sprint`/`cancel_sprint`/`stop_current_operation`/`move_to_stage:*`, so Desktop
built its gate card by scraping a boolean off a snapshot (`SprintWorkspaceViewModel.HasPendingGate`,
"is any node `awaiting_human`") with no way to know which node was waiting, let alone which timeline
event started the wait.

This ADR makes the gate a Host-described action like every other, and adds the linkage a client needs
to place it beside the event that requested it. It deliberately does not change where Desktop renders
anything.

## Decisions

### The gate is projected as two node-scoped actions, not one boolean

`ForSprintAsync` emits an `approve_gate:<node-id>` and a `reject_gate:<node-id>` row for every frozen
`HumanGate` node currently at `awaiting_human`. Modeling the decision as an ordinary
`AvailableAction` means the Host, not each surface, decides whether the decision is available, what
its safety class is, and what it targets -- the same rule every other row in this projection already
follows.

The id is node-scoped, following `move_to_stage:<stage-id>`'s own convention rather than a bare
`approve_gate`. `SprintScheduler.AdvanceGraphAsync` promotes *every* eligible gate, so a graph with
two independent gates legitimately holds two at `awaiting_human` at once; a bare id would then appear
twice in one list and every consumer that looks an action up by id (`SprintActionsViewModel.Find`,
which takes the first match) would silently pick one of them.

The rows are filtered against the frozen graph's own `HumanGate` nodes and gated on a non-terminal
sprint, so the projection never offers a decision `SprintScheduler.ResolveHumanGateAsync` would refuse
with `node_kind_mismatch`, nor one on a sprint that has already been cancelled or completed.

`SafetyClass.HumanApproval` with `ConfirmationRequired = true`, matching `stop_current_operation` and
a rewind: `ResolveGateAsync` itself refuses an unconfirmed call with `confirmation_required`, so any
other classification here would misdescribe the mutation.

### `TimelineSequence` names the requesting event, never the sprint's last sequence

`AvailableActionTarget` gains a nullable `long? TimelineSequence` (last positional, no default,
following `SprintDefinition.Title`'s precedent from ADR 0057). Six construction sites were each
reviewed: five in `AvailableActions.cs` -- `ForProject`, the sprint-lifecycle target, the
`stop_current_operation` target, `BuildMoveToStage`, and this ADR's own gate target -- plus one test
builder in `tests/Forge.Tests/Unit/SprintActionsViewModelTests.cs`. For a gate row the field carries
the `Sequence` of that node's own most recent `NodeChanged` transition into `awaiting_human`, resolved
from the raw journal.

It is emphatically not `SprintWorkflowState.LastSequence`. That value names whatever happened last
anywhere in the sprint -- another node's progress, an operator message posted while the gate waited --
so anchoring to it would attach the decision to an unrelated timeline item while still producing a
plausible-looking number. The *most recent* matching transition, not the first: a rejected gate can be
retried back to `awaiting_human` for a second decision, and only the current round's request is the one
being answered.

Because `SprintTimelineItem.Sequence` is the same `WorkflowEvent.Sequence` (ADR 0054's redesign gave
every timeline item a real, dense sequence of its own), a client can match this value against a
timeline page it already holds with no second lookup and no new query.

### The journal is read only when a gate is actually pending

`ForSprintAsync` documents its cost as bounded by the workflow's declared node count, never by
timeline size. Resolving the requesting event needs the raw event stream, so the `GetEventsAsync` call
sits behind the "at least one gate is pending" check: every sprint without a pending gate keeps its
existing cost exactly.

### Nothing carries a version for the caller to round-trip

`AvailableAction.ExpectedStateVersion` on a gate row is the sprint's journal position, matching
`stop_current_operation` for the same reason: `ForgeApplication.ResolveGateAsync` accepts only
`(projectRoot, sprintId, nodeId, approved, confirmed)` and derives the gate's expected node version and
idempotency key from its own fresh read (ADR 0005). The reported version therefore exists only so a
client can notice its own view is stale, never to be fed back in -- and the action's target needs no
input fields beyond the node id it already carries.

## What stays deferred

- **Rendering the decision inline.** Desktop still renders the gate through
  `SprintWorkspaceViewModel.HasPendingGate` into `ContextualActionHost`, unchanged. Dissolving that
  panel into the timeline and the status header is finding A2, a separate slice; this one lands the
  data it needs.
- **A timeline anchor for `stop_current_operation`.** Its target keeps `TimelineSequence = null`. A
  stop is available because an attempt is running *now*, not because one identifiable event requested
  it, and the event the design would actually anchor a "Retry step" control to is a structured failure
  card that finding D1's payload work has not created yet. Adding a link to the attempt's `running`
  transition would mean shipping a contract field with no reader while forcing a full journal read on
  the common running-sprint path.
- **Per-tool-call "Allow once / Always allow / Deny".** The permission card in the same mockup is not
  this contract at all: it is a decision inside a live provider session, needing an interactive
  provider protocol Forge does not have. Out of scope here -- see
  `docs/plans/desktop-design-parity-review.md` findings D1/D2 and its sequencing section.

## Consequences

- `Forge.Runtime` (`Application/AvailableActions.cs`): `AvailableActionTarget.TimelineSequence`
  (nullable, positional, last); `AvailableAction.ContractVersion` `1.0.0` -> `1.1.0`;
  `AvailableActionProjector.ApproveGateActionPrefix`/`RejectGateActionPrefix` and the gate projection.
- `Forge.Runtime` (`Localization/`): `workspace_action.approve_gate` and
  `workspace_action.reject_gate` in both `Messages.resx` and `Messages.ru.resx`.
- `docs/contracts/v1/capabilities.json`: `1.12.0` -> `1.13.0`; `workspace.available_actions`
  documents the gate pair and `target.timeline_sequence`.
- `VERSION` moves from `0.80.0` to `0.81.0` (MINOR: additive, no breaking change).
- No schema file changes: `AvailableAction` has no JSON schema under `docs/contracts/v1/schemas/`, and
  is versioned by its own C# constant (the same shape `SprintWorkspaceSummary` had before ADR 0057).
- No CLI change: `forge workspace actions` already renders and serializes any action row generically,
  so the new rows and the new target field appear without a code change on that surface.
- No Desktop change: the sprint workspace picks rows by known id and by the `move_to_stage:` prefix,
  so the new ids are ignored there until the slice that renders them lands.

## References

- `docs/plans/desktop-design-parity-review.md` findings D2 and A2 (the gap this ADR's contract half
  closes, and the rendering half it does not)
- ADR 0049 (the `AvailableAction` projection this extends)
- ADR 0054 (why every timeline item carries a real, dense `WorkflowEvent.Sequence` to link against)
- ADR 0057 (the "last positional, no default" precedent for widening a frozen contract record)
