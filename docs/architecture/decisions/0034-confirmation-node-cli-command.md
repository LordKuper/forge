# ADR 0034: Confirmation node CLI command

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.2.0

## Context

Stage 11's node-executor slices (ADR 0028/0030/0032/0033) shipped `intake`,
`planning`, `implementation`, and `review`. `confirmation` — the next node
in `ImplementationCriticalGraphBuilder`'s graph, gating `test_work`'s own
eligibility — is different: `ExecutionProfilePolicy.PhaseFor` returns
`null` for `NodeRole.Confirmation`, documented as "not a model phase," and
no project-level build/test-command configuration exists anywhere in this
codebase for a deterministic executor to run instead. Confirmation mirrors
AGENTS.md's own Quality gate ("confirm it against its definition of done
through inspection and execution") — an inherently judgment-based step,
not a mechanical one — so, following ADR 0019's own precedent (`forge gate
approve|reject`/`forge attempt supersede`: human-only capabilities wired
directly to `IForgeMutations`, never through a background executor), this
item ships confirmation as a CLI command a human operator invokes
directly, not an autonomous `*ExecutionHostedService`.

## Decisions

### A new `SprintScheduler.ConfirmNodeAsync` composes two existing primitives

`RecordConfirmationAsync` (built in an earlier stage, deliberately
state-independent per its own doc comment) only writes a
`ConfirmationArtifact` — it never settles the `confirmation` node's own
attempt. Since no executor exists to do that separately (unlike every
other Work role), nothing would otherwise ever move the node off
`ready`/`running`, permanently blocking `EvaluateCompletionAsync`'s
all-nodes-terminal requirement. `ConfirmNodeAsync` composes the ordinary
Work-node lifecycle (`StartAttemptAsync`/`CompleteAttemptAsync`) with
`RecordConfirmationAsync` into the one call a human-driven command needs,
mirroring `ResolveHumanGateAsync`'s own server-side version/idempotency-key
derivation (`ForgeApplication` reads the node fresh and derives both
itself — a caller supplies neither).

### The node always completes as `succeeded`, regardless of outcome

Rendering an honest judgment is `confirmation`'s whole job. A
`NotConfirmed` verdict still calls `CompleteAttemptAsync(succeeded: true,
...)` — `RecordConfirmationAsync`'s own side effect (blocking the sprint
via `TryBlockSprintAsync`) is the actual stopping point for a human, not a
reason to fail the node's own attempt. This is the same "the node's job
succeeded even though the judgment was negative" reasoning
`review`'s convergence-gate trip already established (ADR 0033) for an
unresolved verdict that still completes the attempt.

### Resumability: terminal short-circuit, not a second convergence loop

Unlike `review` (ADR 0033's multi-iteration attempt spanning many
`RecordReviewIterationAsync` calls, bounded by ADR 0006's own fourteen-
iteration budget), confirmation has no such engine and no severity-floor
budget to bound an indefinite retry loop against. `ConfirmNodeAsync` is
deliberately single-shot: an already-terminal node (`succeeded`/`failed`)
returns the most recently recorded artifact instead of re-acting, so a
stateless CLI retry after its own response was lost resolves cleanly
without re-running anything. A crash between `RecordConfirmationAsync` and
`CompleteAttemptAsync` — the one gap this does not close — leaves the node
`running`; a retry resumes the same attempt and records one harmless
duplicate artifact with the same outcome (eligibility only ever reads the
*latest* one). Named as an accepted, narrow limitation rather than solved:
the retry window is two already-durable local appends, and no
caller-visible outcome depends on which of the two near-identical
artifacts is "latest."

### `forge confirm confirmed|not-confirmed`, not `forge sprint confirm`

Matches ADR 0019's own established convention: every human-only,
non-bypassable capability gets its own top-level CLI noun (`gate`,
`attempt`), never nested under `sprint` (whose own subcommands are either
reads or ordinary config-bypassable mutations). Subcommand verbs are
`confirmed`/`not-confirmed` — `ConfirmationOutcome`'s own two values —
rather than reusing gate's `approve`/`reject` vocabulary, since this
records a definition-of-done judgment, not a gate decision. The same ADR
0023 interactive-session check (`isInteractive()`, refused before any of
the command's own argument validation) and mandatory, never-bypassed
`--yes` (no `interaction.confirm_destructive` fallback) apply, identically
to `gate`/`attempt`.

### Only one evidence entry per confirmation, this slice

`confirmation-result.schema.json` allows `evidence: []` with more than one
entry; this CLI accepts exactly one (`--evidence-kind`/`--evidence`).
Deliberately narrow rather than building the repeatable-option CLI parsing
this codebase has no other precedent for — the same "smallest primitive
now, defer the rest" discipline every other slice in this stage has
followed.

### Wire shape: `ConfirmNodeRequest` stays primitive-only

`Forge.Host.Client` has no reference to `Forge.Domain`/`Forge.Application`
(a deliberate leaf-project boundary). `ConfirmNodeRequest`'s `Outcome` is
a `bool` (`true` = `Confirmed`), and `Evidence` is a list of
`ConfirmationEvidenceEntry(string Kind, string Description)` using the
schema's own snake_case vocabulary (`"inspection"`/`"execution"`/
`"existing_check"`) directly as plain strings — not the `Forge.Domain`
enum. `RemoteForgeMutations`/`ControlPlaneHostedService`'s dispatch
handler convert to and from `ConfirmationEvidenceKind` at the boundary,
the same "domain types stay out of the leaf client project" rule
`ResolveGateRequest`/`SupersedeAttemptRequest` already established. The
response (`RecordConfirmationResult`, containing a full
`ConfirmationArtifact`) is *not* similarly restricted — it travels as-is,
matching every other mutation response in this codebase (deserialized
where `Forge.Domain` is already referenced, in `Forge.Runtime`).

### Not yet in `CapabilityIds.Implemented`

Following ADR 0019's own precedent exactly (`workflow.review`/
`attempt.supersede` shipped CLI-only first; Desktop parity landed in a
later, separate slice): `capabilities.json` documents `workflow.confirm`
now, but it is not added to `CapabilityIds.Implemented`, so
`SurfaceParityTests`' Desktop-control-parity checks do not yet require a
matching Desktop view. Desktop parity is named as deferred future work,
not silently assumed done.

## Consequences

- New `SprintScheduler.ConfirmNodeAsync`/`.ConfirmNodeKey`; first caller of
  the composition described above.
- New `IForgeMutations.ConfirmNodeAsync`, implemented identically by
  `ForgeApplication` (local) and `RemoteForgeMutations` (Host round-trip).
- New `ControlProtocol.ConfirmNodeKind`/`ConfirmNodeRequest`/
  `ConfirmationEvidenceEntry`; new `ControlPlaneHostedService` dispatch
  handler.
- New `forge confirm confirmed|not-confirmed` CLI command; new
  `DiagnosticCodes.ConfirmationEvidenceKindInvalid` (mapped to
  `ExitCodes.Usage`) for an unrecognized `--evidence-kind`.
- New `workflow.confirm` entry in `capabilities.json`, documented but not
  yet `Implemented` (no Desktop control).
- English/Russian RESX localization for the new command's description and
  success-message keys (RESX only — no ICU plural/select work, matching
  every prior CLI command in this stage).
- Explicitly **not** in this slice: Desktop parity (`Confirmation/
  RecordOutcome`, named in `capabilities.json`'s own entry); more than one
  evidence entry per confirmation; de-duplicating the narrow
  crash-between-record-and-complete retry gap named above; a real
  technical control for "human-only" (still the same gap ADR 0019 first
  named, unrelated to this item).

## References

- ADR 0005 (local Host and control plane — the mutation-routing pattern
  this item's dispatch handler follows)
- ADR 0006 (supervised execution — `NodeRole.Confirmation`'s place in the
  graph, and the AGENTS.md Quality-gate mirroring this node models)
- ADR 0018 (rate-limit deferral and attempt supersession — the resumable-
  mutation shape `ConfirmNodeAsync` follows)
- ADR 0019 (human-gate and supersession CLI commands — the direct
  precedent this item extends: CLI noun-per-capability, ADR 0023's
  interactive check, mandatory unbypassable confirmation, deferred Desktop
  parity)
- ADR 0023 (interactive-session detection — the technical control this
  item's command shares with `gate`/`attempt`)
- ADR 0033 (review node execution — the "node succeeds even on a negative
  judgment" precedent this item's own outcome handling follows)
