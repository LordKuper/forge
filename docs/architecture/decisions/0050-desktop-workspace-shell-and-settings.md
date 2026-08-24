# ADR 0050: Desktop workspace shell and settings

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.9.0 (unchanged)

## Context

`docs/plans/desktop-workspace-redesign.md` section 11 Slice 5 replaces Desktop's monolithic
`MainPage` with the two-panel workspace shell, sidebar, project overview, and Forge/project settings
pages plan sections 4 and 5 describe. Slices 1-4 already shipped every read model and backend
capability this slice consumes (`ProjectCatalogStore`, `ProjectWorkspaceSummary`, `AvailableAction`,
`ForgeApplication.SetConfigurationAsync`); this ADR records the presentation-layer and Windows-adapter
decisions Slice 5 itself had to make.

## Decisions

### `MainPageViewModel` stays a shared service; new view-models delegate to it rather than absorbing its methods

Plan section 9.3 lists five new view-models. Rather than splitting `MainPageViewModel`'s 774 lines of
already-reviewed, already-tested capability code across them, each new view-model holds a
`MainPageViewModel` instance and forwards to it:

- `ProjectOverviewViewModel` forwards initialize/recover/create/run/resume/cancel-sprint.
- `ProjectSettingsViewModel` forwards recover, integration inspect/install/remove; it calls
  `IForgeMutations.SetConfigurationAsync` directly (the same interface method the previous
  monolithic page's own generic configuration editor called) rather than through that editor's
  text-rendering wrapper, so a settings save gets a structured `ConfigurationWriteResult` instead of
  a rendered string. That generic editor (`MainPageViewModel.SetConfigurationAsync`) has since been
  deleted as dead code -- see the finding-11 update below.
- `SprintWorkspaceViewModel` forwards every gate/confirm/test-work/finalize/supersede/poll-events/
  lifecycle method, converting a route's `Guid` sprint id to `MainPageViewModel`'s own string-id
  convention (`"D"` format) at the boundary.

This is not a workaround: `MainPageViewModel`'s own tests (`MainPageViewModelTests`,
`DesktopAndCliRenderTheSame*ForOneSnapshot` in `SurfaceParityTests`) exercise it directly and remain
valid unchanged, and every capability plan 12.1 requires stays reachable through a typed control
without re-deriving or re-testing its already-covered behavior. `ForgeSettingsViewModel` and
`SidebarViewModel` are the two exceptions: they hold `ForgeApplication`/`ProjectCatalogStore`
directly, because user-scope configuration and the catalog never route through a project's Host (ADR
0005/0049) and gain nothing from the delegation.

PR #98 review round 1 finding 11 corrected an overstatement here: `MainPageViewModel`'s own generic
scope/key/value `SetConfigurationAsync` -- the previous monolithic page's one-size-fits-all
configuration editor -- has no call site left anywhere in `src/` once `ForgeSettingsViewModel`/
`ProjectSettingsViewModel` call `ForgeApplication`/`IForgeMutations.SetConfigurationAsync` directly
(as this ADR already documents two paragraphs above). Every one of `ConfigurationRegistry`'s 6
user-scoped and 4 project-scoped keys is covered by a typed control on the Forge/project settings
pages, so no user-facing capability was lost; only this one generic internal method became dead code,
and it was deleted (AGENTS.md: remove stale content) rather than left as an untested, unreachable
surface.

### Folder-picker port lives in `Forge.Desktop.Presentation`; the Windows implementation lives in `Forge.Desktop` itself, not a new adapter project

`IFolderPickerPort` (one method, `PickFolderAsync`) is the neutral port plan section 4.1 asks for.
`WindowsFolderPicker` implements it in `Forge.Desktop` directly, calling `Windows.Storage.Pickers.FolderPicker`
and `WinRT.Interop.WindowNative`/`InitializeWithWindow`. `Forge.Desktop` already carries
`ForgeOsAdapter=true` *and* is a composition root (`OutputType=WinExe`) — `ArchitectureTests.LeafOsAdaptersOnlyReferenceNeutralProjects`/
`LeafOsAdaptersAreNamedForTheirOperatingSystem` both exempt composition roots, matching how
`MauiProgram.cs` already calls `ForgeRuntimeWindowsAdapter.Install()` and references the Windows
provider adapters directly. A separate `Forge.Desktop.Windows` leaf-adapter project would only be
warranted if something other than the Desktop composition root itself needed to consume the port's
Windows implementation, which nothing does. `WindowsFolderPicker` resolves the native window lazily
(`Func<Window?>`) because the real `Window` does not exist until `App.CreateWindow` constructs it,
which happens after the page (and therefore the picker) is constructed.

No adapter-level test exists for `WindowsFolderPicker`: `tests/Forge.Tests` never references
`Forge.Desktop` at all (only `Forge.Desktop.Presentation`), so there is no existing infrastructure to
run a Windows-UI-affecting test against it, and this slice does not add one. The neutral-side contract
(`IFolderPickerPort` itself, and every view-model that consumes it) is tested with `FakeFolderPicker`.

### Live UI-language switch: a mutable `SurfaceTextProvider`, not a new localization mechanism

`SurfaceText` (ADR-predating this one) is an immutable snapshot bound to one culture — every existing
surface resolves its whole run against one fixed language. Plan 5.1/12.2 requires a saved
`language.ui` change to apply without restart. `SurfaceTextProvider` wraps `ILocalizationCatalog` plus
the *current* `SurfaceText`, exposing `Resolve`, `Current`, and a `Changed` event;
`ForgeSettingsViewModel.SaveAsync` calls `SetLanguage` exactly when `language.ui` was part of the
saved edit set. `WorkspaceShellPage` subscribes once: on `Changed` it rebuilds every
`MainPageViewModel`-backed view-model (they each close over one fixed `SurfaceText`) and re-renders
the sidebar and content area. This is the minimal change the plan asked for — no second localization
mechanism, no restart, and no change to `ILocalizationCatalog`/`SurfaceText` themselves.

PR #98 review round 1 finding 1 found that both reactive paths above (`RouteChanged` and `Changed`)
were silently no-ops: each is always raised synchronously from inside the very click handler whose
own mutation guard (`WorkspaceShellPage.RunAsync`, "a second click cannot re-enter one while the
first is in flight") is still held, so the render it triggered found the guard already busy and
returned immediately — a sidebar navigation click updated `WorkspaceViewModel.Route` but the content
pane never rebuilt, and a language save never refreshed the sidebar. The fix keeps the guard's
original purpose (mutation re-entrancy) but extracts it into `Forge.Desktop.Presentation`'s new
`ShellRenderGate`, which tracks a *pending* sidebar/content render separately from the busy mutation
and flushes it, once, the moment the guard releases, instead of dropping it. Moving it to neutral code
(rather than fixing it in-place in `Forge.Desktop`) also makes it directly unit-testable
(`ShellRenderGateTests`), which the ADR's own "no MAUI control can be instantiated headlessly" limit
otherwise would have prevented for this exact regression.

### No XAML data binding; every page keeps the previous page's "build controls, assign `.Text` in code" idiom

`ArchitectureTests.HostsDoNotContainHardCodedLabelText` asserts no `Forge.Desktop` XAML file contains
the literal substring `Text="` — which also rejects `Text="{Binding ...}"`. Combined with the shell's
genuinely dynamic content (a variable number of sidebar rows, provider toggles, sprint cards), this
slice keeps `MainPage.xaml.cs`'s own established idiom: XAML declares only the fixed skeleton
(`WorkspaceShellPage.xaml`'s two-column `Grid`), and every page/section builds its controls in
code-behind, assigning `.Text` from `SurfaceTextProvider.Resolve` and view-model data directly. The
previous single 605-line code-behind file becomes one `partial class WorkspaceShellPage` split across
five files by concern (routing/sidebar, Forge settings, project overview, project settings, sprint
workspace) — organization, not a new pattern.

### Every human-only and destructive action still shows a confirmation dialog naming its exact target

Reusing `MainPageViewModel`'s methods (per the decomposition above) makes it easy to pass a
hardcoded `confirmed: true` and skip the dialog entirely; this would silently defeat ADR 0005/0018/0037's
"the human never bypasses this by accident" requirement, since the backend cannot distinguish a real
answer from a hardcoded one. `WorkspaceShellPage.SprintWorkspace.cs`/`ProjectOverview.cs`/
`ProjectSettings.cs` therefore reproduce the previous page's dialog-per-action shape exactly: gate,
supersede, confirm, test-work, and finalize (ADR 0037's human-only capabilities) show a dialog built
from the corresponding `*Prompt` method and never call the mutation at all when the user declines;
cancel-sprint, recover, and integration install/remove (ordinarily bypassable) still show a dialog and
pass its literal answer through as `confirmed`, exactly like the page they replace.

### Sprint-workspace route: a functional-but-unstyled page, not an empty stub

Plan section 11 Slice 6 owns the sticky status header, virtualized timeline, and typed
contextual-action renderer (plan 4.3) — genuinely out of this slice's scope. But every capability
`MainPageViewModel` already exposed must remain reachable (plan 12.1), and several of them (gate,
confirm, test-work, finalize, supersede, poll events) have no other page to live on until Slice 6
ships its own contextual-action UI. `RenderSprintWorkspaceAsync` therefore renders the same raw
controls the previous page did, through `SprintWorkspaceViewModel`, scoped to the route's already-known
project root and sprint id (so unlike the previous page, at least those two fields are never
re-entered). Node/attempt ids remain free-text entries — removing those, too, is explicitly Slice 6's
job ("remove manual ID fields from ordinary workflows").

### Deterministic sprint ordering is one shared, independently tested rule

Plan 4.1's ordering ("human attention, running, paused, blocked or failed, then other non-terminal
sprints by descending creation sequence") cannot reuse `SprintWorkspaceSummary.AttentionRequired`
directly: that field is a coarse "needs a human to look at this" signal (ADR 0049) that also covers
`Blocked`/`Failed`, which the plan's own ordering places in a *later* bucket than true human-decision
states (`AwaitingHuman`/`ReadyToFinalize`). `SprintOrderingRank` ranks directly off `SprintState`
instead, shared by `SidebarViewModel` and `ProjectOverviewViewModel` so the two surfaces can never
silently disagree on one sprint list's order.

### Settings atomicity is validate-then-write-only-changed-keys, not a new transactional store

Plan 5.1/12.2 requires "Save validates the full edit set and writes it atomically" and "invalid edits
cannot be saved and do not partially modify configuration." `ConfigurationRegistry`/`IConfigurationStore`
(Slice 1 and earlier) write one key at a time with no cross-key transaction primitive, and adding one
is out of this slice's scope. `ForgeSettingsViewModel.SaveAsync`/`ProjectSettingsViewModel.SaveAsync`
therefore: validate the entire edit set first (mirroring every server-side rule the write path itself
enforces — supported languages, known provider ids, a positive token budget) and write nothing at all
if any check fails; only then write the keys that actually changed, stopping at the first failure. A
genuine partial write is possible only if a value this validation accepted is rejected moments later
by a concurrent external edit — treated as an accepted, rare residual risk, not a normal path, and
consistent with `catalog.json`'s own already-accepted "no cross-process locking" posture (ADR 0049).

Known limitation (PR #98 review round 1, noted but not blocking): when that rare mid-sequence write
failure does occur, `ForgeSettingsViewModel.SaveAsync`/`ProjectSettingsViewModel.SaveAsync` both
report `MessageKeys.SettingsValidationFailed` ("some values are invalid; nothing was saved"), even
though one or more earlier keys in the write set may already have landed durably. The wording
overstates the rollback the atomicity model above does not actually provide for this residual case.
Left uncorrected for now because the case requires a concurrent external edit to trigger (accepted
above as exceptionally rare) and a more accurate message needs its own reviewed copy; a future change
should either add a distinct partial-failure message or make the write set genuinely atomic.

### `workspace.summary`/`workspace.available_actions` stay reserved capabilities even though Desktop now renders them

`SidebarViewModel`/`ProjectOverviewViewModel` call `ForgeApplication.GetWorkspaceSummaryAsync`/
`GetAvailableActionsAsync` directly — the same local, in-process call every other read
(`GetProjectSnapshotAsync`, `GetOverviewAsync`) already uses, never a remote Host round-trip. ADR
0049's "wait for Desktop parity" reservation was specifically about the `ControlProtocol`-negotiated
capability surface for a *remote* Host connection; nothing in this slice adds Desktop-to-remote-Host
traffic for either query, since project catalog reads/writes and workspace-summary/available-actions
reads never mutate `.forge/` state (ADR 0005 routes only mutations through a Host). Promoting
`CapabilityIds.WorkspaceSummary`/`SprintTimeline`/`WorkspaceAvailableActions` to
`CapabilityIds.Implemented` — updating `capabilities.json`'s `desktop` field, bumping its contract
version, and widening every capability-parity test that keys off `CapabilityIds.Implemented` — is a
real, but separable, follow-up left to a maintainer or a later slice; it does not gate this slice's own
functional correctness, since the reserved status never blocked local, in-process consumption.

## What stays deferred

- Slice 6: sprint workspace's sticky status header, virtualized timeline, typed contextual-action
  renderer, stop-current-operation and stage-transition UI, notification deep links, and removing the
  remaining manual node/attempt id fields.
- Slice 7: real provider/account quota data (the sidebar's status row reports it as not yet available,
  never fabricated).
- A save-file port for persisting a generated diagnostic bundle to disk (`ProjectSettingsViewModel.GenerateDiagnosticBundleAsync`
  returns the JSON for display; writing it to a user-chosen file needs its own port).
- Promoting `workspace.summary`/`sprint.timeline`/`workspace.available_actions` from reserved to
  implemented in `capabilities.json` (see above).
- Live visual, keyboard-navigation, screen-reader, and text-scaling verification of the new pages: no
  MAUI control can be instantiated headlessly in this repository's test suite, so this slice's own
  test coverage is limited to the neutral view-models (routing, ordering, validation, provenance,
  delegation) and to static text-based checks against the Windows adapter's source
  (`SurfaceParityTests`), matching the previous page's own established testing discipline.

## Addendum: collapsible sidebar completes this ADR's own deferred scope

A post-release audit found that plan section 2's "the first UI layout is a fixed two-panel
workspace **with a collapsible sidebar**" was never actually implemented: `WorkspaceShellPage.xaml`
shipped the fixed `280,*` grid with no collapse control anywhere. This addendum records the two
decisions closing that gap, without a new ADR number, since it completes this ADR's own original
scope rather than introducing a new one.

### Collapse state lives in the existing local user-scope configuration store, not `ProjectCatalogStore` or a new file

Whether the sidebar is collapsed is a Desktop-instance-level UI preference: it applies to the whole
installation, not to any one project. `ProjectCatalogStore` was considered and rejected -- despite
being described as "Desktop-installation-local persistence," every field on
`ProjectCatalogEntry` is keyed by a specific project's own `ProjectId`, so a project-agnostic
preference has no natural row to live on there (and adding one to the top-level `Persisted` wrapper
instead of an entry would create a second, differently-shaped persistence convention in the same
file for no benefit).

Instead, this reuses the mechanism `ForgeSettingsViewModel` already established for exactly this
kind of state: a new `ConfigurationRegistry` key (`shell.sidebar_collapsed`, `ConfigurationScope.User`,
default `false`, no session override -- same shape as `notifications.enabled`), written and read
through `ForgeApplication.SetConfigurationAsync`/`GetUserConfigurationAsync` directly, never through
a project's Host (ADR 0005/0049's existing "user-scope configuration stays local" rule). This is a
genuinely public, documented contract addition, not an internal implementation detail: `docs/contracts/v1/configuration.json`
gains the new key (`contract_version` 1.4.0 -> 1.5.0), `docs/contracts/v1/schemas/user-config.schema.json`
gains the corresponding `shell.sidebar_collapsed` property and a `1.3.0` `schema_version` entry, and
`ConfigurationSchemaCodec`'s `UserContractVersion` moves to `1.3.0` in step -- the same three-file
pattern `notifications.enabled` established (ADR 0024).

`SidebarViewModel.LoadAsync` now also reads this key (one extra `GetUserConfigurationAsync` call,
no new Host round-trip) and returns it on `SidebarSnapshot.Collapsed`; `SidebarViewModel.SetCollapsedAsync`
writes it. Both are exercised directly by real, file-backed round-trip tests
(`SidebarViewModelTests`) rather than a live MAUI process, matching this ADR's own established test
discipline for `Forge.Desktop.Presentation`.

### Collapsed is an icon-only rail rendered by the same `RenderSidebarAsync`/`ShellRenderGate` path, not a second render pipeline

`WorkspaceShellPage.RenderSidebarAsync` now also sets `ShellGrid.ColumnDefinitions[0].Width` (280
expanded, 56 collapsed) and always renders one new toggle button first; when collapsed, it returns
immediately after that button, so the rail keeps its own re-expand affordance instead of vanishing
entirely. The toggle's `Clicked` handler calls `SidebarViewModel.SetCollapsedAsync` then
`RenderSidebarAsync` from inside `RunAsync`, the exact same `ShellRenderGate`-backed idiom every
other sidebar-mutating control here already uses (add/remove project, Forge settings) -- deliberately
not a new render path, given `ShellRenderGate` itself exists because two earlier PRs (#98, #99) had
to fix bugs from exactly that mistake. The toggle's accessible name
(`MessageKeys.SidebarCollapseAction`/`SidebarExpandAction`, English and Russian) flips with the
state it describes, so the state change is conveyed by more than a bare column-width change a screen
reader cannot perceive (plan 12.6).

### What stays deferred

Live visual, keyboard-navigation, and screen-reader verification of the toggle and the collapsed
rail is not possible here either, for the same reason this ADR's own "what stays deferred" section
already gives: no MAUI control can be instantiated headlessly in this repository's test suite. Test
coverage is limited to `SidebarViewModel`'s collapse state and its real persistence round-trip.

## Consequences

- `Forge.Desktop.Presentation` gains `SurfaceTextProvider`, `WorkspaceRoute`/`WorkspacePage`,
  `IFolderPickerPort`, `SprintOrderingRank`, `ProjectDisplayName`, `SidebarViewModel`,
  `WorkspaceViewModel`, `ProjectOverviewViewModel`, `ForgeSettingsViewModel`,
  `ProjectSettingsViewModel`, `SprintWorkspaceViewModel`, and `ShellRenderGate` (PR #98 review finding
  1). `MainPageViewModel` loses its now-dead generic `SetConfigurationAsync` (finding 11); otherwise
  unchanged.
- `Forge.Desktop` replaces `MainPage.xaml`/`.xaml.cs` with `WorkspaceShellPage.xaml` and five
  partial-class code-behind files, and gains `WindowsFolderPicker.cs`. `App.xaml.cs` constructs the
  new page instead of `MainPage`.
- `Forge.Localization` gains the new Slice-5 message keys (English and Russian) `SurfaceParityTests`
  and `LocalizationCatalogTests.BuiltInCatalogsHaveIdenticalKeys` both cover.
- `SurfaceParityTests` (`tests/Forge.Tests/Acceptance`) drops its `MainPage.xaml`-text-scanning
  capability-to-control map and per-`Entry` regex extraction in favor of scanning
  `WorkspaceShellPage*.cs`'s combined source for the equivalent view-model method calls and an
  aggregate Entry/Picker-to-naming-call count; the dialog-naming and blank-field-guard-ordering checks
  now anchor on the new files' local functions/lambdas instead of named methods.
- `VERSION` moves to `0.67.0` (MINOR: new Desktop capability, no breaking contract change).

## References

- Plan sections 4, 5, 9.3, 9.4, 11 (Slice 5), 12.1, 12.2
- ADR 0043/0049 (the read models this slice consumes; the reserved-capability reasoning this ADR
  narrows for local, in-process reads)
- ADR 0005 (Host-as-sole-writer; mutation routing `ProjectSettingsViewModel`/`ForgeSettingsViewModel`
  both follow)
- ADR 0037 (human-only confirm/test-work/finalize capabilities whose dialogs this slice preserves)
- AGENTS.md Portability section (the OS-adapter boundary `WindowsFolderPicker` and `Forge.Desktop`
  itself satisfy)
