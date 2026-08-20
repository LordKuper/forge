# ADR 0030: Planning node execution

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.0.0

## Context

ADR 0028 shipped the first node executor (`intake`) and named every
model-bearing role (`planning`, `implementation`, `review`) as still
unexecuted: they need prompt assembly, the provider I/O protocol (ADR
0016), attempt deadlines and process-tree teardown (ADR 0017), and worktree
isolation (ADR 0004) — each already designed, none wired to a real caller.
`ILlmProvider.RunAsync`, `SprintGitIsolation`'s whole lifecycle, and
`SprintScheduler.RecordHandoffAsync` all still had zero production callers
before this slice.

A general model-bearing executor is not one slice — implementation and
review each still need real code-producing/reviewing behavior, a git
commit primitive this repository does not have yet, and
`SprintGitIsolation.IntegrateAsync`/`RebaseAttemptAsync`. `planning` is the
narrowest model-bearing role: the built-in `implementation-critical` graph
depends on it directly after `intake`, and its job — per ADR 0009's own
framing of "sprint-scoped specifications/decisions" — is to reason about
the change and hand off a plan, not to produce or commit code. That makes
it possible to execute honestly without inventing a commit mechanism this
slice does not need.

## Decisions

### A new `PlanningExecutionHostedService`, mirroring `IntakeExecutionHostedService`'s shape

`src/Forge.Host.Runtime/PlanningExecutionHostedService.cs`: same options
record (test-overridable poll interval, 15s default), same `PeriodicTimer`
loop, same per-sprint failure isolation and no-per-sprint-memory /
crash-resumability discipline, registered and started/stopped by
`ControlPlaneHostedService` alongside its siblings, only after the Host
wins the project lease. The node is found by `NodeRole.Planning` +
`NodeKind.Work`, never by the `"planning"` string literal, for the same
reason `intake`'s executor already established.

`context.token_budget` resolution (ADR 0029) is extracted from
`IntakeExecutionHostedService` into a shared `Forge.Application
.TokenBudgetResolver`, a behavior-preserving refactor now that a second
real caller needs the identical read.

### The provider runs inside an isolated, throwaway worktree that makes no commit

`SprintGitIsolation.EnsureIntegrationWorktreeAsync` (idempotent, once per
sprint) then `CreateAttemptWorktreeAsync` (fresh per attempt) provision the
provider's working directory — the first production use of ADR 0004's
whole lifecycle. The prompt explicitly instructs the provider not to edit,
create, or delete any file: planning's product is reasoning, not code, so
there is nothing to commit and nothing for
`IntegrateAsync`/`RebaseAttemptAsync` to reconcile. The attempt worktree is
unconditionally discarded (`DiscardAttemptAsync`) once the provider run
settles, regardless of outcome — clean replay, matching every other
attempt's worktree lifecycle, with no git-commit primitive needed at all.
If a provider disregards the instruction and writes a file anyway, the
change is contained to the throwaway worktree and discarded with it.

### The prompt concatenates the intake-admitted rules/knowledge already in memory

`ContextManifestCompiler.Compile` is called again (idempotent, matching
`intake`'s own resume story) to get the current admitted-item list and its
digest as `input_digest`; the prompt body is built from those same
admitted items' `ForgeDocument.Body` text, read from the identical
`ForgeDocumentCompiler.ParseAsync` pass the manifest was compiled from —
no second disk read, no new templating system. A fixed header states the
phase and the no-file-edit instruction.

### `StartAttemptAsync` already owns routing; a missing profile/provider is refused before starting

`SprintScheduler.StartAttemptAsync` internally routes any role with a
frozen `ExecutionProfile` via `RoutingLedger.DecideAsync` and refuses with
the mapped diagnostic (deferred, budget-exhausted, circuit-open) if not
routed — this executor makes no routing decision of its own. But
`StartAttemptAsync` only routes *when a profile is found*; for a
(malformed) definition missing a planning profile or naming an
unregistered provider, it would silently skip routing and still move the
node to `running` with nothing to complete it. This executor checks both
before calling `StartAttemptAsync`, leaving the node untouched at `ready`
on failure — safe to retry once the definition is fixed, matching
`intake`'s own blank-identity guard.

### The durable outcome distinguishes idle/session timeout, ordinary provider failure, and Host shutdown

`AttemptSupervisor` (ADR 0006/0017) wraps the `provider.RunAsync` call with
the profile's frozen session/idle deadlines. Its
`AttemptTerminationReason` maps to three different executor behaviors:

- **`IdleTimeout`/`SessionTimeout`** — recorded as a failed attempt with
  the long-reserved `ProviderDiagnosticCodes.IdleTimeout`/`SessionTimeout`
  (implemented here for the first time; the two codes existed since Stage
  8 as `docs/contracts/v1/README.md` "reserved" entries).
- **`Cancelled`** — the caller's own token fired (Host shutting down), not
  a provider or infrastructure failure. `AttemptSupervisor.SuperviseAsync`
  swallows this into a returned result rather than throwing, so the
  executor must recognize it explicitly: it skips both the worktree
  discard (the same token would make a `git worktree remove` call with it
  throw) and `CompleteAttemptAsync` entirely, leaving the node `running`
  for the next tick's resume — the identical crash-resumability story
  `intake` already relies on. A resumed planning attempt re-invokes the
  provider from scratch (no partial transcript is preserved), which is
  honestly a wasted turn, not a correctness gap, since the worktree is
  throwaway and the provider is instructed to make no durable change.
- **`None`** — the provider's own `ProviderRunResult` is authoritative.
  Every `ProviderFailureKind` gets its own durable diagnostic code (new
  `ProviderDiagnosticCodes.RunNotReady`/`QuotaExceeded`/
  `RunPolicyViolation`/`RunTransientFailure`/`RunMalformedOutput`/
  `MissingTerminalResult`/`DuplicateTerminalResult`/`RunUnknownFailure`),
  rather than one generic code collapsing every non-timeout failure. An
  exception the provider adapter does not itself catch (e.g. the process
  could not be launched) is converted to `ProviderFailureKind.Unknown`
  at the `provider.RunAsync` call site itself, kept out of this service's
  own per-sprint catch filter, which is tuned for durable-state corruption
  shapes (ADR 0028's eleven-instance history), not process-launch
  failures.

### An empty terminal summary is a failure, not a degraded success

ADR 0016: neither vendor guarantees non-empty terminal-result text. A
schema-valid success (zero exit, one terminal-result event) with a
blank/whitespace-only `Summary` would violate `handoff.schema.json`'s
`summary` `minLength: 1` if handed to `RecordHandoffAsync` — recorded
instead as a failure with the new `ProviderDiagnosticCodes
.EmptyTerminalSummary`, distinct from every other `ProviderFailureKind`
because the provider itself never reported failing.

### The Handoff is recorded after the node succeeds, best-effort

`SprintScheduler.RecordHandoffAsync`'s own first production call: on
success, `Summary` becomes the handoff's summary, `BaseSha` is the
sprint's frozen `BaseCommit`, `Decisions`/`OpenRisks` stay empty (parsing
structured decisions/risks out of free-text provider output is real,
separate design work this slice does not attempt), and `NextNodeIds` names
the graph's `implementation`-role node when one exists. Recorded strictly
after `CompleteAttemptAsync` succeeds, not before or atomically with it: a
crash in the narrow window between the two leaves a succeeded node with no
handoff yet — named explicitly as accepted debt (no retry of this
best-effort write from here), the same "leaked resource left for a future
pass" shape a failed worktree discard already accepts, rather than a
second durable-write mechanism invented for this one caller.

## Consequences

- New `src/Forge.Host.Runtime/PlanningExecutionHostedService.cs` and
  `PlanningExecutionOptions`; registered in `ForgeHostApplication` and
  `ControlPlaneHostedService` alongside `IntakeExecutionHostedService`.
- New `src/Forge.Runtime/Application/TokenBudgetResolver.cs`, extracted
  from `IntakeExecutionHostedService` (behavior-preserving); `IntakeExecutionHostedService
  .DefaultTokenBudget` now re-exports `TokenBudgetResolver.DefaultTokenBudget`.
- Nine new `ProviderDiagnosticCodes` entries mapping every remaining
  `ProviderFailureKind` (plus the empty-summary case) to a durable
  diagnostic code; `IdleTimeout`/`SessionTimeout` reach production for the
  first time.
- First production callers of `ILlmProvider.RunAsync`,
  `SprintGitIsolation.EnsureIntegrationWorktreeAsync`/
  `CreateAttemptWorktreeAsync`/`DiscardAttemptAsync`, and
  `SprintScheduler.RecordHandoffAsync`.
- `tests/Forge.Tests/Support/TestEnvironment.cs` gains an optional
  `IWorktreeManager` override (mirroring the existing `IRepository`
  override) and two new fakes (`FakeRunnableLlmProvider`,
  `FakeWorktreeManager`) so a node executor's own orchestration can be
  tested without a real provider process or `git.exe` — the real thing
  stays covered by `GitIsolationTests`.
- Explicitly **not** in this slice, named rather than silently absorbed:
  `implementation`/`review` executors (need a git-commit primitive and
  `IntegrateAsync`/`RebaseAttemptAsync`, neither built yet);
  `forge sprint rebase` and the two snapshot fields re-scoped from
  P8.25–P8.33 (still blocked on the same code-producing executor path);
  structured decision/risk extraction from provider output;
  `ContextManifestCompiler.WithQueryResults` (layer 4, needs a
  model-proposed query plan); a real unforgeable caller-identity
  mechanism (unrelated to this slice, still open from ADR 0023); atomic
  node-result-plus-handoff recording.

## References

- ADR 0004 (worktree isolation)
- ADR 0006 (supervised execution — deadlines, termination reasons,
  provider failure/routing policy)
- ADR 0009 (Forge document format — the rules/knowledge content this
  prompt carries, and "sprint-scoped specifications/decisions" as
  planning's own described output)
- ADR 0012 (reproducible context assembly)
- ADR 0014 (frozen execution profiles)
- ADR 0016 (provider stdin/environment/bounded-streaming protocol —
  `ProviderTerminalResult`'s own no-guaranteed-non-empty-summary fact)
- ADR 0028 (intake node execution — the sibling executor this one
  mirrors structurally, and the eleven-instance corrupt-state review
  history this service's own catch filter reuses)
- ADR 0029 (context token budget configuration)
