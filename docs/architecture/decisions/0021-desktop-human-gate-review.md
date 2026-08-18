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

Independent review found that this property, while true, had **no test
verifying it**: `FakeForgeMutations.ResolveGateAsync` discarded `approved`/
`confirmed` entirely, so a hardcoded `true, true` in
`MainPageViewModel.ResolveGateAsync` — silently deleting the "never
bypassed" property, and making a `Reject` button that actually approved —
would have left the whole suite green. The same class of gap ADR 0019's own
review found and fixed on the CLI slice. Fixed by recording both values on
the fake and asserting them, plus real end-to-end local-fallback tests for
reject (asserting the node's resulting durable state is `Failed`, the
mirror of the existing approve test) and for an unconfirmed decision
(asserting `ConfirmationRequired` and that the node's state is unchanged).

Review also found declining the dialog still reached `IForgeMutations` —
for an initialized project, `RemoteForgeMutations`, meaning cancelling
still resolved (and could launch) a Host connection and round-tripped a
real `resolve_gate` request only to be told `confirmation_required`.
Fixed by short-circuiting on `!confirmed` before `MainPageViewModel` is
ever called, matching `MainPage.InitializeAsync`'s already-correct shape
exactly (a dedicated `GateConfirmationRequired` message, no mutation call).

### A blank sprint id targets the active sprint, matching the page's other reader

`RefreshAsync`'s existing handling already establishes that a blank
`SprintIdEntry` means "the active sprint", not "no sprint" —
`sprintRequested` only becomes true for a genuinely non-blank value, and a
*non-blank but unparsable* one is reported as `SprintNotFound` rather than
silently falling back. `MainPageViewModel.ResolveGateAsync` originally
diverged from this: any blank value (the page's own default state, with
the active sprint's tree already visible and its gate node showing
`awaiting_human`) failed with `SprintNotFound`, meaning the one moment the
page most invites approving/rejecting a gate was exactly the moment doing
so was guaranteed to fail. Independent review found this and it was fixed
before merge: a blank `sprintId` now resolves the active sprint via
`ForgeApplication.GetProjectSnapshotAsync(...).ActiveSprintId`, while a
non-blank, unparsable value is still reported the same way `forge gate
approve|reject` reports an unparsable `--sprint` — matching `RefreshAsync`'s
own rule exactly instead of a rule unique to this one action.

`StatusAdvisor.DetermineActiveSprint` returns `null` for two materially
different reasons (ADR 0005: "the active sprint is an explicit selection or
the only non-terminal sprint; Forge never silently chooses among multiple
candidates") — zero non-terminal sprints, or more than one. A second review
round found the first fix above collapsed both to `SprintNotFound`, so a
project with several running sprints and a blank entry was told "not
found" while those very sprints were rendered in the tree directly above
the button — wrong information, not merely terse, and with no hint that
typing an id was what the action needed. Fixed by distinguishing the two:
`ResolveSprintIdAsync` now also counts non-terminal sprints when
`ActiveSprintId` is `null`, and `ResolveGateAsync` reports a dedicated
`GateSprintAmbiguous` message (no diagnostic code — this is resolved
entirely client-side, before `IForgeMutations` is ever reached) instead of
`SprintNotFound` when more than one exists. Both branches (genuinely none,
and more than one) now have their own regression test.

### The confirmation dialog names its target, and a stale result never survives an unrelated refresh

Two further review findings, both fixed before merge:

- The dialog originally passed the same action name as title, message, and
  accept button (`DisplayAlertAsync(action, action, action, cancel)`), so a
  user could not tell from it which sprint or node they were about to
  approve/reject — for an irreversible human decision, a materially worse
  confirmation than `InitializeAsync`'s own dialog (which already shows the
  project root being acted on). Fixed with `MainPageViewModel.GatePrompt`,
  which names both the sprint (or the active-sprint placeholder, avoiding
  an extra round-trip just to resolve and display it) and the effective
  node id, applying the identical defaulting rules `ResolveGateAsync`
  itself uses.
- `GateResultLabel` was assigned only inside the gate flow and never reset
  by `RefreshAsync`, so a decision's outcome text survived every later,
  unrelated refresh — a stale "Gate resolved." could sit next to a
  different sprint's tree or a different project root. Fixed by clearing
  it inside `RefreshAsync` (so any *other* trigger — the Refresh button, a
  changed `SprintIdEntry`/`ProjectRootEntry`, `OnAppearing` — clears it)
  and re-ordering `MainPage.ResolveGateAsync` to set the label *after*
  calling `RefreshAsync`, so the decision just made still displays its own
  outcome correctly.

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
  `GateApproveAction`, `GateRejectAction`, `GateConfirmationRequired`,
  `GateActiveSprintPlaceholder`, `GateSprintAmbiguous` (en/ru). `GateResolved`
  already existed from the CLI slice and is reused for the success case.
- No new diagnostic codes — `DiagnosticCodes.SprintNotFound` is reused for
  an unparsable sprint id or a genuinely absent active sprint, matching the
  CLI's own behavior. The distinct "more than one non-terminal sprint"
  case (see "A blank sprint id targets the active sprint" above) carries
  no diagnostic code at all: it is resolved entirely client-side, before
  `IForgeMutations` is ever reached, so there is nothing for a wire-level
  code to describe.

## References

- ADR 0005 (local Host and control plane)
- ADR 0019 (human-gate and attempt-supersession CLI commands — the
  `workflow.review` backend this slice's Desktop controls call)
- `tests/Forge.Tests/Acceptance/SurfaceParityTests.cs` (the parity gate
  this slice satisfies for `workflow.review`)
