# ADR 0020: Sprint-lifecycle CLI commands

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66 is worked as a sequence of vertical slices (ADR
0019). The first slice landed `forge gate approve|reject`/`forge attempt
supersede`; its "Deliberately deferred" section named sprint-lifecycle
commands as the next self-contained slice, since `SprintOrchestrator`
already implements `CreateSprintAsync`/`RunSprintAsync`/`ResumeSprintAsync`/
`CancelSprintAsync` with zero CLI/Host callers — the same "backend exists,
nothing wires it to a surface" shape ADR 0019 closed for the gate/supersede
pair. This ADR covers `forge sprint create|run|resume|cancel`, end to end
through the Host, following the same pattern. `rebase` has no sprint-level
backend at all (only a per-attempt worktree rebase exists,
`SprintGitIsolation.RebaseAttemptAsync`) and needs its own design; Desktop
parity, ICU localization, the snapshot-field additions, accessibility work,
and a real technical human-only control remain deferred to later slices —
see "Deliberately deferred" below.

## Decisions

### `ForgeApplication` derives version/idempotency key server-side, matching ADR 0019

`RunSprintAsync`/`ResumeSprintAsync`/`CancelSprintAsync` on
`SprintOrchestrator` each require a caller-supplied `ExpectedStateVersion`/
`IdempotencyKey` pair derived from the target sprint's *current* version
(`SprintOrchestrator.RunSprintKey`/`.ResumeSprintKey`/`.CancelSprintKey`).
The new `IForgeMutations.RunSprintAsync`/`.ResumeSprintAsync`/
`.CancelSprintAsync` wrappers take only `(projectRoot, sprintId)`, load the
sprint fresh via `SprintOrchestrator.GetSprintAsync`, and derive both values
themselves — the exact pattern `ForgeApplication.ResolveGateAsync`/
`.SupersedeAttemptAsync` already established, so a CLI/Host caller never
handles a raw version or key.

### Sprint creation cannot follow that pattern — it mints its own key instead

`CreateSprintAsync`'s own remarks explain why: the project's state version
does not change when a sprint is created, so a key derived from "the
target's current version" would forever describe "create the first sprint"
and could never create a second one — creation callers must supply their
own opaque `IdempotencyKey`. `IForgeMutations.CreateSprintAsync` mints one
with `Guid.NewGuid()` per call. This means a **stateless CLI retry of a
crashed `forge sprint create` invocation is not guaranteed idempotent** — a
second invocation with no memory of the first's key creates a *second*
sprint rather than resuming the first. This is an accepted trade-off, not a
silent gap: `CreateSprintAsync`'s own remarks already describe an
interrupted-before-`MarkSprintCreatedAsync` create as leaving "an invisible,
safely resumable sprint behind" — worst case, a crash-then-retry orphans one
inert, never-listed sprint directory, not a duplicate *visible* sprint or
corrupted state. A future slice could expose an explicit
`--idempotency-key` option for scripted/automated callers that need true
crash-safety, matching how several external CLIs (e.g. cloud provider
`create` commands) expose a client token for exactly this reason; nothing
here forecloses that.

### `run`/`resume` are not confirmable; `cancel` is — but not human-only

`capabilities.json`'s `sprint.manage` entry declares an ordinary
`workflow_mutate` permission, not one of the `human_gate_confirm`/
`human_attempt_supersede_confirm` permissions `workflow.review`/
`attempt.supersede` carry. Creating, advancing, and resuming a sprint are
additive — nothing destructive to confirm — so `CreateSprintAsync`/
`RunSprintAsync`/`ResumeSprintAsync` take no `confirmed` parameter at all.
Cancelling aborts a sprint, so `CancelSprintAsync` is confirmable the same
way `InstallIntegrationAsync`/`RemoveIntegrationAsync` are: `confirmed`
falls back to `interaction.confirm_destructive` when the CLI's `--yes` flag
is absent, unlike the gate/supersede pair's unconditional, non-bypassable
confirmation requirement.

### `forge sprint run`/`resume` share one command builder, but not one success message

Both take only `(--project-root, --sprint)` and report the resulting
`SprintSnapshot.State`. `run` advances exactly one legal hop per call
(`draft` to `ready`, then `ready` to `running` — `SprintOrchestrator`'s own
contract, unchanged here) and, when it lands `running`, the orchestrator's
existing `AdvanceGraphAsync` side effect can immediately promote further
(e.g. into `awaiting_human` if the graph opens with a gate) — so its success
message always reports whatever state the sprint actually settled at, not a
message that assumes `run` always means "now running". `resume` always
targets exactly one state (`blocked` to `ready`), so its message is fixed
text, not a state-suffixed one. An early version of this shared builder
always printed the `run`-style dynamic message for both verbs; `resume`'s
own acceptance test caught the mismatch (asserting the wrong message showed
up) before this landed, fixed by threading a `successKey`/
`includeResultingState` pair through the shared builder instead of
hard-coding `run`'s framing for both.

### The shared builder defends against a `null` `Sprint` on success

`IForgeMutations` is implemented by more than one concrete type (the local
`ForgeApplication`, `RemoteForgeMutations`, and test fakes); nothing in the
interface's own shape stops a future implementation from returning
`Succeeded: true` with `Sprint: null` (the routing acceptance test's own
`FakeForgeMutations` does exactly that today, by design — it never touches
real state). Dereferencing `result.Sprint!.State` unconditionally on
success crashed with a `NullReferenceException` the moment the routing test
exercised `run`/`resume` against that fake. Fixed by only rendering the
state-suffixed message when `result.Sprint` is not `null`, falling back to
the plain `successKey` text otherwise — the same defensive shape
`CreateSprintAsync`'s own output already needed for `result.SprintId`.

## Deliberately deferred

- **`forge sprint rebase`.** No sprint-level backend exists yet; only a
  per-attempt worktree rebase (`SprintGitIsolation.RebaseAttemptAsync`).
  Needs its own design, left to a later slice.
- **Desktop parity.** `sprint.manage` is not added to
  `CapabilityIds.Implemented` in this slice — `SurfaceParityTests` requires
  a Desktop control for every id in that list, and `rebase`'s absence means
  the capability isn't fully implemented yet regardless of Desktop work.
- **A crash-safe `forge sprint create` retry.** See the mint-a-fresh-key
  decision above — an explicit `--idempotency-key` option is the likely
  future fix, not attempted here.
- **ICU plural/select localization, a language-pack loader, and
  accessibility work** — out of scope for this slice, matching ADR 0019.
- **A real technical human-only control** — unrelated to this slice (no
  command here is human-only), still open from ADR 0019.

## Consequences

- `IForgeMutations` gains four methods; `ControlProtocol` gains
  `create_sprint`/`run_sprint`/`resume_sprint`/`cancel_sprint` kinds and
  their request/response shapes, dispatched by `ControlPlaneHostedService`
  and sent by `RemoteForgeMutations`, mirroring `resolve_gate`/
  `supersede_attempt`'s wire shape exactly.
- `ForgeApplication`'s constructor gains a `SprintOrchestrator` dependency
  (already DI-registered; no new registration needed).
- `forge sprint` gains `create`/`run`/`resume`/`cancel` subcommands
  alongside the existing `inspect` (a query, routed directly through
  `ForgeApplication`, never through `IForgeMutations`).
- New diagnostics: none — `SprintNotFound`/`ConfirmationRequired` already
  existed and are reused.

## References

- ADR 0005 (local Host and control plane)
- ADR 0019 (human-gate and attempt-supersession CLI commands — the pattern
  this slice repeats)
- `src/Forge.Runtime/Application/SprintOrchestrator.cs`
- `docs/contracts/v1/capabilities.json` (`sprint.manage`)
