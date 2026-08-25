# ADR 0055: Attempt provider/model field

- Status: Accepted
- Date: 2026-08-25
- Contract version: project-snapshot.schema.json 1.4.0

## Context

ADR 0051's "What stays deferred" section named this gap explicitly: plan section 12.3's sticky
header must show "applicable provider/model information," but `AttemptSnapshot` carried no such
field, so `SprintStatusHeaderProjector` always rendered the honest "not yet available" placeholder.
This ADR adds the field and closes the gap for real.

## Decisions

### `AttemptSnapshot` gains plain nullable `Provider`/`Model` strings, not a new enum

`Forge.Domain` deliberately contains no provider identifier type (ADR 0008: "the core contains no
provider enum, concrete provider identifier"). `ExecutionProfile` already models provider/model as
plain strings for exactly this reason; `AttemptSnapshot.Provider`/`.Model` matches that existing
precedent rather than inventing a second representation.

### Provider/model is derived from the frozen `ExecutionProfile`, never cached and carried forward

The first shipped version of this change copied `attempt.Provider`/`.Model` onto a superseded
attempt's replacement. Independent review found this reads a value that can legitimately be null
(a legacy pre-1.4.0 attempt, or an attempt created via a path that hadn't yet computed routing) and
therefore propagates that null downstream instead of failing safe. Both call sites that mint an
`attempt_created` event (`SprintScheduler.StartAttemptAsync`'s fresh-attempt and crash-resume
branches, and `SupersedeAttemptAsync`'s replacement-creation branch) now derive provider/model
directly from `ExecutionProfilePolicy.PhaseFor(definedNode.Role)` against the sprint's own frozen
`SprintDefinition.ExecutionProfiles` — the same source of truth `StartAttemptAsync` itself routes
from — rather than trusting a copy. A node's role is fixed for the sprint's lifetime and
`ExecutionProfiles` never changes after sprint creation, so this is always correct, never stale, and
immune to a null intermediate value.

`ExecutionProfilePolicy.PhaseFor` is a total switch returning `null` for non-model-bearing roles
(`Generic`/`Intake`/`Confirmation`/`TestWork`/`HumanApproval`/`Finalization`) — every call site pairs
it with `GetValueOrDefault`, so a role with no execution profile degrades to the existing placeholder
rather than throwing or showing a malformed value.

### Legacy events fold to null, never throw

`WorkflowFold`'s handling of the new `ProviderArgument`/`ModelArgument` follows the same
`TryGetValue && value is not null ? value : previous?.X` carry-forward pattern already established
for `BaseCommit`/`SupersedesAttemptId` — a pre-1.4.0 event missing these arguments folds to
`Provider = null`/`Model = null` without throwing, matching every other additive event-argument
change in this codebase's history.

## What stays deferred

- Recording provider/model on `Handoff` (ADR 0054 already named this as separate, larger contract
  work this ADR does not need).
- Showing the last-known provider/model during the narrow window between a stop request and its
  convergence, where `ActiveOperationLookup.FindActive` currently excludes the attempt and the header
  falls back to the placeholder even though the provider may still be running — documented as
  intentional (matching `FindActive`'s existing "only a live operation" semantics), not fixed here.

## Consequences

- `Forge.Domain` (`WorkflowContracts.cs`): `AttemptSnapshot.Provider`/`.Model` (nullable strings).
- `Forge.Domain` (`WorkflowEvents.cs`): `WorkflowEvent.ProviderArgument`/`ModelArgument`; `WorkflowFold`
  gains the carry-forward branch for both.
- `Forge.Runtime` (`SprintScheduler.cs`): `StartAttemptAsync`'s routing lookup is hoisted above the
  fresh-attempt/crash-resume split so both paths record provider/model; `SupersedeAttemptAsync`
  derives the replacement's provider/model from the frozen profile instead of copying the superseded
  attempt's own fields.
- `Forge.Runtime` (`Application/EntityStatus.cs`, `StatusAdvisor.cs`): `EntityStatus.Provider`/`.Model`
  projected from the attempt.
- `Forge.Desktop.Presentation` (`SprintStatusHeader.cs`): `SprintStatusHeaderProjector.Build` renders
  the real routed provider/model once known, keeping the existing placeholder only when genuinely
  unknown.
- `docs/contracts/v1/schemas/project-snapshot.schema.json`: `1.3.0` → `1.4.0` (additive nullable
  fields on the entity shape); `StatusAdvisor.ContractVersion` matches.
- `VERSION` moves from `0.72.0` to `0.73.0` (MINOR: additive, no breaking change).

## References

- Plan section 12.3 (the sticky header criterion this ADR closes)
- ADR 0051 (the original deferral this ADR resolves)
- ADR 0008 (the "no provider enum in `Forge.Domain`" precedent `Provider`/`Model` follow)
