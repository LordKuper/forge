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

## References

- Plan section 7 (stop current operation), section 12.4 (acceptance criteria)
- ADR 0044 (state-machine/protocol shape this ADR implements)
- ADR 0037 (CLI-first / Desktop-parity-later precedent this ADR follows for the capability id)
