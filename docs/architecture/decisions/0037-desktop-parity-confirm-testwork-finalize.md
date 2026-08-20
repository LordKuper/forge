# ADR 0037: Desktop parity for `workflow.confirm`/`workflow.test_work`/`workflow.finalize`

- Status: Accepted
- Date: 2026-08-20

## Context

ADR 0034 (`workflow.confirm`), ADR 0035 (`workflow.test_work`), and ADR 0036
(`workflow.finalize`) each shipped a CLI-only human-only command and
explicitly deferred Desktop parity, matching `workflow.review`/
`attempt.supersede`'s own first-slice precedent (ADR 0019): the capability
landed in `capabilities.json` and `IForgeMutations`, but not in
`CapabilityIds.Implemented`, so `SurfaceParityTests`' Desktop-parity checks
did not yet require a matching Desktop control.

With all three CLI commands shipped and reviewed, this item closes that gap:
every human-only node-settling capability (`workflow.review`,
`attempt.supersede`, `workflow.confirm`, `workflow.test_work`,
`workflow.finalize`) now has a Desktop control, and every `Work` role in the
built-in `implementation-critical` graph is reachable from both surfaces.

## Decisions

### `MainPageViewModel` gains three methods, each mirroring `ResolveGateAsync`/`SupersedeAttemptAsync` exactly

`ConfirmNodeAsync`, `RecordTestWorkAsync`, and `FinalizeSprintAsync` each
share `ResolveSprintIdAsync`/`SprintTarget`'s blank-means-active-sprint/
ambiguity resolution, mandatory (never config-bypassed) confirmation, and
`resolveMutations`-routed Host dispatch -- the same shape already
established for `workflow.review`/`attempt.supersede`. No new resolution
logic was written; the three methods differ only in which fields they
validate and which `IForgeMutations` member they call.

### Required free-text fields are refused before the confirmation dialog shows

`workflow.confirm`'s definition-of-done/evidence and `workflow.test_work`'s
justification have no default to fall back to (unlike a node id), so a
blank value is refused immediately -- matching
`SupersedeAttemptAsync`'s own blank-instruction guard exactly, rather than
showing a dialog asking the user to confirm a decision with no actual
content. `workflow.confirm`'s evidence-kind uses a `Picker` seeded with the
same three machine values `forge confirm --evidence-kind` accepts
(`inspection`/`execution`/`existing-check`), defaulted to the first entry,
so it can never be blank the way a free-text `Entry` could.

### Each capability gets its own three-line prompt, naming exactly what it acts on

`ConfirmPrompt`/`TestWorkPrompt`/`FinalizePrompt` mirror `GatePrompt`'s own
shape (sprint id, then a capability-specific detail line or two) --
`SurfaceParityTests`' existing "dialog names its target instead of
repeating the action name" pattern (ADR 0021) is extended with one check
per new capability, pinning the code-behind sources each dialog's message
from its own prompt method.

### `CapabilityIds.WorkflowConfirm`/`.WorkflowTestWork`/`.WorkflowFinalize` join `Implemented`

The only change actually required to make `SurfaceParityTests`' existing
Desktop-parity checks (`DesktopExposesEveryImplementedCapability`,
`DesktopControlsAreWiredInCodeBehind`) start enforcing these three
capabilities' own Desktop controls -- the checks themselves needed no new
logic, only new `DesktopControls` dictionary entries naming each
capability's controls.

### `capabilities.json` notes updated, no `contract_version` bump

Each entry's `note` field previously stated "CLI-only for now... not yet in
`CapabilityIds.Implemented`"; that sentence is now false, so it is replaced
with a one-line pointer to this ADR. No capability's `id`/`kind`/`contract`/
`events`/`cli`/`desktop`/`permission`/`acceptance` shape changed -- only
prose describing an already-existing entry -- so `contract_version` stays
at `1.5.0`, unlike ADR 0034/0035/0036's own additive bumps.

## Consequences

- New `MainPageViewModel.ConfirmNodeAsync`/`.ConfirmPrompt`,
  `.RecordTestWorkAsync`/`.TestWorkPrompt`, `.FinalizeSprintAsync`/
  `.FinalizePrompt`, plus a private `ParseEvidenceKind` helper mirroring
  `CliApplication`'s own.
- New `MainPage.xaml` controls (`ConfirmNodeIdEntry`,
  `ConfirmDefinitionOfDoneEntry`, `ConfirmEvidenceEntry`,
  `ConfirmEvidenceKindPicker`, `ConfirmConfirmedButton`,
  `ConfirmNotConfirmedButton`, `ConfirmResultLabel`; `TestWorkNodeIdEntry`,
  `TestWorkJustificationEntry`, `TestWorkAddedButton`,
  `TestWorkNoNewTestsButton`, `TestWorkResultLabel`; `FinalizeNodeIdEntry`,
  `FinalizeButton`, `FinalizeResultLabel`), each reusing the existing
  `SprintIdEntry` for its own sprint context, matching every prior
  human-only capability's own XAML shape.
- New `MessageKeys`/RESX entries (English and Russian) for each control's
  label/placeholder, action button text, confirmation-required text,
  sprint-ambiguous text, and required-field text.
- `CapabilityIds.WorkflowConfirm`/`.WorkflowTestWork`/`.WorkflowFinalize`
  added to `CapabilityIds.Implemented`.
- `capabilities.json`'s three entries' `note` fields updated; no schema or
  `contract_version` change.
- New `SurfaceParityTests` coverage: three `DesktopControls` entries, three
  dialog-naming checks, two blank-required-field-before-dialog checks.
- New `MainPageViewModelTests` coverage for all three methods and prompts.

## References

- ADR 0019 (human-gate and supersession CLI commands -- the Desktop
  parity precedent this item now extends to three more capabilities)
- ADR 0021 (confirmation dialogs must name their target, not repeat the
  action name -- the pattern this item's three new prompts follow)
- ADR 0034 (confirmation node CLI command -- the capability this item adds
  Desktop parity for)
- ADR 0035 (test-work node CLI command -- same)
- ADR 0036 (finalization node CLI command -- same, and the one explicitly
  naming Desktop parity as future work)
