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

Independent review found this method's *check order* diverged from the
CLI's: it tested emptiness before the length bound, while
`CliApplication.ReadInstructionAsync` checks the bound first. For an
instruction that is both whitespace-only and over 4000 characters (a long
blank block pasted into the `Entry`), that meant Desktop reported
`supersession_instruction_required` while the CLI reports
`supersession_instruction_too_long` for the identical input — contradicting
this section's own "same three diagnostics" claim. Fixed by swapping the
checks to match the CLI's order exactly, with a regression test pinning
the over-long-and-blank case specifically. The same round also found
`trimmedInstruction`'s name promised a contract the code never implemented
(it was a null-coalesce, never a `.Trim()`) — not trimming is *correct*
(the CLI forwards its own instruction source verbatim, and this must
record the same durable text for identical input on both surfaces), so the
misleading name was the actual hazard: it invited a future maintainer to
"restore" a trim the name implied was already missing. Renamed to
`effectiveInstruction`, and the routing test now uses an instruction with
surrounding whitespace, asserting it reaches `mutations` untouched. A
third finding pinned the length bound's accepting side too — only the
rejecting `Max + 1` case had a test, so an off-by-one accepting only up to
`Max - 1` would have shipped undetected; an exactly-`Max`-length
instruction now has its own passing test.

### A blank attempt id is refused before the confirmation dialog shows

`GatePrompt` (ADR 0021) is not a precedent for handling a missing target:
its node-id line always renders a concrete value, since a blank node id has
a documented default (`human_approval`). `attempt.supersede` has no such
default — a blank attempt id is always an error. Independent review found
the first version of this slice's `AttemptSupersedePrompt` interpolated a
possibly-`null` attempt id unconditionally, so a blank `AttemptIdEntry`
produced a confirmation dialog reading `Attempt id:` with nothing after
it — asking the user to confirm an irreversible action against an unnamed
target, only failing afterward with the unhelpful `workflow_event_conflict`.
Fixed at two points: `MainPage.SupersedeAttemptAsync` now checks
`AttemptId is null` *before* showing the dialog at all, reporting a
dedicated `AttemptIdRequired` message with no dialog and no mutation call
(the fast, correct answer, since no legitimate flow can proceed without
one); and `AttemptSupersedePrompt` itself now renders an explicit
`AttemptIdMissingPlaceholder` for a `null`/blank id as defense in depth,
for any caller that reaches it without going through that guard.

Round 3 review found this guard had shipped with no regression test: deleting
it left the full suite green, since `AttemptIdRequired` was reachable from
exactly one code path and asserted by none. The same round found a second,
asymmetric gap the guard's own rationale should have already ruled out: a
*blank instruction* was still refused only after the user confirmed the
irreversible action, not before — `MainPage.SupersedeAttemptAsync` now
checks `string.IsNullOrWhiteSpace(AttemptInstructionEntry.Text)` before the
dialog too, reporting a new `AttemptInstructionRequired` message the same
way the attempt-id guard does. The instruction's *length* bound stays
server-validated in `MainPageViewModel.SupersedeAttemptAsync` — only
emptiness makes the target itself meaningless the way a blank attempt id
does, so only emptiness is checked pre-dialog. No MAUI control can be
instantiated headlessly in this test suite (the same constraint every prior
Desktop-slice ADR has noted), so both guards are pinned by a code-behind
text assertion proving the guard text appears, and appears *before*
`DisplayAlertAsync`, inside `SupersedeAttemptAsync`'s own method body.

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

The same review round found this test had a second, independent gap: it
only ever asserted `tokens[0]` (the top-level subcommand), `--options`
(searched recursively via `HasOption`), and `<a|b>`-shaped alternatives —
never a plain *literal* subcommand token appearing after `tokens[0]`.
`attempt.supersede` is the first `Implemented` capability whose `cli`
field has one (`supersede`, between `attempt` and `<attempt-id>`), so
renaming that CLI subcommand would have left this test green as long as
some *option* with a matching name existed anywhere in the command tree.
Fixed by walking each literal token after `tokens[0]` and requiring it to
be a real subcommand at its documented depth, stopping at the first
option or `<...>`-shaped token (positional arguments and options mark the
end of the literal-subcommand path). Verified against every currently
`Implemented` capability's own `cli` string: each one's first token after
`tokens[0]` already starts with `--` or `<`, so the new check is a no-op
for all of them and only `attempt.supersede` gains real coverage from it.

### Round 2 review: three further findings, all fixed

Independent review found the `SprintTarget.Ambiguous` branch of
`SupersedeAttemptAsync` still returned `MessageKeys.GateSprintAmbiguous`
verbatim — text that explicitly reads "resolve its gate" — reached on the
normal blank-sprint-id-with-multiple-non-terminal-sprints path for a
capability that has no gate at all. Round 1 had already given the sibling
`SprintNotFound` branch its own `AttemptSupersedeFailed` wording but left
this branch sharing the gate's message; it was also the only branch of the
method without a test (the gate path has one), which is how it went
unnoticed. Fixed with a dedicated `AttemptSupersedeSprintAmbiguous` key
(en/ru) and a regression test mirroring the gate's own ambiguity test.

The same round found round 1's own literal-subcommand-token fix (above) was
itself incomplete: it resolved `current` — the exact documented command —
but then discarded it, leaving the `--option` and `Alternatives()` checks
searching from `command` with `HasOption`'s own recursive descent, the
exact hazard the walk's comment names. `--instruction-file` would still
have passed if declared on a sibling of `supersede` rather than on
`supersede` itself. Fixed by searching from `current` for both checks;
verified `HasOption(current, option)` still passes for every currently
`Implemented` capability, so this is a pure tightening with no other test
changes required.

Finally, this ADR's own "Consequences" section listed five of the seven
message keys this slice added, omitting `AttemptIdRequired` and
`AttemptIdMissingPlaceholder` despite both being introduced by name earlier
in this document. Fixed by completing the list.

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
  `AttemptSupersedeConfirmationRequired`, `AttemptIdRequired`,
  `AttemptIdMissingPlaceholder`, `AttemptSupersedeSprintAmbiguous`,
  `AttemptInstructionRequired` (en/ru). `AttemptSuperseded` already existed
  from the CLI slice and is reused for the success case.
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
