# ADR 0027: Desktop sprint-lifecycle controls

- Status: Accepted
- Date: 2026-08-19
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66's Desktop parity work continues as its own sequence
of slices (ADR 0021, 0022, 0025, 0026). Every prior slice's own
"Deliberately deferred" list named `sprint.manage`'s Desktop controls as
blocked on `forge sprint rebase`, which `capabilities.json` documents as
one of `sprint.manage`'s five verbs (`create|run|resume|cancel|rebase`) but
which has never had a backend on either surface (ADR 0020: "no sprint-level
backend exists yet ... needs its own design").

Before starting this slice, that blocker was investigated directly rather
than accepted at face value again. The result: **`rebase` is not a design
gap waiting to be filled — it is a command with no reachable trigger.**
`SprintGitIsolation.RebaseAttemptAsync` (the git-level primitive
`forge sprint rebase` would wrap) already exists and is unit-tested in
isolation, but it exists purely to recover from `WorktreeBaseMismatch`: two
attempts' branches both forked from the same integration tip, one
integrated first, the second's fast-forward now fails because the tip
moved. That race can only occur once a node executor exists to drive
attempts through `SprintGitIsolation`'s own `IntegrateAsync`, and no
executor exists yet — confirmed by the same "zero production callers" fact
every
prior Stage 11 ADR has independently reconfirmed for `SprintScheduler.StartAttemptAsync`/
`ILlmProvider.RunAsync`. Building `forge sprint rebase` today would ship a
command with no state it could ever legitimately act on.

This is categorically different from every other capability's own deferred
scope (a surface choice around an already-meaningful backend operation):
`rebase`'s backend operation is only meaningful in a system state that
cannot arise without the executor, which is itself a multi-slice effort no
single command wrapper can absorb.

## Decisions

### `sprint.manage` and `rebase` are split into two separate capabilities

`sprint.manage`'s own contract (`CreateRunResumeCancelRebaseSprint`) is
narrowed to `CreateRunResumeCancelSprint`, and its `cli` field drops
`rebase`. A new `sprint.rebase` capability (`RebaseSprint`) documents the
verb separately, `note`s exactly why it has no backend trigger yet, and is
deliberately left off `CapabilityIds.Implemented` on both surfaces.
`docs/contracts/v1/capabilities.json`'s `contract_version` bumps
`1.1.0` → `1.2.0` for the split. This unblocks Desktop parity for the four
real, already-CLI-complete verbs without pretending `rebase` is designed
when it structurally cannot be yet.

### `create`/`run`/`resume` are not confirmable; `cancel` is ordinarily bypassable

Matches `ForgeApplication`'s own existing contract exactly, unchanged by
this slice: `create`/`run`/`resume` are additive, so their Desktop buttons
show no dialog at all — the click *is* the action, the same shape
`control.events`'s poll button and `integration.skill`'s generate button
already established for non-destructive actions. `cancel` reuses
`RecoverAsync`/`InstallIntegrationAsync`'s ordinarily-bypassable shape
(`workflow_mutate`, not one of `workflow.review`/`attempt.supersede`'s
human-only permissions): the dialog's own answer is passed through as
`confirmed`, but declining does not itself short-circuit the call — the
mutation may still succeed via a configured `interaction.confirm_destructive`
bypass, and its dialog names the sprint it targets (`MainPageViewModel.SprintCancelPrompt`,
mirroring `GatePrompt`/`AttemptSupersedePrompt`'s own shape) rather than
repeating the action name, applying ADR 0026 round 1's lesson from the
first commit instead of waiting for a review round to catch it.

### `run`/`resume`/`cancel` reuse `ResolveSprintIdAsync`/`SprintTarget` directly

The same blank-means-active-sprint/ambiguity-resolution logic
`ResolveGateAsync`/`SupersedeAttemptAsync` already established and two
review rounds already hardened (ADR 0021, 0022) — not a third,
independently-written copy. A dedicated `SprintManageSprintAmbiguous`
message key is used for the ambiguous branch rather than reusing
`GateSprintAmbiguous`/`AttemptSupersedeSprintAmbiguous`, applying the exact
fix ADR 0022 round 2 needed for `attempt.supersede` from this slice's first
commit — proven by a test that also asserts the message is *not* either
sibling capability's own ambiguity text. `create` needs no sprint id at
all: it always mints a fresh one.

### Rendering is shared with the CLI from the start, with a parity strategy each verb actually needs

`CliApplication`'s `create`/`run`/`resume` command bodies (`cancel`'s own
message is a single fixed string, not worth extracting) are refactored
into two new `SurfaceFormatting` methods — `SprintCreatedMessage` and
`SprintTransitionMessage` (the latter reused by both `run` and `resume`,
matching the CLI's own existing `CreateSprintTransitionCommand`
parameterization) — landed in the same commit as CLI/Desktop parity tests
for all four verbs, applying ADR 0026's lesson proactively rather than
reactively.

`create`'s own parity test cannot use literal text equality the way every
prior parity test in this codebase has: each call mints a fresh `Guid`, so
the two surfaces' messages can never be byte-identical even when the
formatting is genuinely correct. It compares the only property that can
actually drift instead — the fixed message prefix, and that a well-formed
`"D"`-format id follows it — rather than either skipping the parity proof
entirely or asserting something that would always fail. `run`/`resume`/
`cancel` have no such randomness in their own rendered text, so they keep
the established two-separate-projects literal-equality shape `integration.skill`'s
write parity test set. `resume`'s own parity test uses a genuinely blocked
sprint (a rejected human gate, matching `SprintLifecycleCliTests`'s own
fixture), not a fresh one — a fresh sprint's `resume` call fails, and a
failure renders nothing but the diagnostic on both sides, exactly the
"an empty state on both sides would pass too" pattern ADR 0025 round 1
rejected for the events parity test.

## Round 1 review

Independent review found five issues, all fixed:

1. **The `sprint.rebase` capability note and this ADR's own Context section
   both cited a nonexistent member**, `SprintScheduler.IntegrateAsync` —
   `SprintScheduler` never references that type; the real method is
   `SprintGitIsolation.IntegrateAsync`. The investigation's conclusion was
   unaffected (independently re-verified by the review itself), only the
   name was wrong. Fixed in both `capabilities.json` and this ADR.
2. **The `create` parity test's format check never actually compared the
   two surfaces against each other**, and skipped exactly one unasserted
   separator character — a drifted separator (e.g. `"prefix:id"` instead
   of `"prefix id"`) would have passed on both sides independently. Fixed:
   since a `"D"`-format `Guid` is always exactly 36 characters, everything
   before the last 36 characters (prefix and separator together) is now
   compared for direct equality between the two surfaces, with the tail
   independently confirmed to parse as a `Guid`.
3. **`TransitionSprintAsync`'s own copy of the ambiguity-resolution
   branch — shared by `run`/`resume` — had no test of its own.** Only
   `CancelSprintAsync`'s separate copy of the identical branch was tested;
   swapping `TransitionSprintAsync`'s message to `GateSprintAmbiguous`
   left the full suite green. This is precisely the ADR 0022 round-2
   defect this ADR's own "Decisions" section claims to have pre-empted —
   pre-empted for one of the two call sites, not both. Fixed with a new
   `RunSprintAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity`
   test (`run` stands in for `resume`, which shares the identical helper
   call); verified by mutation testing — applied the exact regression,
   confirmed the new test fails, reverted.
4. **`CHANGELOG.md` had no entry for the capability-contract split**,
   despite the section being published verbatim as the GitHub Release
   description. Added a `Changed` entry.
5. **This ADR's own Consequences section said "five new message keys"
   directly above a list of six.** Corrected.

## Round 2 review

Independent review found three issues, all fixed:

1. **Round 1 finding 3 was fixed for only the `Ambiguous == true` half of
   the branch it named.** `TransitionSprintAsync`'s copy also has a
   `false` (unparsable/not-found) half; only `CancelSprintAsync`'s
   separate copy of that half was tested. Mutation-verified: swapping the
   message/diagnostic on that line left the full suite green. Fixed with a
   new `RunSprintAsyncReportsSprintNotFoundForAnUnparsableSprintIdWithoutCallingMutations`
   test; verified by mutation testing — applied the exact regression,
   confirmed the new test fails, reverted.
2. **`SprintCancelPrompt`'s blank-sprint branch had no test**, unlike both
   prompts it claims to mirror (`GatePrompt`/`AttemptSupersedePrompt` each
   have one). Mutation-verified: swapping the placeholder key left the
   full suite green. Fixed with a new
   `SprintCancelPromptRendersThePlaceholderForABlankSprintId` test —
   the first attempt used `Assert.Contains` against the placeholder text
   and passed even under the mutation, because the Russian
   `SprintIdLabel` string itself already contains the placeholder's own
   Russian text as a substring (`"... (пусто: активный спринт): ..."`
   contains `"активный спринт"` regardless of which placeholder branch
   renders). Fixed by asserting `Assert.Equal` against the full composed
   string instead of a substring, which fails correctly on the mutation
   regardless of UI culture.
3. **Round 1 finding 4's fix introduced its own inaccuracy**: it justified
   the `CHANGELOG.md` addition with "`contract_version`'s first-ever bump
   in this file", but that file's `contract_version` was already bumped
   once before, `1.0.0` -> `1.1.0` in commit `a7be09f` (v0.11.0). Removed
   the inaccurate appositive; the `Changed` entry itself was correct and
   is kept.

## Round 3 review (final full-scope round)

Independent review found one issue, fixed:

1. **The PR body's own Verification section claimed
   `SprintLifecycleCliTests`'s "existing 215-test suite"** — the file
   actually has 8 tests (confirmed by a filtered run); no suite in this
   repository has 215 tests. Corrected in the PR body.

The reviewer independently re-verified every prior round's claims (the
`rebase` zero-caller investigation, the `a7be09f` contract-version
history, the ru-RU substring coincidence behind round 2 finding 2, TOCTOU
safety, and all four branches of the `TransitionSprintAsync`/
`CancelSprintAsync` duplication) and mutation-tested six regressions, all
caught. Two non-blocking observations, named rather than silently
dropped: the `SprintManageFailed`/`RecoveryFailed`/etc. mutation-failure
fallback messages have no dedicated test anywhere in this codebase — a
pre-existing, repo-wide gap this slice did not introduce; and
`README.md`'s Desktop paragraph is stale documentation accumulated across
ADR 0021/0022/0025/0026 that this slice compounds but did not cause.
Neither blocks this PR.

## Deliberately deferred

- **`forge sprint rebase` and Desktop `sprint.rebase` controls.** Not a
  scoping choice deferred for later — structurally blocked on the node
  executor, itself a multi-slice effort. See "Context" above.
- **A real navigation shell / attention navigation.** Unchanged from ADR
  0021 — still the largest remaining piece of this item.
- **Every other Desktop-absent capability** (`project.sync_validate`,
  `observability.inspect`, `diagnostics.bundle`, `quality.evaluate`) —
  none have a backend implementation yet, unrelated to this slice.
- **ICU plural/select localization, a language-pack loader, and further
  accessibility work** — out of scope, matching every prior slice.
- **The two named snapshot-field gaps** (integration status, phase
  profile) — still blocked on the same executor `rebase` is, unrelated to
  this slice's own scope.

## Consequences

- `docs/contracts/v1/capabilities.json`: `sprint.manage`'s `contract`/`cli`
  fields narrowed to the four real verbs; new `sprint.rebase` capability
  entry added, not implemented; `contract_version` `1.1.0` → `1.2.0`.
- `CapabilityIds` gains `SprintManage`; `CapabilityIds.Implemented` gains
  it too.
- `SurfaceFormatting` gains `SprintCreatedMessage`/`SprintTransitionMessage`,
  shared by `CliApplication`'s `create`/`run`/`resume` commands and the new
  `MainPageViewModel` methods.
- `MainPage.xaml` gains `SprintCreateButton`, `SprintRunButton`,
  `SprintResumeButton`, `SprintCancelButton`, `SprintManageResultLabel`
  (reuses the existing `SprintIdEntry`); `MainPage.xaml.cs` wires them and
  clears the result label unconditionally inside `RefreshAsync` (no
  companion state, matching `GateResultLabel`'s own rule, not
  `EventsLabel`'s gated one).
- `MainPageViewModel` gains `CreateSprintAsync`, `RunSprintAsync`,
  `ResumeSprintAsync`, `CancelSprintAsync`, `SprintCancelPrompt`, and a
  private `TransitionSprintAsync` helper shared by `run`/`resume`.
- Six new message keys: `SprintCreateAction`, `SprintRunAction`,
  `SprintResumeAction`, `SprintCancelAction`, `SprintManageFailed`,
  `SprintManageSprintAmbiguous` (en/ru). `SprintCreated`/`SprintAdvanced`/
  `SprintAdvancedUnknownState`/`SprintResumed`/`SprintCancelled` already
  existed from the CLI slice and are reused as-is.
- No new diagnostic codes — `SprintNotFound` (unparsable sprint id) and
  every diagnostic the underlying mutations already produce are reused,
  matching the CLI's own behavior for each.

## References

- ADR 0005 (local Host and control plane — the confirmable/non-confirmable
  split this slice's four verbs follow)
- ADR 0007 (cross-platform core and minimal OS adapters — `SurfaceFormatting`
  stays neutral; nothing here is OS-specific)
- ADR 0018 (rate-limit deferral and attempt supersession — introduces
  `SprintGitIsolation.RebaseAttemptAsync`, the primitive `forge sprint
  rebase` would eventually wrap)
- ADR 0020 (sprint-lifecycle CLI commands — the `create`/`run`/`resume`/
  `cancel` backend this slice's Desktop controls call, and the original
  "rebase needs its own design" note this ADR resolves by explaining why
  it cannot be designed yet)
- ADR 0021 (Desktop human-gate review — the flat-`MainPage`,
  no-navigation-shell precedent this slice continues, and the
  `ResolveSprintIdAsync`/`SprintTarget` sharing this slice reuses)
- ADR 0022 (Desktop attempt-supersession — the dedicated-ambiguity-message-key
  lesson this slice applies from its first commit)
- ADR 0025/0026 (Desktop control-events polling, Desktop integration-skill
  controls — the parity-test-completeness lessons this slice applies
  proactively)
