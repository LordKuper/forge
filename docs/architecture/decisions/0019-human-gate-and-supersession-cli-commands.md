# ADR 0019: Human-gate and attempt-supersession CLI commands

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.56-P11.66) is a broad item:
"Complete CLI/TUI and Desktop projections, commands, attention navigation,
human gates, recovery, English/Russian localization, configuration editors,
accessibility, and parity tests." A repo-wide survey before starting found:

- No separate TUI project exists — `docs/architecture/overview.md` and every
  ADR treat "CLI/TUI" as one surface; `Forge.Cli.csproj`'s `Terminal.Gui`
  reference is unused scaffolding from Stage 1. CLI work satisfies this
  item's "CLI/TUI" half directly.
- The Desktop is a single flat page (`MainPage.xaml`) with no navigation and
  no per-entity detail views. Building attention navigation, a gate view,
  and accessibility for it is the largest single piece of this item.
- `SprintScheduler.ResolveHumanGateAsync` (human gates) and
  `.SupersedeAttemptAsync` (ADR 0018) both have zero production callers —
  no CLI command, no Desktop control, nothing in `IForgeMutations`.
  `capabilities.json` already reserves their shapes (`workflow.review`,
  `attempt.supersede`), and ADR 0018 explicitly assigned "which command
  surfaces expose `attempt.supersede` and to whom" to this item.
- `SurfaceParityTests.DesktopExposesEveryImplementedCapability`/
  `DesktopControlsAreWiredInCodeBehind` throw `KeyNotFoundException` the
  moment a capability id is added to `CapabilityIds.Implemented` without a
  matching Desktop control — flipping a capability to `Implemented`
  therefore requires Desktop parity to land in the same change.
- No item in this plan, closed or open through Stage 13, is titled "build
  the node executor" that would call `ILlmProvider.RunAsync` — every prior
  Stage 11 item deferred it. This item's own text ("the snapshot fields...
  which only this stage's executor first produces") presumes one lands
  somewhere in this item's scope, but building a real attempt-execution
  loop is an order of magnitude larger than CLI/Desktop wiring and belongs
  to its own scoped effort.

Given the size, this item is worked as a sequence of vertical slices
(matching how P8.25-P8.33 closed across three passes — Reopened, Progress,
Closed), each its own reviewed PR, with the plan checkbox staying open and
carrying a `Progress` note until every slice lands. This ADR covers the
**first slice only**: `forge gate approve|reject` and
`forge attempt supersede`, end to end through the Host. Sprint-lifecycle
commands (`create`/`run`/`resume`/`cancel`/`rebase`), the snapshot-field
additions (integration status, phase profile), Desktop parity, ICU
localization, and accessibility work are explicitly deferred to later
slices of this same plan item — see "Deliberately deferred" below.

## Decisions

### The CLI mutation, not the domain layer, resolves version and idempotency key

`ResolveHumanGateAsync`/`SupersedeAttemptAsync` both require a caller to
present an `expectedVersion`/idempotency key computed from the target
entity's *current* version — but `EntityStatus` (the snapshot projection
`forge status`/`tree`/`sprint inspect` already expose) carries no raw
version field, and adding one is its own contract change with its own
blast radius (every snapshot consumer, the JSON schema, `SurfaceFormatting`
parity). Rather than expose it, `ForgeApplication.ResolveGateAsync`/
`.SupersedeAttemptAsync` load the target's current
`NodeSnapshot`/`AttemptSnapshot` fresh from `ISprintStore` and derive the
version and key themselves — exactly what
`SurfaceParityTests`/`ResolveHumanGateAsync`'s own resumability comment
already does in-process. A CLI or Host caller therefore supplies only
`(sprint id, node id, approved)` or `(sprint id, attempt id, instruction)` —
the two mutating methods on `IForgeMutations` this item adds — never a
version or key of their own.

### No config-driven confirmation bypass

Every other confirmable mutation on `IForgeMutations`
(`InstallIntegrationAsync`/`RemoveIntegrationAsync`, and
`InitializeProjectAsync`) treats `confirmed: false` as provisional: if
`interaction.confirm_destructive` is configured `false`, the call proceeds
anyway. `ResolveGateAsync`/`SupersedeAttemptAsync` do not use that bypass —
`confirmed` must be `true` outright, or the call returns
`DiagnosticCodes.ConfirmationRequired` unconditionally. `interaction.confirm_destructive`
is itself a project-scope configuration value reachable through
`forge config project`, which an agent could set; letting a human-only
command's confirmation requirement be silently disabled through ordinary
configuration would defeat the requirement rather than merely relax it.
`SupersedeAttemptAsync`'s own domain-level `confirmed` parameter (already
present since ADR 0018) is threaded straight through — the CLI's `--yes`
flag is the only thing that can set it `true`.

### Human-only enforcement is surface omission, not caller identity

ADR 0005 requires attempt supersession and gate resolution to be
"human-only," but neither this codebase nor the Host control protocol has
any concept of caller identity — `ControlHandshakeRequest` carries a
protocol/client version and an instance id, never a principal, and the
`"permission"` field every capability declares in `capabilities.json` is
documentation only; nothing reads it to authorize a call. Inventing a
caller-identity field now — human vs. agent, with the Host trusting
whatever a connecting client self-reports — would be a false sense of
enforcement: a client can claim to be anything.

The mechanism this item uses instead, already present by construction:
`IntegrationSourceCompiler.BuildBody` (the generator behind every
`CLAUDE.md`/`AGENTS.md` Forge writes) emits only a fixed preamble, the
testing-invariant paragraph, and the project's own `.forge/rules`/
`.forge/knowledge` documents — it enumerates no CLI commands at all, so
`forge gate`/`forge attempt supersede` are never mentioned in agent-facing
text regardless of whether they exist. Combined with the mandatory,
non-bypassable confirmation above, an agent operating strictly from its
generated integration text has no path to either command, and an operator
invoking them directly always passes through the same explicit `--yes`
gate ADR 0005 requires. This is a structural absence, not a checked
permission — nothing needs to keep re-verifying it, and no future
capability-enumerating change to the compiler can silently regress it
without itself being a reviewed change to `IntegrationSourceCompiler`.

### `--sprint`/`--node`/`--instruction-file` are not literally in `capabilities.json`'s `cli` strings

`capabilities.json` documents `forge gate <approve|reject>` and
`forge attempt supersede <attempt-id> --instruction-file <path|->` — neither
mentions `--sprint`, the same way no capability's `cli` string mentions
`--project-root` even though every command accepts it. `--node` defaults to
`human_approval`, the one `NodeKind.HumanGate` node
`ImplementationCriticalGraphBuilder` ever produces, so the common case needs
no explicit node id; a caller with a non-canonical graph can still override
it. `--instruction-file` accepts a real path or `-` for standard input,
matching the documented `<path|->` alternative — reading is delegated to a
small helper (`ReadInstructionAsync`) that treats a missing file, a
permission error, and an over-length instruction as the same class of "no
usable bounded instruction was supplied" outcome, since
`SupersedeAttemptAsync` itself has no separate "instruction unreadable"
diagnostic to distinguish them.

### `NodeActionResult`/`CompleteAttemptResult` travel on the wire unchanged

Rather than invent minimal wire-specific response DTOs, the Host dispatch
handlers (`resolve_gate`/`supersede_attempt`) serialize the same
`NodeActionResult`/`CompleteAttemptResult` records `SprintScheduler` already
returns, using `StatusJson.Options` — the exact precedent
`GetProjectSnapshotKind`'s own dispatch handler already set by sending the
full `ProjectSnapshot` (a much larger nested type) over the wire as-is.
Request payloads (`ResolveGateRequest`, `SupersedeAttemptRequest`) stay
primitive-only (`Guid`/`string`/`bool`), matching every existing request
record, since `Forge.Host.Client` is a leaf project with no reference to
`Forge.Domain`/`Forge.Application`.

## Deliberately deferred

- **Sprint-lifecycle CLI commands** (`forge sprint create|run|resume|cancel`).
  `SprintOrchestrator` already implements all four; wiring them is a
  separate, self-contained slice of this same plan item, not bundled here
  to keep this PR reviewable. `rebase` has no sprint-level backend at all
  today (only a per-attempt worktree rebase exists,
  `SprintGitIsolation.RebaseAttemptAsync`) and needs its own design.
- **Desktop parity.** `SurfaceParityTests` requires a Desktop control for
  every id in `CapabilityIds.Implemented`; neither `workflow.review` nor
  `attempt.supersede` is added to that list in this slice, so the backend
  and CLI wiring here lands without yet claiming the capability
  "implemented" — that claim, and the Desktop navigation/gate view it
  requires, is later work.
- **The two snapshot fields this plan item names** (integration status,
  phase profile) and the CLI/Desktop projections that would read them —
  unrelated to gates/supersession, left to a later slice.
- **The node executor.** Nothing in this slice drives an attempt through
  `Preparing`/`Running`/`Validating` — `forge attempt supersede` cancels
  whatever a future executor would have been running and creates a linked
  replacement for that same future executor to pick up, exactly as ADR
  0018 already designed it.
- **ICU plural/select localization, a language-pack loader, and
  accessibility work** — out of scope for this slice; the two new commands
  use the existing flat `Resolve(key)` RESX catalog like every command
  before them.
- **`ExitCodes.For`'s incomplete diagnostic-to-exit-code mapping.** ADR
  0018 already declined to fix this project-wide; this item's new
  diagnostics (`node_not_found`, `attempt_terminal`,
  `supersession_instruction_too_long`, etc.) fall through to
  `internal_error` (13) the same way several pre-existing codes already do,
  left to whichever future item closes that gap comprehensively.

## Consequences

- `ResolveHumanGateAsync`/`SupersedeAttemptAsync` (both zero-caller since
  their own introducing stages) have their first production callers.
- `attempt.supersede`'s "human-only" requirement is satisfied by
  construction (generated integration text enumerates no commands) plus
  mandatory, non-bypassable confirmation, not by a caller-identity check
  this codebase has no infrastructure for.
- `IForgeMutations` grows two members; both `ForgeApplication` and
  `RemoteForgeMutations` implement them identically to every existing
  mutation's local/remote split.
- Neither new capability is yet marked `Implemented` in
  `CapabilityIds` — the backend and CLI half of this item's promised
  parity exists; the Desktop half does not yet, tracked as the next slice.

## References

- ADR 0005 (local Host and control plane — the mutation-routing pattern,
  human-gate/supersession contract requirements this item implements)
- ADR 0006 (supervised execution — review convergence's own human gate,
  not yet wired to a command; left for a later slice)
- ADR 0018 (rate-limit deferral and attempt supersession — the domain
  primitive this item's `attempt.supersede` command finally calls)
