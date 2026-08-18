# ADR 0023: A technical control for human-only CLI commands

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

ADR 0019 introduced `forge gate approve|reject` and
`forge attempt supersede`, and was explicit that the slice shipped "no
actual technical control" for ADR 0005's "human-only" requirement: "no
caller-identity or authorization mechanism exists anywhere in this
codebase, and a project's own `.forge/rules` can instruct an agent to
invoke either command by name regardless of what the generated integration
text otherwise contains." Every ADR since (0020, 0021, 0022) repeats "a
real technical human-only control" in its own deferred-work list without
anyone picking it up. Stage 11 P11.56-P11.66 remains open with substantial
scope left (a real Desktop navigation shell, `sprint.manage` Desktop
controls, `forge sprint rebase`, ICU localization, accessibility,
executor-blocked snapshot fields, P11.67-P11.72 notifications) — this ADR
picks the technical-control item specifically, since it is the one gap
named as a real, live security concern in every prior slice rather than a
scoping/UX deferral.

`docs/contracts/v1/README.md`'s diagnostic table already reserved
`permission_denied` at exit code 8 ("authorization" / "Policy denied the
command") since the table's own introduction — grep confirms zero
production references anywhere in the codebase before this ADR, the same
"reserved but unimplemented" pattern `workflow_blocked` and the three
`supersession_instruction_*` codes each carried before the stage item that
finally implemented them.

## Decision

### `Console.IsInputRedirected` is the technical control, for the CLI only

`forge gate approve|reject` and `forge attempt supersede` now refuse to run
unless standard input is an interactive terminal (`!Console.IsInputRedirected`
at the moment the command tree is built). An agent invoked through
`.forge/rules` runs as a subprocess of its own host tool — this session's
own Bash tool included — with no allocated terminal on stdin, exactly the
condition this check detects. A human typing the command directly at a
shell prompt has a real terminal, and passes.

This is deliberately not framed as solved: an attacker or agent that
explicitly allocates a pseudo-terminal (a real `pty`) before invoking the
command would still pass the check. It closes the specific, common attack
vector this codebase has actually flagged three times — an agent shelling
out to `forge gate approve`/`forge attempt supersede` the same way it runs
any other command — without claiming to be unforgeable caller-identity.
Framed the same honest way ADR 0019 framed mandatory confirmation: this
raises the bar from "a project's rules file can just say to do it" to
"the invoking process must additionally fake an interactive session," a
real, non-trivial obstacle for a typical agent harness, not a cryptographic
guarantee.

Desktop needs no equivalent change. `DisplayAlertAsync`'s confirmation
dialog already requires a human to click a button in a running GUI session
— there is no non-interactive invocation path for a MAUI page action to
begin with, unlike a CLI command a script or subprocess can invoke freely.

### The check runs first, unconditionally, before any other validation

Both commands check `isInteractive()` as the very first statement inside
their `SetAction` callback — before parsing `--sprint`/`--node`/the attempt
id, before reading the instruction file or stdin, and before `--yes` is
even inspected. `--yes` cannot substitute for an interactive session any
more than it can substitute for a valid sprint id; a non-interactive caller
that also passes `--yes` is refused identically to one that does not, and
never reaches `ReadInstructionAsync` — proven by a dedicated
`ThrowingTextReader` test double that fails the test if the instruction
source is ever touched, not merely if the mutation is never called.

### `permission_denied` (exit 8) is implemented for exactly this refusal

No new diagnostic code was invented. `permission_denied`/exit 8 already
existed in the contract table with the exactly-matching meaning "Policy
denied the command" and zero implementation anywhere — the same kind of
gap `workflow_blocked` was before Stage 11 P11.1-12 implemented it. Wiring
it here closes a second long-reserved code rather than adding a third
similar one.

### `CreateRootCommand` gains an injectable `isInteractive` parameter

Matching the existing `input`/`resolveMutations` pattern exactly: a new
optional `Func<bool>? isInteractive` parameter, defaulting to
`() => !Console.IsInputRedirected` when the caller supplies none (every
real CLI invocation). Tests exercising the gate/supersede mutation logic
itself pass `isInteractive: () => true` explicitly — `Console
.IsInputRedirected` reflects the real test-runner process's own stdin,
which is not guaranteed interactive under `dotnet test` (confirmed: it
reads `true` in this repository's own CI and local test runs), so relying
on the ambient default would make 12 pre-existing gate/attempt CLI tests
fail non-deterministically depending on how the test process itself was
launched. Two new tests pass `isInteractive: () => false` to exercise the
refusal path itself, one per command.

## Consequences

- `DiagnosticCodes.PermissionDenied` (`permission_denied`) is implemented;
  `ExitCodes.Authorization` (8) is wired into `ExitCodes.For`.
- `CliApplication.CreateRootCommand`/`CreateGateCommand`/
  `CreateGateResolveCommand`/`CreateAttemptCommand`/
  `CreateAttemptSupersedeCommand` all gain the threaded `isInteractive`
  parameter.
- 12 existing tests in `HumanGateAndSupersessionCliTests.cs` were updated to
  pass `isInteractive: () => true` explicitly, so their outcomes no longer
  depend on the test process's own ambient stdin state.
- Two new tests (`GateApproveCommandRefusesANonInteractiveSessionEvenWithYes`,
  `AttemptSupersedeCommandRefusesANonInteractiveSessionEvenWithYes`) prove
  the refusal: exit 8, `permission_denied` on stderr, zero mutation calls,
  and — for supersede — the instruction source is never read at all.
- `SurfaceLanguageTests.DiagnosticCodesMapToTheContractExitCodes` gained the
  new `PermissionDenied → Authorization` case.
- No wire/protocol change: this is CLI presentation-layer argument handling,
  entirely upstream of `IForgeMutations` — no `ControlProtocol` kind, Host
  dispatch handler, or `capabilities.json` field changes.

## Deliberately deferred

- **A real, unforgeable caller-identity mechanism.** This ADR raises the
  bar; it does not claim to close it. A pty-allocating agent still passes.
  True caller identity (a signed human-presence token, an OS-level
  interactive-session credential, or similar) is a substantially larger
  effort with no existing primitive in this codebase to build on.
- **Applying the same check to any future human-only CLI command.** Only
  the two commands that exist today are covered; a future one must
  explicitly opt in the same way, not inherit it implicitly.
- Every other Stage 11 P11.56-P11.66 gap named in ADR 0022's own deferred
  list (navigation shell, `sprint.manage` Desktop controls, `forge sprint
  rebase`, ICU localization, accessibility, executor-blocked snapshot
  fields) — unrelated to this slice, still open.

## References

- ADR 0005 (human-only requirement's origin)
- ADR 0019 (introduced `forge gate approve|reject`/`forge attempt
  supersede` with mandatory confirmation as the only control, explicitly
  naming a real technical control as deferred)
- `docs/contracts/v1/README.md` (the pre-reserved `permission_denied`/exit
  8 row this ADR implements)
