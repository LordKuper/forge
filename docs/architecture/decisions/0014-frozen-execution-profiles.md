# ADR 0014: Frozen execution profiles

- Status: Accepted (revised 2026-08-27)
- Date: 2026-08-17
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.13-P11.20) must freeze
exactly three execution profiles per sprint — planning, implementation, and
review — including ordered provider candidates and capability allowlists,
so that a future model node "starts without a parent transcript and
receives only its frozen context manifest and profile capabilities" and
"cannot widen them or invoke human-only commands." ADR 0006 already commits
to this shape ("The sprint snapshot resolves one profile for planning,
implementation, and review... Missing values inherit from the project model
policy before the sprint starts; running sprints never follow later
configuration changes") and to best-effort reviewer lineage independence,
delegating the selection rule to ADR 0008 ("Routing candidates are the
ordered intersection... The resolved candidate list is frozen into the
sprint profile").

`ExecutionProfile`/`ExecutionPhase`/`ExecutionLineage` already existed as a
versioned wire shape (added at Stage 8, P8.48-P8.54) with a schema
(`execution-profile.schema.json`) and one round-trip test — but, confirmed
by a repo-wide search, nothing anywhere constructed one. `SprintDefinition`
already freezes `FrozenProviders` (ADR 0008's ordered candidate list) but
carries no per-phase profile. No node executor exists yet that would call
`ILlmProvider.RunAsync` — that lands with this stage's later items
(around P11.32-P11.40's provider-protocol rework) — so this item is a pure
data/policy concern: resolve and freeze the profile *values*, not build
anything that consumes them.

Two gaps blocked a straightforward freeze:

1. **No default model exists anywhere.** No provider adapter, catalog, or
   configuration surface names a model id — `grep` for `model` across every
   configuration schema returns nothing. Inventing one in neutral code would
   violate ADR 0008's boundary ("no vendor enum/command/path in the core").
2. **`ExecutionProfile` lived in `Forge.Application`, not `Forge.Domain`.**
   Every other frozen-at-creation record `SprintDefinition` (`Forge.Domain`)
   holds — `NodeDefinition`, `SprintDependency` — is a Domain type; adding an
   `Application`-namespaced field to a `Domain` record would invert the
   layering `NodeResult`/`Finding`/`Handoff`/`ConfirmationArtifact` all
   already respect.

## Decisions

### `ExecutionProfile` moves to `Forge.Domain`

`ExecutionProfileContracts.cs` moves from `Forge.Runtime/Application` to
`Forge.Runtime/Domain` with a `Forge.Domain` namespace — a pure move, no
shape change. This lets `SprintDefinition.ExecutionProfiles`
(`IReadOnlyDictionary<ExecutionPhase, ExecutionProfile>`) exist without a
Domain-to-Application dependency, matching every other frozen-record field
on that type.

### `ILlmProvider.DefaultModel`

Rather than naming a model anywhere in neutral code, `ILlmProvider` gains
one new member, `string DefaultModel { get; }` — vendor-owned, exactly like
`Id`, implemented once per adapter: `ClaudeLlmProvider.DefaultModel =>
"sonnet"` and (as originally shipped) `CodexLlmProvider.DefaultModel =>
"gpt-5"`. These are not new vendor knowledge invented for this ADR — they
are the exact provider/model pairing `ExecutionProfileTests.cs` and
`contract-cases.json` already established as this repository's own
canonical example values when the schema was written at Stage 8. Each was a
`ponytail:`-marked fixed MVP default ("no per-project model policy
configuration exists yet") to revisit once one does, not a claim that these
are the only or best models available.

> **Revised by ADR 0063** (2026-08-27): the Codex default of `"gpt-5"` was a
> non-working placeholder — the real CLI rejects it outright. `DefaultModel`
> is no longer a fixed constant for Codex; it resolves the vendor's real
> effective, config-resolved model at runtime (`codex doctor --json`), cached
> and revalidated on read. `ExecutionProfilePolicy` no longer looks it up
> from inside `Freeze` — see the revision note below.

### `ExecutionProfilePolicy.Freeze`

`Forge.Application.ExecutionProfilePolicy.Freeze(frozenProviders,
providerCatalog)` was, as originally shipped, a pure, deterministic function
(no I/O, no clock) that builds exactly three profiles from already-frozen
inputs:

- **Planning** and **Implementation** both use `frozenProviders[0]` — the
  highest-priority already-ordered candidate ADR 0008 resolved. Nothing in
  ADR 0006 asks these two to differ, so the simplest deterministic choice
  (reuse the routing order Stage 11's callers already trust) is what this
  freezes.
- **Review** calls `SelectReviewProvider`: the first candidate in
  `frozenProviders` whose id differs from the implementation phase's
  provider, or that same provider when none differs — recording
  `ExecutionLineage.AchievedIndependence` either way. This is ADR 0006/0008's
  "best-effort... a single-provider configuration can complete review...
  recorded as diagnostic metadata, never a gate," expressed as one pure list
  scan over data Stage 11's P11.1-P11.12 predecessor items already freeze —
  no new configuration or executor needed.
- **Effort/sandbox/permission/deadlines/capability-allowlist** are fixed
  MVP defaults for all three phases (`workspace-write` / `never` /
  `[context.git_show, context.git_grep]`), with review alone using a higher
  effort (`"high"` vs. `"medium"`) and longer deadlines (3600s/300s vs.
  1800s/180s) — matching the exact values `ExecutionProfileTests.cs` already
  established as this repository's own canonical review-vs-planning
  contrast. `ponytail:`-marked: no per-project model policy configuration
  exists to source these from yet (ADR 0006 names one; nothing built it).
  Revisit once it does.
- `CapabilityAllowlist` uses `ContextCapabilityIds.GitShow`/`GitGrep` —
  the only capability ids any real code checks against today (ADR 0012's
  `GitContextReader`) — replacing the placeholder `"read_file"`/`"grep"`
  strings `ExecutionProfileTests.cs`/`contract-cases.json` had used as
  stand-ins since Stage 8; those never matched any real vocabulary.

`SprintOrchestrator.CreateSprintAsync` calls `Freeze` once, immediately
after resolving `frozenProviders`, and stores the result on
`SprintDefinition.ExecutionProfiles` — frozen exactly once, alongside every
other field that record already freezes at creation, never re-resolved
even if enablement or configuration changes while the sprint runs (ADR
0006: "running sprints never follow later configuration changes").
`FileSprintEventLog` validates each profile against
`execution-profile.schema.json` before persisting (matching
`WorkflowRecordCodec`'s existing pattern for `NodeResult`/`Finding`/
`Handoff`/`ConfirmationArtifact`) and defaults a legacy sprint's missing
`execution_profiles` to an empty set on read, the same
"shape now, tolerant of older data" rule `NodeRole` already established for
this exact file.

> **Revised by ADR 0063** (2026-08-27): `Freeze` no longer takes a
> `ProviderCatalog` or resolves `DefaultModel` internally — that overload is
> gone. `CodexLlmProvider.DefaultModel` is no longer a pure, no-I/O lookup
> once Codex's real model is resolved from a live probe, so
> `SprintOrchestrator.CreateSprintAsync` now calls
> `ExecutionProfilePolicy.ResolveModels` exactly once per sprint creation,
> before `ModelPolicyGate` validation, and passes that single resolved map
> into `Freeze` — so the model the gate approves and the model `Freeze`
> records are guaranteed identical, with no window for a concurrent
> resolution refresh to change the answer in between. `Freeze` itself is
> still pure over its inputs; only where those inputs come from changed.

### `NodeRole` → `ExecutionPhase`, and why no capability guard exists yet

`ExecutionProfilePolicy.PhaseFor(NodeRole)` maps the three model-bearing
roles (`Planning`/`Implementation`/`Review`) to their `ExecutionPhase` and
returns `null` for every other role — `Generic`, `Intake`, `Confirmation`,
`TestWork`, `HumanApproval`, `Finalization` — matching ADR 0006's
"finalization is deterministic code, not a model phase" extended to every
other non-model role in the built-in graph. This is the join key a future
node executor uses to look up which of a sprint's three frozen profiles
governs a given node; nothing yet consumes it beyond that mapping itself.

Neither half of "a node cannot widen them or invoke human-only commands"
has any enforcement code in this item, and deliberately so — both are
vacuously true today, the same way "starts without a parent transcript" is
true because no transcript concept exists yet: there is no node executor
anywhere that makes a capability *request* for something to check against
a frozen allowlist, so nothing could widen anything even if it tried. Two
earlier drafts of this ADR each added a small guard function for this
(first `IsCapabilityAllowed` with an internal human-only exclusion, then a
version with the human-only clause removed but the plain-containment check
kept); independent review found both versions had zero production callers
and, in the first version's case, an unreachable branch (a model node's
allowlist uses ADR 0012's `context.*` vocabulary while a Host-protocol
human-only command — `docs/contracts/v1/capabilities.json`'s
`workflow.review`/`attempt.supersede` — uses a disjoint one, so no code
could ever route one into the other). Both were removed rather than kept
as speculative, uncalled infrastructure: a one-line containment check has
no real weight to preserve ahead of a caller that needs it, unlike
`ConfirmationArtifact` or `Handoff`, which carry a real schema/store/codec
pipeline worth keeping ahead of their own producers. Real enforcement — for
both halves of the rule — lands once a node executor exists to make a
capability request in the first place.

### What stays deferred

- **Per-node context-manifest scoping.** `ContextManifest` (ADR 0012)
  remains sprint-scoped, not per-node. "Every model node... receives only
  its frozen context manifest" is satisfied today by there being exactly
  one immutable manifest per sprint that nothing widens after freezing —
  true per-node content differentiation has no real source to build from
  until an executor exists that needs one.
- **The node executor itself.** Nothing calls `ILlmProvider.RunAsync`
  through the interface anywhere in production code, confirmed by a
  repo-wide search; `ILlmProvider.RunAsync`'s signature (prompt as a string
  argument) also does not yet match ADR 0006's "stdin, never a command-line
  argument" decision. Both remain explicitly out of scope, landing with
  this stage's provider-protocol rework.
- **A real "project model policy" configuration surface.** ADR 0006 names
  the concept; nothing builds it. Every value this ADR cannot source from
  already-frozen data (effort, sandbox/permission policy, deadlines) is a
  fixed, `ponytail:`-marked MVP default until one exists.

## Consequences

Every sprint now freezes a real, schema-validated, provider-grounded
`ExecutionProfile` per model phase, deterministically derived from data
Stage 11's own predecessor items already freeze — no new configuration
surface, no executor, no invented vendor knowledge, and no speculative
enforcement code with nothing yet to call it. `NodeRole`/`ExecutionPhase`
gives a future executor the join key it needs to look up which frozen
profile governs a given node; "cannot widen them or invoke human-only
commands" stays honestly vacuous — true by construction, not by an
unreachable or uncalled check — until a node executor exists to make a
capability request for something to validate against.

| Action | Recovery |
|---|---|
| freeze a sprint with a single enabled provider | review profile uses the same provider; `Lineage.AchievedIndependence = false`; sprint creation still succeeds |
| freeze a sprint with `frozenProviders` empty | never reached — `SprintOrchestrator.CreateSprintAsync` already fails earlier with `sprint_provider_candidates_empty` |
| load a sprint frozen before this ADR | `ExecutionProfiles` defaults to empty; no crash |
| load a sprint whose `execution_profiles` has two entries for the same phase | rejected as a corrupt definition, not an uncaught exception |
| a frozen profile fails schema validation | `SaveDefinitionAsync` throws before anything is persisted |
