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

### `Console.IsOutputRedirected` is the technical control, for the CLI only

`forge gate approve|reject` and `forge attempt supersede` now refuse to run
unless standard *output* is an interactive terminal
(`!Console.IsOutputRedirected`, called fresh each time the command's own
action runs, not fixed once at command-tree construction). An
agent invoked through `.forge/rules` runs as a subprocess of its own host
tool — this session's own Bash tool included — with both streams
redirected so the host tool can capture them, exactly the condition this
check detects. A human typing the command directly at a shell prompt has a
real terminal attached to output, and passes.

Independent review of the first version of this ADR found it checked
*input* redirection instead (`!Console.IsInputRedirected`) — this broke
`forge attempt supersede`'s only practical non-interactive-adjacent shape:
`--instruction-file -` reads the replacement instruction from standard
input, which is *always* redirected when piped, whether the piping human
is sitting at an interactive shell or not. Checking input redirection made
that documented, ordinary invocation shape (`capabilities.json`'s own `cli`
field for `attempt.supersede`) permanently unreachable, contradicting this
ADR's own "raises the bar" framing with an actual functional regression.
Output redirection has no such conflict — a human piping instruction text
in via stdin still has an attached terminal for stdout unless they also
explicitly redirect it, which is a distinct, much rarer scripting choice —
and is the same signal common Unix tooling checks (`isatty(stdout)`) for
exactly this "should I behave as if a human is watching" question, chosen
specifically because piped *input* is an ordinary, legitimate data channel
that says nothing about who is driving the process.

This trades which direction the check is wrong in, not the underlying
limitation itself: a human
who deliberately redirects their OWN output — `forge gate approve ... |
tee log.txt` to keep a record, for example — is now refused exactly like a
non-interactive agent, even though a real human is making the decision.
This is a genuine, known false refusal, not merely a false-negative gap
like pty allocation; it is accepted rather than fixed for the same reason
input redirection was rejected as the signal: there is no `Console`
property, and no portable primitive in this codebase's neutral code today,
that distinguishes "a human piped my output for logging" from "an agent's
host tool captured my output to parse it." A future `--force-interactive`
escape hatch (or similar, deliberately opt-in) is the likely shape of a
real fix, named here rather than silently left unstated.

This is deliberately not framed as solved: an attacker or agent that
explicitly allocates a pseudo-terminal (a real `pty`) before invoking the
command would still pass the check, and see "Deliberately deferred" below
for a second, independently-discovered bypass through the Host protocol
directly. It closes the specific, common attack vector this codebase has
actually flagged three times — an agent shelling out to `forge gate
approve`/`forge attempt supersede` the same way it runs any other command
— without claiming to be unforgeable caller-identity. Framed the same
honest way ADR 0019 framed mandatory confirmation: this raises the bar from
"a project's rules file can just say to do it" to "the invoking process
must additionally fake an interactive session," a real, non-trivial
obstacle for a typical agent harness, not a cryptographic guarantee.

Desktop needs no equivalent change. `DisplayAlertAsync`'s confirmation
dialog already requires a human to click a button in a running GUI session
— there is no non-interactive invocation path for a MAUI page action to
begin with, unlike a CLI command a script or subprocess can invoke freely.

### The check runs first, unconditionally, before this action's own validation

Both commands check `isInteractive()` as the very first statement inside
their `SetAction` callback — reachable only after System.CommandLine has
already parsed `--sprint`/`--node`/`--yes`/the attempt id into
`parseResult` (parsing itself cannot be skipped; this is the earliest point
*this command's own code* runs), and before this action inspects any of
those values itself, reads the instruction file or stdin, or even looks at
`--yes`. `--yes` cannot substitute for an interactive session any more than
it can substitute for a valid sprint id; a non-interactive caller that also
passes `--yes` is refused identically to one that does not, and never
reaches `ReadInstructionAsync` — proven by a dedicated `ThrowingTextReader`
test double that fails the test if the instruction source is ever touched,
not merely if the mutation is never called.

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
`() => !Console.IsOutputRedirected` when the caller supplies none (every
real CLI invocation). Tests exercising the gate/supersede mutation logic
itself pass `isInteractive: () => true` explicitly — `Console
.IsOutputRedirected` reflects the real test-runner process's own stdout,
which is not guaranteed interactive under `dotnet test` (confirmed: it
reads `true` in this repository's own CI and local test runs, the same way
`IsInputRedirected` did under the first version of this check), so relying
on the ambient default would make every pre-existing gate/attempt CLI test
fail non-deterministically depending on how the test process itself was
launched. Two tests pass `isInteractive: () => false` to exercise the
refusal path itself, one per command; a third omits the parameter entirely
to prove the production default itself is wired correctly and reaches the
same real `Console` property — every other test only proves the
*parameter* works, never that `CreateRootCommand`'s own default lambda is
actually connected to it.

### Round 1 review: one functional regression, one residual bypass, three prose fixes

Independent review found six issues, none left unaddressed:

1. **The input-vs-output redirection signal** (described above under "the
   technical control") — a real functional regression fixed by switching
   the check itself, not just documenting around it.
2. **A residual bypass this ADR's original "Deliberately deferred" section
   named only partially**: `ControlPlaneHostedService`'s
   `DispatchResolveGateAsync`/`DispatchSupersedeAttemptAsync` have no
   equivalent check, and the Host's named pipe accepts same-user
   connections — a caller that speaks the Host protocol directly via
   `Forge.Host.Client` (never invoking the `forge` binary at all) bypasses
   this control entirely, not just via a pty. This is not fixed in this
   ADR: a console-based interactivity check is *meaningless* at the Host
   dispatch layer, since the Host is a background service that never had a
   terminal of its own to check, connected to over a pipe with no
   equivalent concept of "the connecting process's console." A real fix
   would need the Host to authenticate something about the caller's own
   session, which is exactly the "real, unforgeable caller-identity
   mechanism" this ADR already named as future work, not a gap this one
   introduced — but the specific *shape* of this bypass (same-user
   `Forge.Host.Client` calls, not just pty allocation) was previously
   unstated and is now named explicitly below.
3. A code comment claimed `Console.IsInputRedirected` (now
   `IsOutputRedirected`) "is read once here, not inside the default
   lambda" while the very next line reads it *inside* the lambda — the
   opposite of what the comment said. Fixed by rewriting the comment to
   describe what the code actually does (read once per invocation, not
   cached across invocations), matching this section's rewrite above.
4. "Refused before any argument is even validated" overstated what
   `SetAction` can control: System.CommandLine already parses
   `--sprint`/`--node`/`--yes`/the attempt id into `parseResult` before any
   `SetAction` callback runs at all — parsing itself cannot be skipped.
   Fixed by rewording to "before this action's own validation runs," which
   is what was actually meant and actually true.
5. The production default (`isInteractive` parameter omitted, real
   `Console.IsOutputRedirected` consulted) had zero test coverage —
   replacing the default expression with `() => true` would have shipped
   with the full suite still green, since every existing test explicitly
   overrides the parameter. Fixed with a third test (see round 2 below for
   the further fix this test itself needed).
6. A minor indentation slip in an existing test's `CreateRootCommand` call
   (introduced when the `isInteractive` argument was added) that
   `dotnet format` did not flag. Fixed.

### Round 2 review: six further gaps, all fixed

1. **The `| tee`/output-redirection false refusal** described above under
   "the technical control" — round 1's own input→output fix traded one
   false refusal for a different, narrower one, and the first version of
   this ADR never named it. Documented honestly rather than fixed (no
   portable signal exists to distinguish it), matching how the pty and
   Host-protocol bypasses are handled.
2. `CreateGateCommand`'s own doc comment still asserted "there is no
   technical caller-identity control here" verbatim — accurate before this
   ADR, stale after it, and inconsistent with `CreateAttemptCommand`'s
   sibling comment (already updated to reference ADR 0023). Fixed by
   updating `CreateGateCommand`'s comment to match.
3. `DiagnosticCodes.PermissionDenied`'s own XML doc — the constant every
   consumer actually reads — still said "standard input," carried over
   from round 1's original (pre-fix) wording. Fixed.
4. This ADR's own "at the moment the command tree is built" claim was
   false: the lambda is evaluated fresh every time a command's action
   runs, not once at tree-construction time — the exact inaccuracy round 1
   already fixed in the *code* comment, left uncorrected here. Fixed to
   match.
5. `GateApproveCommandRefusesTheRealAmbientNonInteractiveTestProcessByDefault`
   hard-asserted the ambient test process is always refused. True under
   `dotnet test`, but xunit v3 also builds a directly runnable
   `Forge.Tests.exe` — run from an actual interactive terminal,
   `Console.IsOutputRedirected` reads `false` there, and the hard-coded
   assertion becomes the wrong one, not the production code. Renamed to
   `GateApproveCommandUsesTheRealAmbientConsoleStateByDefault` and rewritten
   to compute its own expectation from the same real `Console
   .IsOutputRedirected` property the production default consults, so it
   passes under either launch environment (see round 3 below for a further
   accuracy fix to this test's own doc comment).
6. A code comment cited `Console.IsInputRedirected`'s "own already-
   established meaning" as authority for the *output* check's semantics —
   confusing after the round-1 signal switch — and separately claimed the
   value is "not cached," which risks misreading against the fact that
   `Console.IsOutputRedirected` itself is a memoized BCL property. Fixed by
   rewriting the comment to describe only what this lambda does (call
   through fresh on every invocation) without characterizing the BCL
   property's own internals.

### Round 3 (final full-scope) review: five accuracy fixes, no functional defect

Full re-trace of every CLI path reaching `ResolveGateAsync`/
`SupersedeAttemptAsync` found no gap: only the two guarded `SetAction`
callbacks reach either mutation, and no third redirection-signal problem
exists beyond the two already documented (`--instruction-file -` piping and
`| tee` output redirection). All five findings were doc/comment/test-claim
accuracy issues, several of them the same *shape* of mistake round 2 itself
made — fixed in code but left stale in prose, or in one case a claim that
does not survive careful review of a different environment:

1. The code comment's "never cached" phrasing round 2 removed as
   misleading (against `Console.IsOutputRedirected` being a memoized BCL
   property) was still present in this ADR's own prose. Fixed to match.
2. `DiagnosticCodes.PermissionDenied`'s XML doc claimed `permission_denied`
   was "reserved since Stage 8"; `git log -S permission_denied -- docs/
   contracts/v1/README.md` shows it landed in `59b2ac1`, Stage 0 — this
   ADR's own Context section already had the correct claim ("since the
   table's own introduction"), only the source-code doc comment was wrong.
   Fixed.
3. A broken sentence ("This trade the check's direction...") opening the
   round-2 false-refusal paragraph. Fixed.
4. **The most substantive finding**: the round-2 default-coverage test's
   own doc comment claimed it fails "if the default is ever replaced with
   a constant," unqualified. Under `dotnet test` — the only environment
   this suite is actually run in — `interactive` is always `false`, so
   only the `else` (refused) branch of the test's own assertion ever
   executes. A default hard-coded to `() => true` disagrees with that
   branch and is caught. A default hard-coded to `() => false` agrees with
   it by coincidence and is **not** caught — indistinguishable from correct
   behavior in this always-redirected environment. Fixed by narrowing both
   the test's own doc comment and this ADR's Consequences section (below)
   to state the real, asymmetric coverage honestly instead of the
   overclaimed symmetric one. A genuinely symmetric test would need control
   over the real ambient `Console.IsOutputRedirected` value (e.g. a
   subprocess launched with deliberately controlled redirection), out of
   proportion for this finding's own severity — named as a real, narrower
   gap rather than pretending the existing test already closes it.
5. `CHANGELOG.md`'s `## v0.44.0` entry — used verbatim as this release's
   GitHub Release description per AGENTS.md — omitted the `| tee`/
   redirected-output false refusal both this ADR and the PR body name
   explicitly. Fixed by adding it.

## Consequences

- `DiagnosticCodes.PermissionDenied` (`permission_denied`) is implemented;
  `ExitCodes.Authorization` (8) is wired into `ExitCodes.For`.
- `CliApplication.CreateRootCommand`/`CreateGateCommand`/
  `CreateGateResolveCommand`/`CreateAttemptCommand`/
  `CreateAttemptSupersedeCommand` all gain the threaded `isInteractive`
  parameter.
- 12 existing tests in `HumanGateAndSupersessionCliTests.cs` were updated to
  pass `isInteractive: () => true` explicitly, so their outcomes no longer
  depend on the test process's own ambient stdout state.
- Three new tests: `GateApproveCommandRefusesANonInteractiveSessionEvenWithYes`
  and `AttemptSupersedeCommandRefusesANonInteractiveSessionEvenWithYes`
  prove the refusal itself (exit 8, `permission_denied` on stderr, zero
  mutation calls, and — for supersede — the instruction source is never
  read at all); `GateApproveCommandUsesTheRealAmbientConsoleStateByDefault`
  proves the production default reaches the real `Console.IsOutputRedirected`
  in one direction (catches a default hard-coded to `() => true`), computing
  its own expectation instead of a launch-environment-dependent hard-coded
  one. It does not catch a default hard-coded to `() => false`, which is
  indistinguishable from correct behavior under `dotnet test`'s own
  always-redirected environment — named honestly as this test's real,
  narrower scope rather than left overclaimed.
- `SurfaceLanguageTests.DiagnosticCodesMapToTheContractExitCodes` gained the
  new `PermissionDenied → Authorization` case.
- No wire/protocol change: this is CLI presentation-layer argument handling,
  entirely upstream of `IForgeMutations` — no `ControlProtocol` kind, Host
  dispatch handler, or `capabilities.json` field changes.

## Deliberately deferred

- **A real, unforgeable caller-identity mechanism.** This ADR raises the
  bar; it does not claim to close it. Two concrete residual bypasses are
  known and named, not silently assumed away: (1) a pty-allocating agent
  still passes the console check; (2) a same-user process that speaks the
  Host's `ControlProtocol` directly via `Forge.Host.Client` — rather than
  invoking the `forge` CLI binary — bypasses this control entirely, since
  the check lives in the CLI presentation layer and the Host dispatch
  handlers (`DispatchResolveGateAsync`/`DispatchSupersedeAttemptAsync`)
  have no equivalent, and fundamentally cannot check a console that a
  background service connected over a named pipe never had. True caller
  identity (a signed human-presence token, an OS-level interactive-session
  credential, or similar, verified at the Host itself) is a substantially
  larger effort with no existing primitive in this codebase to build on.
- **A false refusal for a real human who redirects their own output.**
  `forge gate approve ... | tee log.txt` — or any other deliberate output
  redirection by an interactive human — is now refused identically to a
  non-interactive agent, since output redirection is exactly the signal
  this control checks. No portable, reliable signal in this codebase's
  neutral code today distinguishes the two cases; a future opt-in escape
  hatch (e.g. `--force-interactive`) is the likely shape of a real fix,
  not attempted in this ADR.
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
