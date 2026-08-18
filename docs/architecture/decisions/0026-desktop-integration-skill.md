# ADR 0026: Desktop integration-skill controls

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66's Desktop parity work continues as its own sequence
of slices (ADR 0021, ADR 0022, ADR 0025). This slice covers
`integration.skill`: previewing, installing, and removing the generated
provider agent integration (`CLAUDE.md`, `AGENTS.md`, etc. — ADR 0011),
wired to the existing `ForgeApplication.InspectIntegrationAsync`/
`IForgeMutations.InstallIntegrationAsync`/`.RemoveIntegrationAsync`
backend and CLI (`forge integration skill generate|install|remove`).

Every remaining Desktop-absent capability with an actually-built backend
has now landed a slice (`workflow.review`, `attempt.supersede`,
`control.events`, and now `integration.skill`); everything left
(`sprint.manage`, `project.sync_validate`, `observability.inspect`,
`diagnostics.bundle`, `quality.evaluate`) either has no backend at all yet
or is blocked on one, per ADR 0022's own accounting, unchanged here.

## Decisions

### `generate` is a query, `install`/`remove` are ordinarily-bypassable mutations — not human-only

`capabilities.json` lists `integration.skill`'s permission as
`integration_write_confirm`, an ordinary permission — unlike
`workflow.review`/`attempt.supersede`'s `human_gate_confirm`/
`human_attempt_supersede_confirm`. `InstallIntegrationAsync`/
`RemoveIntegrationAsync`'s own `confirmed` parameter may still succeed via
a configured `interaction.confirm_destructive` bypass even when the
dialog's own answer is `false` (ADR 0011) — the same shape
`RecoverAsync`'s existing Desktop control already has, and this slice's
`InstallIntegrationAsync`/`RemoveIntegrationAsync` view-model methods copy
that shape directly rather than `ResolveGateAsync`/`SupersedeAttemptAsync`'s
never-bypassed one. `GenerateIntegrationPreviewAsync` needs no
confirmation and no Host round-trip through `resolveMutations`, matching
`PollEventsAsync`'s own query shape (ADR 0025).

### Rendering is shared with the CLI from the start, not added reactively

ADR 0025's own review history showed, three times, that a shared
formatting helper without a same-slice parity test is not itself a
no-drift guarantee. This slice applies that lesson from the outset instead
of after a review round catches it: `CliApplication`'s
`CreateIntegrationGenerateCommand`/`CreateIntegrationWriteCommand` bodies
were extracted into two new `SurfaceFormatting` methods —
`IntegrationInspectionLines`/`IntegrationWriteLines` — used by both the
CLI and the new `MainPageViewModel` methods, and
`SurfaceParityTests.DesktopAndCliRenderTheSameIntegrationPreviewForOneSnapshot`
lands in the same commit as the extraction, not a later one.

### Every new async method captures `ProjectRoot` before its own request, proactively

ADR 0025's review history also found the same "`ProjectRoot` read again
after an `await`" TOCTOU shape twice, in two different methods, across
three review rounds. `GenerateIntegrationPreviewAsync`/
`InstallIntegrationAsync`/`RemoveIntegrationAsync`'s own code-behind
methods each read `ProjectRoot` (or capture it into `requestedRoot`) at
most once before their own request, applying that fix's shape from the
first commit rather than waiting for a review round to find it. Unlike
`control.events`, none of these three methods hold any cross-call state of
their own (no stored cursor, no tracking field), so there is no
`lastPolledEventsProjectRoot`-shaped desync risk to guard against here —
each call is a fully self-contained, one-shot request.

### `IntegrationLabel`/`IntegrationWriteResultLabel` follow `GateResultLabel`'s reset rule, not `EventsLabel`'s

Both new result labels carry a one-shot query or mutation outcome with no
companion state — the same shape `GateResultLabel`/
`AttemptSupersedeResultLabel` already have, not `EventsLabel`'s stored-cursor
shape. `RefreshAsync` therefore clears both unconditionally, alongside the
two existing unconditional clears, rather than gating them on any
condition.

### One shared write helper for install and remove

`MainPageViewModel.InstallIntegrationAsync`/`.RemoveIntegrationAsync` both
delegate to a private `WriteIntegrationAsync(projectRoot, confirmed, write,
cancellationToken)`, parameterized by which `IForgeMutations` verb to call
— mirroring `CliApplication.CreateIntegrationWriteCommand`'s own
CLI-side shape (`Func<IForgeMutations, string?, bool, CancellationToken,
Task<IntegrationWriteResult>>`), so the two install/remove code paths
cannot silently diverge from each other the way two hand-written copies
could.

## Round 1 review

Independent review found four issues, all fixed:

1. **The install/remove confirmation dialogs repeated the action name as
   their own message instead of naming a target.** Unlike `RecoverAsync`
   (a valid precedent for the *bypass* shape, but not for an uninformative
   dialog — there is nothing more specific than "startup" to name),
   `install`/`remove` write to and delete from the project's own working
   tree, the same destructiveness rigor `GateConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName`/
   `AttemptSupersedeConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName`
   already enforce for `workflow.review`/`attempt.supersede`. Fixed by
   reusing `MainPageViewModel.InitializePrompt`'s existing shape (resolved
   via `GetProjectSnapshotAsync`, the same call `InitializeAsync` already
   makes) rather than inventing a third, independent prompt scheme; pinned
   with a new static test mirroring the two existing dialog-naming checks.
2. **The new parity test's fixture was degenerate.** `TestEnvironment`'s
   only default provider (`fake`) has no matching generator anywhere in
   this neutral test composition — the real Claude/Codex generators live
   in Windows-only OS-adapter projects (ADR 0007) this suite never
   references — so both surfaces rendered only the constant
   `NoIntegrationArtifacts` string, and `IntegrationInspectionRow`/
   `AppendIntegrationDocumentErrors`, the only parts of the shared
   projection that can actually drift, were never exercised. Fixed by
   giving `TestEnvironment` a new optional `generators` parameter (same
   shape as its existing `llmProviders`/`providers` overrides) and a
   minimal in-file fake generator for the `fake` provider, then pinning
   that the compared text really carries a real artifact row. (Round 2
   review found this closed only the artifact-row half of the claim —
   `AppendIntegrationDocumentErrors` was still never reached; see "Round
   2 review" below.)
3. **`IntegrationWriteLines`, the mutating half and the one actually
   reachable from a destructive action, had no parity test at all** —
   the original PR covered only `generate`. Fixed with a second parity
   test, `DesktopAndCliRenderTheSameIntegrationWriteForOneSnapshot`, using
   two separate projects rather than one install-then-compare-a-second-
   install (a second install against the same target renders `unchanged`,
   not `written`, which would fail for a reason unrelated to what the test
   exists to prove).
4. **`IntegrationInspectionRow`/`IntegrationWriteRow` were left `public`**
   with no caller outside `SurfaceFormatting.cs` after the extraction.
   Narrowed to `private` (pre-1.0 contracts are freely replaceable); their
   doc comments, which claimed to be "shared with Desktop" directly, were
   corrected to point at `IntegrationInspectionLines`/`IntegrationWriteLines`
   as the actual shared, tested surface.

## Round 2 review

Independent review found one issue, fixed:

1. **Round 1 finding 2 was only half-closed.** Round 1 itself named two
   drift-capable parts of the shared projection — `IntegrationInspectionRow`
   *and* `AppendIntegrationDocumentErrors` — but the fake-generator fix
   only exercised the first. Both new parity tests used freshly
   initialized projects with no `.forge/rules` parse failures, so the
   `  ! {path} {code}` document-error rows this PR newly exposed to
   Desktop were never rendered by any test on either surface; a
   Desktop-side regression there would have left the full suite green.
   Fixed by writing a malformed rule document (missing frontmatter,
   matching `IntegrationGenerationTests`'s own fixture shape) into the
   preview parity test's project before generating, and pinning that the
   compared text carries the resulting error row alongside the artifact
   row.

## Round 3 review (final full-scope round)

Independent review found one issue, fixed:

1. **Round 2's own fix was itself asymmetric.** It added document-error
   coverage only to the preview (`generate`) parity test, but
   `IntegrationInstallationService.InstallAsync`/`.RemoveAsync` each run
   their own `InspectAsync` internally and propagate its `DocumentErrors`
   verbatim into `IntegrationWriteResult` — the write path renders the
   identical error row, on the verb actually reachable from a destructive
   action, and it was still untested there. Deleting `AppendIntegrationDocumentErrors`
   from `IntegrationWriteLines` would have left every test in the suite
   green, the exact shape round 2 itself rejected for the preview half.
   Fixed by writing the same malformed rule document into both parity
   projects in `DesktopAndCliRenderTheSameIntegrationWriteForOneSnapshot`
   and pinning the error row alongside the existing artifact-row/
   `written`-outcome assertions.

## Deliberately deferred

- **`sprint.manage` Desktop controls.** Unchanged from ADR 0022 — still
  blocked on `forge sprint rebase`, which needs its own design.
- **A real navigation shell / attention navigation.** Unchanged from ADR
  0021 — still the largest remaining piece of this item.
- **Every other Desktop-absent capability** (`project.sync_validate`,
  `observability.inspect`, `diagnostics.bundle`, `quality.evaluate`) —
  none have a backend implementation yet, unrelated to this slice.
- **ICU plural/select localization, a language-pack loader, and further
  accessibility work** — out of scope, matching every prior slice.
- **Rendering the generated artifact's own content on Desktop.** Both
  surfaces show only the machine-summarized row (provider, path, state) —
  matching the CLI's own `generate` output exactly, not a gap this slice
  introduces.

## Consequences

- `CapabilityIds` gains `IntegrationSkill`; `CapabilityIds.Implemented`
  gains it too — the trigger for `SurfaceParityTests` to require Desktop
  parity, now satisfied.
- `SurfaceFormatting` gains `IntegrationInspectionLines`/
  `IntegrationWriteLines`, shared by `CliApplication`'s `generate`/
  `install`/`remove` commands and the new `MainPageViewModel` methods.
- `MainPage.xaml` gains `IntegrationGenerateButton`, `IntegrationLabel`,
  `IntegrationInstallButton`, `IntegrationRemoveButton`,
  `IntegrationWriteResultLabel`; `MainPage.xaml.cs` wires them and clears
  the two result labels unconditionally inside `RefreshAsync`.
- `MainPageViewModel` gains `GenerateIntegrationPreviewAsync`,
  `InstallIntegrationAsync`, `RemoveIntegrationAsync`, and a private
  `WriteIntegrationAsync` helper.
- Three new message keys: `IntegrationGenerateAction`,
  `IntegrationInstallAction`, `IntegrationRemoveAction` (en/ru).
  `IntegrationTitle`/`NoIntegrationArtifacts` already existed from the CLI
  slice and are reused as-is via the shared line-building helpers.
- No new diagnostic codes.

## References

- ADR 0005 (local Host and control plane — the read/mutation split this
  slice's `generate` vs. `install`/`remove` verbs follow)
- ADR 0007 (cross-platform core and minimal OS adapters — `SurfaceFormatting`
  stays neutral; nothing here is OS-specific)
- ADR 0009 (`.forge/rules`/`knowledge` parsing — the document-error rows
  `IntegrationInspectionLines`/`IntegrationWriteLines` both surface)
- ADR 0011 (provider integration install and removal — the
  `InspectIntegrationAsync`/`InstallIntegrationAsync`/`RemoveIntegrationAsync`
  backend this slice's Desktop controls call, and the bypassable-confirm
  shape they follow)
- ADR 0021 (Desktop human-gate review — the flat-`MainPage`,
  no-navigation-shell precedent this slice continues)
- ADR 0025 (Desktop control-events polling — the most recent prior
  Desktop-parity slice, whose four-round review history is what this
  slice's own proactive fixes (shared rendering with a same-commit parity
  test, `ProjectRoot` captured once per method) are directly responding to)
