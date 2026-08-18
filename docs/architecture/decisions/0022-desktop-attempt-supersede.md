# ADR 0022: Desktop attempt-supersession controls

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66's Desktop parity work is worked as its own
sequence of slices (ADR 0021). The first, `workflow.review` (gate
approve/reject), landed directly on the existing flat `MainPage`. This ADR
covers the second: `attempt.supersede`, wired to the existing
`IForgeMutations.SupersedeAttemptAsync` backend (ADR 0019).

`sprint.manage` — the third Desktop-absent capability with real CLI/backend
support (ADR 0020) — was considered first, since ADR 0021's own research
ranked it alongside `attempt.supersede` as a next candidate. It was
rejected for this slice: `capabilities.json`'s `sprint.manage` entry
documents `"cli": "forge sprint <create|run|resume|cancel|rebase>"`, and
`forge sprint rebase` does not exist yet (ADR 0020: "no sprint-level
backend exists yet ... needs its own design"). Marking `sprint.manage`
`Implemented` — on either surface — would make
`SurfaceParityTests.CliExposesEveryDocumentedCapabilityCommand` require a
`rebase` subcommand that cannot be built without first designing that
backend, which is out of scope for a Desktop-controls slice. `attempt.
supersede`'s own `cli` field (`forge attempt supersede <attempt-id>
--instruction-file <path|->`) names one fully-implemented command with no
such gap, so it is the clean next target; `sprint.manage`'s Desktop
controls are deferred to a future slice that either builds `rebase` first
or accepts documenting the gap the way `workflow.review`'s "no navigation
shell yet" gap was accepted in ADR 0021.

## Decisions

### The design mirrors `workflow.review`'s exactly, reusing what it established

`attempt.supersede` is human-only (`permission:
human_attempt_supersede_confirm` in `capabilities.json`, matching
`workflow.review`'s `human_gate_confirm`), so the same shape applies
without modification: confirmation is the `DisplayAlertAsync` dialog's real
answer forwarded to `IForgeMutations.SupersedeAttemptAsync`, never
bypassed; declining short-circuits before any mutation call; the
confirmation dialog names its target instead of repeating the action name;
the result label is cleared by `RefreshAsync` and re-set after it so a
decision's own outcome still displays correctly. `MainPageViewModel`
directly reuses `ResolveSprintIdAsync`/`SprintTarget` — the exact
blank-means-active-sprint/ambiguity-resolution logic ADR 0021 built and
then spent two further review rounds hardening for `workflow.review` — for
this capability's own sprint targeting, rather than re-deriving (and
re-discovering the same bugs in) a second copy of it.

### The instruction is a single-line `Entry`, validated client-side to the same bound the server enforces

The CLI reads the replacement instruction from a file or standard input
(`--instruction-file <path|->`), bounded server-side by
`SprintScheduler.MaxSupersessionInstructionLength` (4000 characters).
Desktop has no file-picker or streaming-input concept on this page, and the
full instruction text is already resident in memory as soon as it is
typed — an `Entry` (MAUI's single-line text control) is enough, and the
same three diagnostics the CLI's bounded reader produces
(`SupersessionInstructionRequired` for empty/whitespace-only,
`SupersessionInstructionTooLong` for over-bound, otherwise proceed) are
checked client-side in `MainPageViewModel.SupersedeAttemptAsync` before
ever resolving mutations or reaching the Host — there is nothing to stream,
so unlike the CLI's own bounded-reader design (which caps a read from an
unbounded source), this is a plain length check on an already-complete
string. A multi-line `Editor` control would render long instructions more
readably; deferred as a minor UX polish, not a correctness gap (see below).

### An unparsable attempt id reports `WorkflowEventConflict`, matching the CLI's own choice

`CliApplication.CreateAttemptSupersedeCommand` reports an unparsable
`<attempt-id>` argument as `WorkflowEventConflict`, not a dedicated
"attempt not found" code (that decision predates this ADR — ADR 0019).
`MainPageViewModel.SupersedeAttemptAsync` matches it exactly, so the same
attempt-id failure produces the same diagnostic on both surfaces.

### A latent `SurfaceParityTests` bug this slice's own capability exposed

`CliExposesEveryDocumentedCapabilityCommand`'s `Alternatives(tokens)`
helper extracts `<a|b>`-shaped tokens as alternative subcommand names, to
verify e.g. `workflow.review`'s `forge gate <approve|reject>` exposes both
`gate approve` and `gate reject`. `attempt.supersede`'s own `cli` field
also contains a `<a|b>`-shaped token — `--instruction-file <path|->` — but
that one describes an *option's* accepted value grammar ("a path, or the
literal `-` for stdin"), not sibling subcommands; nothing before this slice
ever added a capability whose CLI string had a `<...|...>` token following
a `--flag`, so the bug was latent, not previously reachable. Fixed by
having `Alternatives` stop scanning at the first `--option` token, since a
genuine subcommand-alternatives token can only appear before any option
starts. Verified `workflow.review`'s own existing check is unaffected (its
`cli` string has no `--` token at all).

## Deliberately deferred

- **`sprint.manage` Desktop controls.** See "Context" above — blocked on
  `forge sprint rebase`, which needs its own design.
- **A real navigation shell / attention navigation.** Unchanged from ADR
  0021 — still the largest remaining piece of this item.
- **A multi-line `Editor` for the instruction text.** A minor UX
  improvement over the single-line `Entry` used here, not a correctness
  gap — the same 4000-character bound applies either way.
- **Every other Desktop-absent capability** (`control.events`,
  `project.sync_validate`, `integration.skill`, `observability.inspect`,
  `diagnostics.bundle`, `quality.evaluate`) — most have no backend caller
  at all yet, unrelated to this slice.
- **ICU plural/select localization, a language-pack loader, and further
  accessibility work** — out of scope, matching every prior slice.

## Consequences

- `CapabilityIds` gains `AttemptSupersede`; `CapabilityIds.Implemented`
  gains it too — the trigger for `SurfaceParityTests` to require Desktop
  parity, now satisfied.
- `MainPage.xaml` gains `AttemptIdEntry`, `AttemptInstructionEntry`,
  `AttemptSupersedeButton`, `AttemptSupersedeResultLabel`;
  `MainPage.xaml.cs` wires them, mirroring `ResolveGateAsync`'s shape
  exactly.
- `MainPageViewModel` gains `SupersedeAttemptAsync`/
  `AttemptSupersedePrompt`, reusing `ResolveSprintIdAsync`/`SprintTarget`.
- New message keys: `AttemptSupersedeFailed`, `AttemptIdLabel`,
  `AttemptInstructionLabel`, `AttemptSupersedeAction`,
  `AttemptSupersedeConfirmationRequired` (en/ru). `AttemptSuperseded`
  already existed from the CLI slice and is reused for the success case.
- No new diagnostic codes — `SprintNotFound`, `WorkflowEventConflict`,
  `SupersessionInstructionRequired`/`TooLong`, and `ConfirmationRequired`
  are all reused, matching the CLI's own behavior for each.
- `SurfaceParityTests.Alternatives` bugfix (see above), plus a new static
  assertion mirroring the gate slice's dialog-naming check.

## References

- ADR 0018 (rate-limit deferral and attempt supersession — the
  `SupersedeAttemptAsync` scheduler primitive)
- ADR 0019 (human-gate and attempt-supersession CLI commands — the
  `attempt.supersede` backend this slice's Desktop controls call)
- ADR 0021 (Desktop human-gate review controls — the pattern this slice
  repeats, including the resumability/targeting logic reused directly)
- `tests/Forge.Tests/Acceptance/SurfaceParityTests.cs` (the parity gate
  this slice satisfies for `attempt.supersede`)
