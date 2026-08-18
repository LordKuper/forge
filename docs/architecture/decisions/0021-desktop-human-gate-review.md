# ADR 0021: Desktop human-gate review controls

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66 is worked as a sequence of vertical slices (ADR
0019). Two CLI-side slices landed (ADR 0019: `forge gate approve|reject`/
`forge attempt supersede`; ADR 0020: `forge sprint create|run|resume|
cancel`) — all backend/CLI, nothing on the Desktop surface. ADR 0019's
own Context section named "Desktop parity" as "the largest single piece of
this item": the Desktop app is a single flat `MainPage` with no navigation
and no per-entity detail views, and `SurfaceParityTests` requires a
Desktop control for every id in `CapabilityIds.Implemented` — adding a
capability there without matching Desktop controls fails immediately, so
Desktop parity cannot be deferred piecemeal the way CLI wiring was.

Given the size of full Desktop parity (attention navigation, a gate view,
per-entity detail, accessibility, for every implemented-but-Desktop-absent
capability — `sprint.manage`, `attempt.supersede`, `workflow.review`, and
several read-only ones with no backend caller at all yet), this is worked
as its own sequence of slices within the larger item, matching how the
CLI side already did. This ADR covers the **first Desktop slice only**:
`workflow.review` (gate approve/reject), added directly to the existing
flat `MainPage` rather than behind a new navigation shell — see "Flat page,
not a navigation shell, for this slice" below for why.

## Decisions

### `workflow.review` first, not `sprint.manage`/`attempt.supersede`

`workflow.review` is a single boolean-shaped action (approve/reject one
gate) with an existing backend (`IForgeMutations.ResolveGateAsync`, ADR
0019) and needs the fewest new controls: one node-id entry, two buttons,
one result label. `sprint.manage` needs four verbs' worth of controls;
`attempt.supersede` needs an instruction-file-equivalent text entry and a
way to select/display the target attempt. Smallest well-scoped slice,
matching every prior slice's own sizing rationale.

### Flat page, not a navigation shell, for this slice

`capabilities.json`'s `desktop` field names `"Review/ResolveGate"` for
this capability, which reads as implying a dedicated "Review" page/area.
No navigation framework exists anywhere in `Forge.Desktop` today — no
`Shell`, no `NavigationPage`, not even unused scaffolding; `App.CreateWindow`
wraps a single `ContentPage` directly. Every capability implemented so far
(`project.snapshot`, `project.initialize`, `configuration.manage`,
`provider.health`) lives on that one flat page, and `SurfaceParityTests`
checks control names against `MainPage.xaml`/`.xaml.cs` directly with no
concept of "which page" a control lives on — nothing in the existing test
suite or contract requires a separate page today. Building a real
navigation shell is a substantial, separate design decision (routing,
per-page view models, how attention navigation actually prioritizes
gate/blocked/failed sprints) that deserves its own slice rather than being
bundled into "add two buttons for one capability." The gate controls
therefore land on `MainPage` alongside everything else, following the
established pattern exactly; a navigation shell remains explicitly
deferred (see below), and `capabilities.json`'s `desktop` field is
unchanged since it already only names an area, not a literal page class.

### The sprint-id entry is reused, not duplicated

`MainPage` already has a `SprintIdEntry` used to select which sprint's
tree/detail to expand (`project.snapshot`). The new gate controls reuse it
as the gate's sprint context rather than adding a second sprint-id field —
one page, one sprint in view at a time, matching how the CLI's own
`--sprint` option means the same sprint across every command. A new
`GateNodeIdEntry` is added for the node id, empty meaning the canonical
`human_approval` node — the same default `forge gate approve|reject`
applies when `--node` is omitted (`MainPageViewModel.ResolveGateAsync`
substitutes `ImplementationCriticalGraphBuilder.HumanApprovalNodeId`
exactly where `CliApplication`'s own gate command does).

### Confirmation is the dialog's answer, never bypassed — matching the CLI's no-bypass rule

ADR 0019: `workflow.review`'s confirmation is mandatory and never
config-bypassable, unlike `RecoverAsync`/`InitializeAsync`'s own Desktop
confirmation dialogs (which pass a real yes/no through with no separate
bypass path either, but back mutations that already allow one at the
`ForgeApplication` layer). The gate buttons follow the exact same
dialog-then-pass-through shape already established by
`MainPage.RecoverAsync`: `DisplayAlertAsync` returns the user's real
answer, and that boolean is the `confirmed` argument
`MainPageViewModel.ResolveGateAsync` forwards to
`IForgeMutations.ResolveGateAsync` — there is no code path that could set
`confirmed: true` without the dialog itself returning it, so the
"never bypassed" property holds by construction, not by a separate check.

### An unparsable/missing sprint id is a reported failure, not a silent fallback

`RefreshAsync`'s existing handling of a malformed `--sprint`-equivalent
already established the precedent (`sprintMalformed` reports
`SprintNotFound` rather than silently expanding the active sprint).
`MainPageViewModel.ResolveGateAsync` follows it: `Guid.TryParse` failure
returns `GateResolutionFailed` with `DiagnosticCodes.SprintNotFound`
*before* resolving mutations or calling the Host at all — matching
`CliApplication`'s own `--sprint` parse-failure path, which also never
reaches `IForgeMutations`.

## Deliberately deferred

- **A real navigation shell / attention navigation.** See "Flat page, not
  a navigation shell" above. Still the largest remaining piece of this
  item; a future slice's own scope.
- **`sprint.manage`/`attempt.supersede` Desktop controls.** Not part of
  this slice; `CapabilityIds.Implemented` gains only `workflow.review`
  here.
- **Every other Desktop-absent capability** (`control.events`,
  `project.sync_validate`, `integration.skill`, `observability.inspect`,
  `diagnostics.bundle`, `quality.evaluate`) — most have no backend caller
  at all yet, unrelated to this slice.
- **ICU plural/select localization, a language-pack loader, and further
  accessibility work** beyond the existing `Describe(...)` pattern this
  slice reuses — out of scope, matching every prior slice.

## Consequences

- `CapabilityIds` gains `WorkflowReview`; `CapabilityIds.Implemented`
  gains it too — the trigger for `SurfaceParityTests` to require Desktop
  parity, now satisfied.
- `MainPage.xaml` gains `GateNodeIdEntry`, `GateApproveButton`,
  `GateRejectButton`, `GateResultLabel`; `MainPage.xaml.cs` wires them
  (button text, `Describe(...)` for the entry, click handlers) and adds
  `ResolveGateAsync`, mirroring `RecoverAsync`'s confirm-then-call shape.
- `MainPageViewModel` gains `ResolveGateAsync`, following
  `RecoverAsync`/`SetConfigurationAsync`'s existing
  resolve-mutations/`UseMutationsAsync`/`Message(...)` pattern exactly.
- New message keys: `GateResolutionFailed`, `GateNodeIdLabel`,
  `GateApproveAction`, `GateRejectAction` (en/ru). `GateResolved` already
  existed from the CLI slice and is reused for the success case.
- No new diagnostic codes — `DiagnosticCodes.SprintNotFound` is reused for
  an unparsable sprint id, matching the CLI's own behavior.

## References

- ADR 0005 (local Host and control plane)
- ADR 0019 (human-gate and attempt-supersession CLI commands — the
  `workflow.review` backend this slice's Desktop controls call)
- `tests/Forge.Tests/Acceptance/SurfaceParityTests.cs` (the parity gate
  this slice satisfies for `workflow.review`)
