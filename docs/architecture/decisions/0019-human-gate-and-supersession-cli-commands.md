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

### `ResolveHumanGateAsync`/`SupersedeAttemptAsync` had to be fixed to actually support this

Independent review found that the design above breaks the exact
resumability both scheduler methods were built to provide (ADR 0018). Both
recognized a resumed call only by recomputing the *same deterministic hash*
the original call used — for `ResolveHumanGateAsync`, seeded with the
node's version *at the moment of the original decision*; for
`SupersedeAttemptAsync`, an idempotency key derived from the attempt's
version at that same moment. A caller with no memory of that original call
— exactly what `ForgeApplication`'s server-side derivation above produces
on every genuinely new invocation — reads the target's *current* state
instead, which has already moved once the original call got anywhere at
all. The recomputed hash then never matches, so a crash between two
sequential durable steps inside either method (the two node transitions in
`ResolveHumanGateAsync`'s approved path; the cancel append and everything
after it in `SupersedeAttemptAsync`) left the target permanently wedged:
every later, independent retry recomputed a *different* hash from the
now-advanced state, missed the store's own idempotency-key-based replay
detection, and fell through to a hard failure
(`node_transition_invalid`/an illegal `cancelled -> cancelled` transition)
instead of resuming.

Both are now fixed in `SprintScheduler.cs` itself, not in the new
`ForgeApplication` wrapper, since the defect is in the resumability
contract those methods offer their callers, not in how this item calls
them:

- **`ResolveHumanGateAsync`** now finds the gate's own attempt by linkage
  instead of recomputing a hash. Getting the linkage itself right took two
  more review rounds after the first attempt landed:
  - A first fix (`state.Attempts.Values.FirstOrDefault(a => a.NodeId ==
    nodeId)`) assumed a human-gate node has at most one such attempt, ever,
    and that any attempt carrying the node's id must be a gate-resolution
    attempt. Both assumptions are false. `StartAttemptAsync` stamps the
    same `NodeIdArgument` on an ordinary `Work` node's own attempts, so the
    lookup matched *any* node's live attempt — `forge gate approve` against
    a `Work` node with an in-progress attempt would hijack and fraudulently
    "succeed" it. And a rejected gate can be retried back to
    `awaiting_human` for a second decision (`RetryNodeAsync`), which the
    single-attempt premise did not account for: the lookup would resolve to
    the first, terminal attempt from the *earlier* round, silently
    discarding the fact that a second decision was ever made.
  - The fix now checked at the top of the method, before any attempt
    lookup, that the target node is actually a
    `Forge.Domain.NodeKind.HumanGate` (refusing with
    `DiagnosticCodes.NodeKindMismatch` otherwise) — closing the
    cross-node-kind hijack outright, the same way `StartAttemptAsync`
    already gates on `NodeKind.Work`. Linkage itself is now gated on the
    node's *current* state: attempts for this node only ever get created
    from `awaiting_human` onward, so as long as the node is still sitting
    at `awaiting_human`, no earlier round's attempt is ever eligible —
    resumability there is handled entirely by the deterministic-hash fresh
    path, which naturally reproduces the same attempt id for a same-round,
    same-version retry. Once the node has moved past `awaiting_human`,
    linkage picks the most recently updated attempt linked to the node
    (`OrderByDescending(a => a.UpdatedAt)`), which is always the current
    round's — earlier rounds each leave their own, now-stale, terminal
    attempt linked to the same node id, and `UpdatedAt` breaks that tie
    deterministically instead of relying on `Dictionary` enumeration order.
  - The decision-flip protection the hash coincidentally provided (approve
    and reject hashed to different ids, so a caller flipping the decision
    could never accidentally resume the other one) is now explicit instead
    of incidental: once the node's state reveals which way the current
    round's decision went (`running`/`succeeded` only ever follow
    `approved`; `failed` only ever follows a rejection), a resumed call
    supplying the opposite `approved` value is refused as a conflict rather
    than silently reinterpreting the original attempt.
- **`SupersedeAttemptAsync`** already recognized "already superseded" by
  the target's own `Cancelled` state (a fix from ADR 0018's own review
  history) — but still *re-attempted* the cancel append unconditionally,
  with whatever (possibly stale) version/key the caller supplied, relying
  entirely on the store's idempotency-key ledger to recognize a genuine
  replay before it. A freshly-recomputed key never matches that ledger, so
  the append fell through to `IsLegalTransition`, which has no
  `cancelled -> cancelled` edge. The cancel append itself is now skipped
  entirely once already superseded, matching every other step in this
  method, each of which already independently checks current state before
  acting.

Both fixes carry their own regression test — a caller re-deriving
version/key fresh from already-advanced state must still resolve cleanly,
and (for the gate) a caller flipping the decision that way must still be
refused.

### `Forge.Cli.ExitCodes.For` now maps the three supersession-instruction codes

`supersession_instruction_too_long` (pre-existing), and the two new
`supersession_instruction_unreadable`/`_required` codes this item adds, are
documented in `docs/contracts/v1/README.md` as category 2 (usage, exit 2),
but `ExitCodes.For`'s `switch` had no case for any of the three — all three
fell through to the catch-all `Internal` (exit 13). That mismatch was latent
before this item (nothing produced these codes on a real CLI path yet);
`forge attempt supersede` makes all three reachable for the first time, so
it is fixed here rather than left latent. `ExitCodes.For`'s broader,
pre-existing gap for other diagnostic codes is unrelated and stays out of
scope, matching ADR 0018's own precedent of not attempting a project-wide
fix incidentally to an unrelated change.

### No config-driven confirmation bypass

Every other confirmable mutation on `IForgeMutations`
(`InstallIntegrationAsync`/`RemoveIntegrationAsync`, and
`InitializeProjectAsync`) treats `confirmed: false` as provisional: if
`interaction.confirm_destructive` is configured `false`, the call proceeds
anyway. `ResolveGateAsync`/`SupersedeAttemptAsync` do not use that bypass —
`confirmed` must be `true` outright, or the call returns
`DiagnosticCodes.ConfirmationRequired` unconditionally. `interaction.confirm_destructive`
is itself a user-scope configuration value reachable through
`forge config user`, which an agent with shell access could still set;
letting a human-only command's confirmation requirement be silently
disabled through ordinary configuration would defeat the requirement
rather than merely relax it.
`SupersedeAttemptAsync`'s own domain-level `confirmed` parameter (already
present since ADR 0018) is threaded straight through — the CLI's `--yes`
flag is the only thing that can set it `true`.

### Human-only enforcement does not exist as a technical control yet — named honestly as a gap

ADR 0005 requires attempt supersession and gate resolution to be
"human-only," but neither this codebase nor the Host control protocol has
any concept of caller identity — `ControlHandshakeRequest` carries a
protocol/client version and an instance id, never a principal, and the
`"permission"` field every capability declares in `capabilities.json` is
documentation only; nothing reads it to authorize a call. Inventing a
caller-identity field now — human vs. agent, with the Host trusting
whatever a connecting client self-reports — would be a false sense of
enforcement: a client can claim to be anything.

An earlier version of this section argued that `IntegrationSourceCompiler`
never enumerating CLI commands into generated `CLAUDE.md`/`AGENTS.md` text
was itself a meaningful barrier. Review found that reasoning unsound on two
counts: `IntegrationSourceCompiler.AppendSection` copies a project's own
`.forge/rules`/`.forge/knowledge` document bodies into the generated file
*verbatim* — a project-authored rule can name and instruct
`forge gate approve --yes` explicitly, with no compiler change required —
and `forge --help` enumerates both commands regardless, discoverable by any
agent that runs it. Command-omission from *generated* text is not a barrier
an agent operating in this repository is actually confined by.

What this item ships instead, honestly described: there is **no technical
mechanism in this slice that distinguishes a human invocation from an agent
invocation.** Both new commands require an explicit `--yes` with no
config-driven bypass, which raises the bar against an *accidental* or
*automatic* invocation (an agent following its ordinary workflow, with
nothing telling it to pass `--yes` for this specific command, will not
trigger it by accident) but does not — and is not claimed to — stop an
agent that has been explicitly instructed (by a project rule, or by an
operator's own prompt) to run it. ADR 0005's "human-only" requirement is,
for this slice, a **project-level policy**, enforced the same way this
repository's own AGENTS.md rules are enforced on Claude Code and Codex
today: by instruction and convention, not by a cryptographic or
authorization boundary. A real technical control — some form of caller
identity the Host actually verifies, distinct from a capability an
end-to-end connected client merely claims to have — is out of scope for
this slice and is named as future work, not silently assumed solved.

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

- **A real technical control for "human-only."** This slice has none — see
  above. A future item must design an actual caller-identity or
  authorization mechanism (or explicitly accept policy-only enforcement as
  the permanent answer, which is a materially different, and weaker,
  posture than ADR 0005's own wording implies) before this gap can be
  considered closed rather than merely documented.
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
  their own introducing stages) have their first production callers, and
  both are now genuinely crash-resumable for a stateless caller — the gap
  their first real callers exposed and this item fixed.
- `attempt.supersede`/`workflow.review`'s "human-only" requirement is, for
  this slice, a project-level policy (mandatory, non-bypassable
  confirmation) rather than an enforced technical boundary — named
  honestly as a gap, not silently assumed solved.
- `IForgeMutations` grows two members; both `ForgeApplication` and
  `RemoteForgeMutations` implement them identically to every existing
  mutation's local/remote split.
- Neither new capability is yet marked `Implemented` in
  `CapabilityIds` — the backend and CLI half of this item's promised
  parity exists; the Desktop half does not yet, tracked as the next slice.
- Two new diagnostic codes (`supersession_instruction_unreadable`,
  `supersession_instruction_required`) distinguish an unreadable
  instruction source from an over-length or empty one; the instruction
  itself is now read bounded (never more than
  `SprintScheduler.MaxSupersessionInstructionLength + 1` characters from
  either a file or standard input) rather than buffered whole before the
  bound is checked.

## References

- ADR 0005 (local Host and control plane — the mutation-routing pattern,
  human-gate/supersession contract requirements this item implements)
- ADR 0006 (supervised execution — review convergence's own human gate,
  not yet wired to a command; left for a later slice)
- ADR 0018 (rate-limit deferral and attempt supersession — the domain
  primitive this item's `attempt.supersede` command finally calls)
