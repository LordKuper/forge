# ADR 0047: Stop-current-operation implementation

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.7.0

## Context

ADR 0044 recorded the state-machine and protocol shape for `StopCurrentOperation` (plan section 7)
and explicitly deferred the coordinator, registry, process/worktree wiring, resume behavior, crash
recovery, and surfaces to Slice 2. This ADR records the decisions Slice 2's implementation actually
had to make that ADR 0044 left open, and one deliberate deviation from the slice's own task
description.

## Decisions

### Live cancellation and durable-intent convergence are two separate mechanisms, not one

`ActiveOperationRegistry` (`Forge.Application`) is an in-memory `AttemptId -> CancellationTokenSource`
map. Each of `PlanningExecutionHostedService`/`ImplementationExecutionHostedService`/
`ReviewExecutionHostedService` (the three executors that invoke `ILlmProvider.RunAsync`) registers
the exact attempt before constructing its `AttemptSupervisor`, passing the registered token in place
of the tick's own token, and unregisters in `finally`. `StopOperationCoordinator.RequestStopAsync`
calls `ActiveOperationRegistry.TryCancel` only after the stop intent is already durable
(`ISprintStore.AppendAttemptStopRequestedAsync`), matching plan section 7.3's ordering exactly.

Cancelling the registered token unblocks `ProcessRunner.RunAsync`'s existing
`Process.Kill(entireProcessTree: true)` (already reached by any cancellation of the token passed
into `ILlmProvider.RunAsync`) with no new process-tree-kill code. The executor's own
`AttemptTerminationReason.Cancelled` handling is deliberately **not** changed to special-case a stop
in the moment it happens: on the very next tick, the node is still `Running` with a `CurrentAttemptId`
whose `AttemptSnapshot.StopRequestedAt` is now set, and each executor's own new check (below)
converges it. This means a live stop and a stop recovered after a Host crash go through the *exact
same* convergence code path — there is no separate "live" cancellation-handling branch to keep in
sync with restart recovery.

### `AttemptSnapshot.StopRequestedAt` is a folded projection, not audit-only

Unlike `AttemptSuperseded` (recorded but never projected into `AttemptSnapshot` — pure audit),
`AttemptStopRequestedType` **is** folded (`WorkflowFold.Apply`) into a new
`AttemptSnapshot.StopRequestedAt` field. Every node-role executor (including `IntakeExecutionHostedService`,
which invokes no provider at all) checks this field directly at the top of its own tick, immediately
after its existing `node.State is not (Ready or Running)` gate: a `Running` node whose current
attempt already carries a stop intent calls `StopOperationCoordinator.FinishStopAsync` instead of
resuming/starting the provider. This is the one mechanism that satisfies plan section 7.3's
"executors and restart recovery must check the intent before starting or resuming an attempt" for
every executor, live or freshly restarted, without a second recovery pass.

`ReviewExecutionHostedService`'s check is deliberately narrower in effect than it looks: an ordinary
unresolved `ChangesRequested` verdict also leaves the node `Running` between review iterations, and
must keep doing so — it is unaffected because `StopRequestedAt` is `null` for that attempt.

### `StopOperationCoordinator.FinishStopAsync` never calls `CompleteAttemptAsync`

Plan section 7.2 requires the re-arm be "represented by a dedicated event/revision rule, not by
pretending the provider failed." `FinishStopAsync` appends `AttemptChanged` (`Running`/`Validating`
-> `Cancelled`, message key `workflow.attempt_stopped`), then `NodeChanged` (`Running` -> `Failed`,
`workflow.node_stopped`; `Failed` -> `Ready`, `workflow.node_rearmed`), then `SprintChanged`
(`Running` -> `Paused`, `workflow.sprint_paused`) directly through `ISprintStore.AppendTransitionAsync`
— the same primitive `SprintScheduler.SupersedeAttemptAsync` already uses for its own re-arm steps,
never `SprintScheduler.CompleteAttemptAsync`. Since `NodeSnapshot.AttemptCount` only advances when a
node's `running` transition itself carries a fresh `attempt_number` (see `WorkflowFold.Apply`), and
none of these three appends does, the stop consumes no automatic-retry budget: `MaxAutomaticRetries`
accounting is never consulted for this path at all, structurally, not by a numeric exemption.

Each step is gated on current durable state (`attempt.State is Running or Validating`, then
`node.State == Running`, then `node.State == Failed`, then `sprint.State == Running`) rather than on
whether this exact call already ran a given step — the same idempotent-resumption discipline
`SprintScheduler.SupersedeAttemptAsync` already uses, which is what makes `FinishStopAsync` safe to
call again after a Host crash lands between any two of its steps.

### `StopOperationCoordinator.RequestStopAsync`'s rejection reasons map onto existing diagnostics plus two new ones

- "no active operation exists" and "the caller targets another project's Host" both reduce to
  existing mechanisms: `DiagnosticCodes.SprintNotFound` (a sprint absent from this Host's own store)
  already covers the second case with no new code, since `ForgeApplication.StopCurrentOperationAsync`
  resolves the project root exactly like every other mutation here — it never trusts a caller-supplied
  root to select a different project's Host. A sprint present but not `Running` is the new
  `DiagnosticCodes.NoActiveOperation`.
- "the target attempt has already settled" reuses `DiagnosticCodes.AttemptTerminal`.
- "the active attempt changed before validation" is the new `DiagnosticCodes.ActiveOperationChanged`,
  returned when the requested attempt is not the owning node's current, `Running` attempt — this is
  also what rejects a stale stop targeting an attempt superseded or retried since the caller's last
  read (plan section 12.4).
- "the expected version is stale" has no separate client-supplied version on the wire (matching
  `SupersedeAttemptRequest`'s own "no expected version travels on the wire" precedent, ADR 0044): the
  Host derives every version fresh from its own read, so this reduces to `AppendTransitionAsync`'s
  ordinary optimistic-concurrency conflict inside `FinishStopAsync`'s own steps, never a
  client-observable rejection at request time in this CLI-only slice.
- A repeated identical stop request is idempotent by checking `AttemptSnapshot.StopRequestedAt`
  first, before any of the above: once an intent is durable, `RequestStopAsync` short-circuits to
  success (re-issuing a best-effort `TryCancel`) rather than re-validating a snapshot that may have
  legitimately moved on (the attempt settled, the node re-armed, the sprint paused) since the
  original call.

### `SprintOrchestrator.ResumeTarget` maps `Paused` exactly like `Blocked`/`Failed`

No special-cased "restart the interrupted attempt" resume path exists. `Paused -> Ready` joins the
same `ResumeTarget` case `Blocked`/`Failed` already use; a separate `forge sprint run` (unchanged)
carries `Ready -> Running`. `StartAttemptAsync` always mints a genuinely fresh attempt id and a fresh
attempt-worktree branched from the integration worktree's *current* tip for a `Ready` node — plan
section 7.1's "starts a fresh attempt from the current integration base" holds with zero new resume
logic, matching ADR 0044's own reasoning for why this mapping was deferred rather than added
speculatively in Slice 1.

### Deviation from the slice's task description: `workflow.stop_operation` stays out of `CapabilityIds.Implemented`

The task description asked to "promote `workflow.stop_operation`... to `CapabilityIds.Implemented`."
This ADR deviates from that instruction and leaves the capability reserved (`capabilities.json`'s
`note` field updated to describe the CLI-only state, contract version 1.6.0 -> 1.7.0), because:

- `capabilities.json`'s own top-level rule is `"public_requires_both_surfaces": true`.
- ADR 0037 is explicit precedent for exactly this situation: `workflow.confirm`/`workflow.test_work`/
  `workflow.finalize` each "shipped a CLI-only human-only command and explicitly deferred Desktop
  parity... the capability landed in `capabilities.json` and `IForgeMutations`, but not in
  `CapabilityIds.Implemented`" until a *later* ADR added Desktop parity. ADR 0044 itself already
  states the same plan for this capability: "Desktop wiring in Slice 6 per the established CLI-first
  rhythm — ADR 0037's own precedent."
- `SurfaceParityTests.DesktopControls` is a fixed dictionary keyed by `CapabilityIds.Implemented`;
  `DesktopExposesEveryImplementedCapability`/`DesktopControlsAreWiredInCodeBehind` index it
  unconditionally. Adding `workflow.stop_operation` to `Implemented` without also shipping Desktop
  controls (explicitly out of scope for this slice) throws `KeyNotFoundException` in both tests
  rather than failing an assertion — there is no way to satisfy both the literal instruction and a
  clean build/test run without building Desktop UI this slice deliberately excludes.

No `CapabilityIds.WorkflowStopOperation` constant was added either, matching how none of the six
Slice-1-reserved ids (`workspace.summary`, etc.) has one until it graduates.

## What stays deferred

- Desktop `SprintWorkspace/StopOperation` UI and `CapabilityIds.WorkflowStopOperation` (Slice 6).
- `AssessStageTransition`/`MoveSprintToStage` and the stage-revision evaluator (Slice 3, ADR 0045/0046).
- Any OS-specific process-tree enumeration: the existing `ProcessRunner.RunAsync`'s
  `Process.Kill(entireProcessTree: true)` (neutral `Forge.Runtime` code) already satisfies plan
  section 7.1's "process execution terminates the entire owned process tree," so no OS-adapter code
  was needed for this slice.

## Consequences

- `AttemptSnapshot` gains a `StopRequestedAt` field, always `null` for every event stream that never
  appends `AttemptStopRequestedType` — every existing production path and test fixture is unaffected.
- `ISprintStore` gains `AppendAttemptStopRequestedAsync`; both existing implementers
  (`FileSprintEventLog`, `FlakySprintStore`) and test fakes implementing the interface directly were
  updated to match.
- `IForgeMutations` gains `StopCurrentOperationAsync`; both fakes in `TestEnvironment.cs`
  (`FakeForgeMutations`, `DisposableFakeForgeMutations`) and `RemoteForgeMutations` implement it.
- `IntakeExecutionHostedService`, `PlanningExecutionHostedService`, `ImplementationExecutionHostedService`,
  and `ReviewExecutionHostedService` each gained two constructor dependencies
  (`ActiveOperationRegistry`/`StopOperationCoordinator`, or just the latter plus
  `IConfigurationRegistry` for Intake, which never registers a live operation); every direct test
  construction site was updated alongside the production DI registration in `ForgeHost.AddForgeCore`.

## Addendum: independent review findings (PR #95, round 1)

An independent review agent found four genuine saga-correctness defects in the design above before
merge. Fixing them changed the design in ways worth recording here rather than only in the commit
history.

### Findings 1 and 2 shared one root cause: the convergence check was gated on `node.State == Running`

The original per-executor check ("a `Running` node whose current attempt already carries a stop
intent") could only ever see the node *before* `FinishStopAsync`'s node-stopped/node-rearmed steps
ran. Once a Host crash landed after either step, the node was `Failed` or already `Ready` on
restart — a state the check no longer matched — and nothing else in this codebase ever revisits a
node once it leaves `Running` on its own:

- **Finding 1**: a crash between the node-stopped append (`Running` -> `Failed`) and the
  node-rearmed append (`Failed` -> `Ready`) left the node durably `Failed` with the sprint still
  `Running`. `EvaluateCompletionAsync`'s own "stuck" check does not fire either (it only treats a
  `Failed` node as stuck once `AttemptCount` exhausts `MaxAutomaticRetries`, which the stop path
  deliberately never advances) — the sprint wedged permanently.
- **Finding 2**: a crash between the node-rearmed append and the sprint-paused append left the node
  `Ready` with the sprint still `Running`. The next tick's stop-check no longer matched (node not
  `Running`), so execution fell through to the ordinary `StartAttemptAsync` path, minting a **new**
  attempt and silently spending the automatic-retry budget the stop was meant to preserve.

**Fix**: the check no longer reads `node.State` at all. It reads `node.CurrentAttemptId` (set once
by the node's own `running` transition and never cleared by any later transition in this codebase,
including every step `FinishStopAsync` itself appends — verified against `WorkflowFold.Apply`) and
then the *attempt's* own durable state: `StopRequestedAt is not null && StopConvergedAt is null`.
This resolves correctly regardless of whether the node is `Running`, `Failed`, or already `Ready`,
closing both crash windows with one generalized rule instead of two special cases. Each of
`IntakeExecutionHostedService`/`PlanningExecutionHostedService`/`ImplementationExecutionHostedService`/
`ReviewExecutionHostedService` now runs this check immediately after resolving its own node (before
either the `sprint.State != Running` or the `node.State is not (Ready or Running)` gate — both now
sit *after* it, since a node with an unconverged stop can legitimately be in any state those gates
would otherwise skip).

`AttemptSnapshot.StopConvergedAt` is a new folded projection (`WorkflowEvent.AttemptStopConvergedType`,
message key `workflow.attempt_stop_converged`), appended by `FinishStopAsync` as its own last,
unconditional step, after the sprint-pause step whether or not that step actually fired. Without it,
the generalized check above would re-fire forever once the saga finished, including after an
unrelated later `resume_sprint` put the sprint back in `Running` — re-pausing it spuriously. This one
marker is what lets the check be keyed purely off "has this exact attempt's stop finished," fully
independent of every other field's own, unrelated later history.

`FinishStopAsync`'s own internal steps needed no reordering and no new gating: each already re-reads
current durable state before acting (attempt non-terminal, then `node.State == Running`, then
`node.State == Failed`, then `sprint.State == Running`), so it was already safe to call redundantly
from any of its own intermediate states, including a `Ready` node. The defect was entirely that
nothing durable-state-independent ever called it again after the two crash windows above.

### Finding 3: `RequestStopAsync` validated a snapshot without a lock, then appended without re-checking it

`RequestStopAsync` read and validated the target attempt (sprint `Running`, attempt non-terminal,
`node.CurrentAttemptId` matches) and only afterward called
`ISprintStore.AppendAttemptStopRequestedAsync` — with no lock held across the two calls. A concurrent
`SprintScheduler.CompleteAttemptAsync`/`SupersedeAttemptAsync` landing in that window could move the
attempt off being the node's current, live operation; the stop intent would still attach to the now-
stale attempt, nothing would ever converge it, and the caller would still see success.

**Fix**: `AppendAttemptStopRequestedAsync` now takes `expectedAttemptVersion` and re-validates it
against the attempt's *current* version inside its own per-sprint critical section (the same
optimistic-concurrency discipline `AppendTransitionAsync` already applies, and the same one
`SupersedeAttemptAsync` already relies on for its own compound operation) — reusing the store's
existing per-sprint lock rather than introducing a new locking primitive, per the review's own
suggestion. A version mismatch returns `AppendOutcome.Conflict`; `RequestStopAsync` reports
`DiagnosticCodes.ActiveOperationChanged` and does not call `ActiveOperationRegistry.TryCancel` — a
clean rejection, never a silently-stuck stop intent reported as success.

### Finding 4: `capabilities.json` documented `[--sprint <id>]` as optional; the CLI defines it `Required = true`

Fixed the documented string to `--sprint <id>` (no brackets), matching the real, already-shipped
`CreateAttemptStopCommand`. Making `--sprint` genuinely optional (resolving the sprint from the
attempt id alone) was considered and rejected: attempt ids are not indexed across sprints anywhere in
`ISprintStore` today, so resolving one would mean scanning every sprint's full event journal on every
stop request — a real complication for no clear benefit, when the caller already knows which sprint
it is targeting in every existing surface.

This drift was invisible to `SurfaceParityTests.CliExposesEveryDocumentedCapabilityCommand` for two
independent reasons: that test only walks `CapabilityIds.Implemented`, and `workflow.stop_operation`
is deliberately excluded from it (this ADR's own decision, above); and even for a capability it does
walk, it only ever checked that a documented `--option` exists somewhere in the command tree, never
whether its *documented bracket optionality* matches the option's real `Required` value. A new test,
`SurfaceParityTests.StopOperationDocumentedCliOptionsMatchTheirActualRequiredness`, checks the second
property for this one capability specifically (not generalized to every reserved capability): most of
the others reserved alongside it have no CLI command at all yet, and a couple of long-implemented,
unrelated commands elsewhere in this contract (`init --project-root`, `doctor --bundle`) have their
own pre-existing, unrelated bracket/`Required` drift that is not this PR's concern to fix.

## References

- Plan section 7 (stop current operation), section 12.4 (acceptance criteria)
- ADR 0044 (state-machine/protocol shape this ADR implements)
- ADR 0037 (CLI-first / Desktop-parity-later precedent this ADR follows for the capability id)
- Independent review of PR #95, round 1 (four findings fixed by this addendum)
