# ADR 0035: Test-work node CLI command

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.4.0

## Context

`test_work` is the next node in `ImplementationCriticalGraphBuilder`'s
graph after `confirmation` (PR #75/ADR 0034), depending on it and gating
`review`. Like `confirmation`, `ExecutionProfilePolicy.PhaseFor` returns
`null` for `NodeRole.TestWork` too ("not a model phase"), and it mirrors
another part of AGENTS.md's own Quality gate: "identify the smallest
risk-based set of new tests that protects the scope... A no-new-test
decision is allowed only when the change adds no behavior or existing
checks cover every material risk; justify it." This is genuinely
greenfield: ADR 0013 (the original DAG/confirmation-gate ADR) explicitly
declined to model it — "What a test-work node then does with a `Confirmed`
verdict — select tests, or record a justified no-new-test decision — is
not modeled here" — and nothing since has built a domain type, schema, or
scheduler method for it. Following ADR 0034's own precedent (a human-only
CLI command, not an autonomous executor) and its exact design shape
(decision-flip protection and stale-artifact handling built in from the
start this time, rather than discovered across two review rounds) closes
that gap.

## Decisions

### `TestWorkOutcome`/`TestWorkArtifact`, deliberately simpler than confirmation's

Two values (`TestsAdded`, `NoNewTestsJustified`), one free-text
`Justification` field, no structured evidence list — `confirmation-result
.schema.json`'s `evidence[]` (kind + description) is not mirrored here.
AGENTS.md's own rule just asks for a justification, not categorized
evidence types; matching `ConfirmationEvidence`'s shape here would add
structure nothing in this CLI's own vocabulary needs, the same
"deliberately narrow" discipline ADR 0033 (review) applied to skip located
findings and ADR 0034 applied to skip multiple evidence entries.

### One combined `SprintScheduler.RecordTestWorkAsync`, not a two-primitive split

Confirmation's own `RecordConfirmationAsync` (state-independent, no node
lifecycle) plus `ConfirmNodeAsync` (the composing orchestrator) split
existed because `RecordConfirmationAsync` predates this session by a
stage and was already covered by its own tests before `ConfirmNodeAsync`
needed to reuse it. Nothing else needs a state-independent
"record only" test-work primitive, so this item builds
`RecordTestWorkAsync` as the one method directly — composing
`StartAttemptAsync`/`CompleteAttemptAsync` around a private `store
.SaveTestWorkAsync` call — with **no** separate lower-level method to
accidentally leave under-protected. Every property ADR 0034's two review
rounds established for `ConfirmNodeAsync` is present here from the first
commit: server-derived version/idempotency key
(`RecordTestWorkKey`/`ForgeApplication.RecordTestWorkAsync`), the
already-terminal short-circuit checks the caller's outcome before
returning (never silently returns a stale, mismatched artifact), the
`running`-resume branch has the identical decision-flip check, and the
reuse-instead-of-record shortcut is gated on `resuming` specifically (a
fresh attempt — including one right after a supersession re-armed the
node to `ready` — always records anew regardless of any stale artifact
left by an earlier, abandoned attempt on the same node id).

### No downstream eligibility gate reads the recorded artifact

Unlike `confirmation` (whose `Confirmed` artifact content gates
`test_work`'s own eligibility via `IsTestWorkEligibleAsync`), nothing
reads `TestWorkArtifact`'s `Outcome` to decide anything else.
`review`'s own graph dependency on `test_work` is satisfied by the node
reaching `succeeded` alone (`AdvanceGraphAsync`'s ordinary
dependency-completion check) — recording either outcome and completing
the node is unconditionally this node's whole job. This makes
`RecordTestWorkAsync` simpler than `ConfirmNodeAsync` in exactly the one
place `IsTestWorkEligibleAsync`'s own logic would otherwise have had to
be duplicated or generalized for a second artifact type.

### `forge test-work added|no-new-tests`, not `forge sprint test-work`

Same top-level-noun convention ADR 0019/0034 established for every
human-only, non-bypassable capability. Subcommand verbs
(`added`/`no-new-tests`) match `TestWorkOutcome`'s own two values, the
same reasoning `confirm`'s `confirmed`/`not-confirmed` pair already used
over gate's `approve`/`reject` vocabulary. The same ADR 0023
interactive-session check and mandatory, never-bypassed `--yes` apply.

### `--justification` validated CLI-side before the attempt starts

Applying ADR 0034's own round-1 finding proactively rather than waiting
for review to find it again: an empty/whitespace-only `--justification`
is rejected (new `DiagnosticCodes.TestWorkJustificationRequired`) before
`resolveMutations`/`RecordTestWorkAsync` ever runs, so `StartAttemptAsync`
never durably moves the node to `running` for input that was always going
to be rejected by `test-work-result.schema.json`'s own `minLength: 1`.

### Not yet in `CapabilityIds.Implemented`

Same precedent as `workflow.confirm`: `capabilities.json` documents
`workflow.test_work` now (bumping `contract_version` to `1.4.0`, following
the established one-MINOR-bump-per-addition pattern), but it is not added
to `CapabilityIds.Implemented` — CLI-only this slice, Desktop parity
deferred and named as future work.

## Consequences

- New `TestWorkOutcome`/`TestWorkArtifact` (`src/Forge.Runtime/Domain/TestWork.cs`),
  `docs/contracts/v1/schemas/test-work-result.schema.json`,
  `ISprintStore.SaveTestWorkAsync`/`.GetTestWorkAsync` (and their
  `FileSprintEventLog`/`WorkflowRecordCodec` implementations).
- New `SprintScheduler.RecordTestWorkAsync`/`.RecordTestWorkKey`/
  `.GetTestWorkAsync`.
- New `IForgeMutations.RecordTestWorkAsync`, implemented identically by
  `ForgeApplication` (local) and `RemoteForgeMutations` (Host round-trip).
- New `ControlProtocol.RecordTestWorkKind`/`RecordTestWorkRequest`; new
  `ControlPlaneHostedService` dispatch handler.
- New `forge test-work added|no-new-tests` CLI command; new
  `DiagnosticCodes.TestWorkJustificationRequired` (mapped to
  `ExitCodes.Usage`), documented in `docs/contracts/v1/README.md`'s
  diagnostics table.
- New `workflow.test_work` entry in `capabilities.json`, documented but
  not yet `Implemented` (no Desktop control); `contract_version` bumped
  to `1.4.0`.
- English/Russian RESX localization for the new command.
- Explicitly **not** in this slice: Desktop parity
  (`TestWork/RecordOutcome`, named in `capabilities.json`'s own entry);
  `finalization`, the one remaining Work role in the built-in graph with
  no executor or command; a real technical control for "human-only"
  (still the same gap ADR 0019 first named, unrelated to this item).

## References

- ADR 0005 (local Host and control plane — the mutation-routing pattern
  this item's dispatch handler follows)
- ADR 0006 (supervised execution — this graph's overall shape)
- ADR 0013 (implementation-critical DAG and confirmation gate — the ADR
  that explicitly deferred this item's own scope)
- ADR 0018 (rate-limit deferral and attempt supersession — the resumable-
  mutation shape this item's own composition follows)
- ADR 0019 (human-gate and supersession CLI commands — the CLI-noun,
  ADR-0023-check, mandatory-confirmation precedent this item extends)
- ADR 0023 (interactive-session detection — the technical control this
  item's command shares with every other human-only command)
- ADR 0033 (review node execution — `test_work`'s only dependent; its own
  executor bypasses `test_work` entirely and reads implementation's
  handoff directly, unaffected by this item)
- ADR 0034 (confirmation node CLI command — the direct precedent and
  template this item mirrors structurally, including the decision-flip
  protection and stale-artifact handling both of that ADR's review rounds
  established, applied here from the start)
