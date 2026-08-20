# ADR 0039: Injection-safety audit and defense-in-depth fix

- Status: Accepted
- Date: 2026-08-21

## Context

Stage 12's P12.16–P12.32 names "injection" as one of ~24 security/
robustness properties needing test coverage. No test existed for it, and
no ADR had ever defined what it should mean for Forge specifically —
`Rubric.cs`'s `untrusted_input` criterion governs what an AI reviewer
should flag in *generated* code, a different concern from proving Forge's
own code is injection-safe.

Investigation confirmed the design is safe by construction, not merely by
luck: a single `IProcessRunner` implementation (`ProcessRunner`,
`RuntimeAdapters.cs`) always sets `UseShellExecute = false` and always
builds arguments via `ArgumentList`, never a concatenated string —
`Process.Start` with `ArgumentList` passes each element to the OS as a
literal argv entry, with no shell parsing step for `;`, `` ` ``, `$()`, or
similar to exploit. Every `git` invocation that takes a caller-influenced
value places it after an explicit `-e`/`--` separator, defense-in-depth
against option injection even though `ArgumentList` already prevents shell
injection. Node ids — the one place a project author freely chooses a
string that later becomes a filename — are constrained to
`^[a-z0-9][a-z0-9_-]*$` by `SprintGraphValidator`, enforced once, at the
moment a graph is frozen (`SprintOrchestrator.CreateSprintAsync`). Branch
names never use raw node ids at all; `WorktreeLayout` derives them from a
GUID's own hex digits. Provider subprocess output is only ever parsed as
JSON/text and stored or displayed, never fed into a new process argument,
file path, or git command.

One real gap surfaced: `FileSprintEventLog.ReviewFloorPinPath` interpolates
a node id directly into a filename — the *only* place in that file that
does, everywhere else derives filenames from a `SprintId`/`AttemptId`/
random id — with no local check of its own. Not exploitable today (the
node id reaching this method has already passed the graph-freeze alphabet
gate), but a second, independent defense was missing exactly where the
first one is trusted to be the only guard that ever runs.

## Decisions

### Consolidate the safe-by-construction design into an explicit test suite

Rather than treating "the design already avoids the hazard" as sufficient
on its own, four new tests pin the specific mechanisms down, each proven
to actually catch a regression by a live mutation check before landing
(remove the guard, watch the test fail on the real defect shape, reinstate
it):

- `SprintSchedulerTests.AGraphWithAPathTraversalOrShellMetacharacterNodeIdIsRejected` —
  a `[Theory]` over path-traversal (`../`, absolute paths), shell-metacharacter
  (`;`, `` ` ``, `$()`), whitespace, uppercase, empty, and leading-dash node
  ids, each rejected with `DiagnosticCodes.SprintGraphInvalid` through the
  real entry point (`SprintOrchestrator.CreateSprintAsync`), not the
  validator's internals directly.
- `FileSprintEventLogTests.SetReviewFloorPinnedAsyncFailsClosedOnAPathTraversalNodeId` —
  calls the store method directly with a path-traversal node id, bypassing
  graph freezing entirely, to prove the new independent containment check
  holds on its own merits.
- `GitContextReaderTests.GitGrepTreatsAShellMetacharacterAndOptionLikePatternAsLiteralText` —
  a provider-authored (untrusted, per ADR 0006) grep pattern starting with
  `-` and containing shell metacharacters is matched as literal text, not
  misread as a `git grep` option or given shell meaning.

### `ReviewFloorPinPath` gains an independent path-containment check

Resolves the constructed path with `Path.GetFullPath` and verifies it
still starts with the intended review-iterations directory, throwing
`InvalidDataException` (matching this file's own "corrupt state fails
loud" convention) if not — the same fail-closed shape
`SprintGraphValidator`'s upstream gate already uses, applied a second time
at the one point that would actually be reachable if that upstream gate
were ever bypassed by a future change.

## Consequences

- `ReviewFloorPinPath` (private, `FileSprintEventLog.cs`) now fails closed
  independently of the graph-freeze alphabet gate.
- Four new tests, each confirmed via a live mutation check to actually
  detect the specific regression it guards against, not merely to pass.
- No behavior change for any legitimate node id, grep pattern, or process
  invocation — every fixed/tested path was already unreachable or already
  safe; this closes a latent defense-in-depth gap and documents the
  reasoning for the rest, rather than fixing a live vulnerability.
- Deliberately not attempted in this slice, named rather than silently
  dropped: a `ProcessRunner`-level argv-fidelity canary test (the property
  is already exercised incidentally by many existing tests — git branch
  names with special characters, the real-child-process environment
  isolation test — so a dedicated test would add limited marginal proof
  for its cost) and a full "hostile project" end-to-end scenario spanning
  the whole attempt lifecycle (the four targeted tests above already prove
  the actual mechanisms directly, at far lower cost and fragility risk).

## References

- ADR 0006 (supervised execution — "Forge sends prompts through redirected
  standard input, never a command-line argument... shell invocation
  remains forbidden," the design principle this audit confirmed holds)
