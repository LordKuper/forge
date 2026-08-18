# ADR 0025: Desktop control-events polling

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66's Desktop parity work continues as its own sequence
of slices (ADR 0021, ADR 0022). Both prior slices landed a human-only
mutation (`workflow.review`, `attempt.supersede`) and, each time, spent
several review rounds hardening a confirmation dialog's own
never-bypassed property and its sprint-targeting edge cases. This slice
covers `control.events` instead: a read-only query already fully built for
the CLI (`forge events`, backed by `ForgeApplication.ReadControlEventsAsync`/
`ControlEventsReader`) with zero Desktop counterpart.

`sprint.manage` remains blocked exactly as ADR 0022 left it — its
`capabilities.json` entry documents a `rebase` subcommand
`SprintOrchestrator` has no backend for. Every other Desktop-absent
capability (`project.sync_validate`, `integration.skill`,
`observability.inspect`, `diagnostics.bundle`, `quality.evaluate`) has no
CLI/backend implementation at all yet — building any of them would be a
new feature, not a Desktop-controls slice. `control.events` is the only
remaining capability with a fully-built backend and CLI surface and
nothing on Desktop: the correct next smallest well-scoped target.

## Decisions

### A query needs neither a confirmation dialog nor a Host round-trip

`capabilities.json` documents `control.events`'s permission as
`read_redacted`, not one of the `*_confirm` permissions `workflow.review`/
`attempt.supersede` carry. `MainPageViewModel.PollEventsAsync` therefore
calls `ForgeApplication.ReadControlEventsAsync` directly — the same
application instance `RefreshAsync` already reads from — rather than
`resolveMutations`'s Host-routing path, which exists specifically for
`.forge/` mutations (ADR 0005). Nothing is confirmed, nothing can be
declined, and no dialog is shown; the button's own click is the entire
interaction.

### Rendering is shared with the CLI, not duplicated

`CliApplication.WriteEvents`'s per-record formatting is extracted into
`SurfaceFormatting.EventLines(SurfaceText, ControlEventsPage)`, matching
the existing precedent `SprintTreeLines`/`SprintDetailLines` already set —
both surfaces render the *same* projection, so they cannot silently drift
the way two independently-written formatters could. `WriteEvents` is now a
three-line wrapper over the shared method.

### The page instance owns one stored cursor, mirroring the CLI's own local variable

`forge events --follow` keeps a `cursor` variable in its polling loop,
advancing it after every read (`ADR 0005`: "no subscriber registry, no
streaming socket"). Desktop's `MainPage` instance is the direct analogue —
it lives for as long as the user has the page open, the same lifetime a
CLI `--follow` invocation's loop variable has for as long as the process
runs. `MainPageViewModel` gains two private fields, `eventsCursor` and
`eventsCursorProjectRoot`, rather than threading cursor state through the
button click's own signature (there is nowhere else for it to live between
clicks). A poll's own diagnostic code (most notably
`DiagnosticCodes.ControlCursorStale`) is rendered via the existing
`Message()` helper exactly as every other capability's diagnostics are,
and needs no dedicated recovery path: `ControlEventsReader` already
returns a fresh, immediately-usable anchor cursor for a rejected one
(`ControlEventsPage.Empty`), and redisplaying an event a user has already
seen has no side effect to protect against here — unlike a delivered
notification (ADR 0024), a rendered list has nothing to dedup against.

### The stored cursor resets on a project-root switch

A cursor's watermarks describe one project's sprints; reusing them against
a different project root is meaningless (structurally decodable, since the
format carries no project identity, but semantically stale). `PollEventsAsync`
compares the requested `projectRoot` against the root its stored cursor was
last read against and resets to a fresh cursor on any change.

`EventsLabel` needs the same reset, but *not* the same trigger
`GateResultLabel`/`AttemptSupersedeResultLabel` use. Those two labels carry
a one-shot mutation's own outcome with no companion state, so clearing them
unconditionally on every `RefreshAsync` call (routine `Refresh`, page
`OnAppearing`, or the implicit refresh after an unrelated action) is
correct — a decision's own result should never survive past the next
refresh regardless of cause. `EventsLabel` is different: it is a live view
of the view model's own stored cursor, which does *not* reset on an
ordinary refresh, only on a project-root switch. Round 1 review caught the
first version of this slice clearing `EventsLabel` unconditionally anyway
(copying the other two labels' own rule verbatim without checking whether
it actually applied), which meant a poll's rendered page never survived a
single subsequent `Refresh` click. Fixed by gating the clear on the same
project-root-switch condition the cursor itself resets on — tracked in
`MainPage`'s own code-behind (`lastPolledEventsProjectRoot`), since the
view model's cursor state is not otherwise exposed to the page.

### No `--follow` equivalent on Desktop

The CLI's `--follow` is a bounded, non-streaming poll loop with a
one-second `Task.Delay` between reads — a manual re-click of
`EventsPollButton` is the direct Desktop equivalent of one iteration of
that same loop, not a gap to close. Building an automatic timer would add
a second polling mechanism alongside the CLI's own, for no capability the
CLI itself does not already lack (nothing here needs push delivery; ADR
0024's own notification sweep already covers the "the user is not looking
at the app" case this capability does not need to solve again).

## Round 1 review

Independent review found two issues, both fixed:

1. **`RefreshAsync` cleared `EventsLabel` unconditionally**, copying
   `GateResultLabel`/`AttemptSupersedeResultLabel`'s own reset rule without
   checking whether it actually applied to a label with companion cursor
   state — see "The stored cursor resets on a project-root switch" above
   for the fix and why the two labels' reset conditions are not actually
   the same. A new static test, `RefreshAsyncOnlyClearsEventsLabelWhenTheProjectRootChanged`,
   pins the fix directly in the code-behind text (no MAUI control can be
   instantiated headlessly in this suite, the same constraint every other
   code-behind-text assertion in `SurfaceParityTests` already works
   around).
2. **`EventLines`'s extraction had no test proving the CLI and Desktop
   actually render identically.** Sharing `SurfaceFormatting` is not
   itself the no-drift guarantee this ADR claims — either surface can
   still wrap, reorder, or filter the shared lines on its way to the
   screen, exactly the caveat `SurfaceParityTests.DesktopAndCliRenderTheSameSprintTreeAndDetailForOneSnapshot`
   already states for its own capability but that this slice's own tests
   never extended to `control.events`. Fixed with a new
   `DesktopAndCliRenderTheSameEventsForOneSnapshot` test: drive one real
   event, render through both surfaces with diagnostics on a separate
   channel, and diff the text directly.

## Deliberately deferred

- **`sprint.manage` Desktop controls.** Unchanged from ADR 0022 — still
  blocked on `forge sprint rebase`, which needs its own design.
- **A real navigation shell / attention navigation.** Unchanged from ADR
  0021 — still the largest remaining piece of this item.
- **Every other Desktop-absent capability** (`project.sync_validate`,
  `integration.skill`, `observability.inspect`, `diagnostics.bundle`,
  `quality.evaluate`) — none have a backend implementation yet, unrelated
  to this slice.
- **ICU plural/select localization, a language-pack loader, and further
  accessibility work** — out of scope, matching every prior slice.
- **An automatic/timer-driven poll.** See "No `--follow` equivalent"
  above — a deliberate choice, not an oversight.

## Consequences

- `CapabilityIds` gains `ControlEvents`; `CapabilityIds.Implemented` gains
  it too — the trigger for `SurfaceParityTests` to require Desktop parity,
  now satisfied.
- `SurfaceFormatting` gains `EventLines`, shared by `CliApplication.WriteEvents`
  and `MainPageViewModel.PollEventsAsync`.
- `MainPage.xaml` gains `EventsPollButton`, `EventsLabel`; `MainPage.xaml.cs`
  wires them and clears `EventsLabel` inside `RefreshAsync`, gated on a
  project-root switch (see "Round 1 review") rather than unconditionally.
- `MainPageViewModel` gains `PollEventsAsync` and two private fields
  (`eventsCursor`, `eventsCursorProjectRoot`) holding this page instance's
  own poll progress.
- One new message key: `EventsPollAction` (en/ru). `EventsTitle`/`NoEvents`
  already existed from the CLI slice and are reused as-is via the shared
  `EventLines` projection.
- No new diagnostic codes — `ControlCursorStale` (already reserved) is the
  only one this capability can surface, rendered the same way every other
  capability's diagnostic is.

## References

- ADR 0005 (local Host and control plane — the bounded, cursor-driven,
  non-streaming event read this slice's Desktop control calls)
- ADR 0007 (cross-platform core and minimal OS adapters — `SurfaceFormatting`
  stays neutral; nothing here is OS-specific)
- ADR 0021 (Desktop human-gate review — the flat-`MainPage`,
  no-navigation-shell precedent this slice continues)
- ADR 0022 (Desktop attempt-supersession — the most recent prior slice in
  this same sequence, and the "considered `sprint.manage`, rejected it"
  precedent this ADR repeats)
- ADR 0024 (best-effort local notifications — the complementary
  push-delivery mechanism that makes an automatic Desktop poll unnecessary)
