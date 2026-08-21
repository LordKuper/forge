# ADR 0042: `forge eval` and a project model-policy gate

- Status: Accepted
- Date: 2026-08-21

## Context

Stage 12's P12.9–P12.15 asks for "updater/provider/bootstrap/workflow
evaluations and model-policy gates that run through existing commands; keep
evaluation orchestration out of presentation code." `capabilities.json`
already reserves the capability (`quality.evaluate`, `cli: forge eval`,
`desktop: Quality/RunEvaluation`, `acceptance: evaluation-parity`) with no
implementation behind it. `ExecutionProfilePolicy`'s own `ponytail` comment
independently names the second half: "no per-project model policy
configuration exists yet (ADR 0006 describes one; nothing built it)." This
item closes both gaps in one slice, scoped to what has a concrete MVP
acceptance case today.

## Decisions

### `forge eval` reuses `StartupPipeline`'s existing checks; it does not re-probe

`StartupPipeline.RunAsync` (already the backing of `forge doctor --startup`
and every project overview) already produces one `StartupCheck` per area:
`UserConfiguration`/`Language`/`Platform`/`ProjectRoot`/`ProjectConfiguration`
(bootstrap), `Providers` (provider, including its bounded local probe and
conditional install/repair — the reason this capability's reserved
permission is `execute_tools_confirm`, not a read-only one), and
`UpdateStrategy`/`Release` (updater). `RunEvaluationAsync` re-buckets these
into named `EvaluationArea`s rather than building parallel probing logic —
"runs through existing commands" taken literally, matching
`CollectDiagnosticBundleAsync`'s own precedent of reusing `GetOverviewAsync`
rather than a second code path.

`Release` stays `Skipped`/`update_check_deferred` in `forge eval` too, not
just at startup: `StartupPipeline` hardcodes that value unconditionally
(the code comment names the real "on demand" caller as `forge update`
itself, Stage 2's own lifecycle — a mutation with composition-root-injected
delegates `ForgeApplication` never receives, not a second read). Performing
a genuine on-demand release check here would need those same delegates
threaded into neutral core, contradicting ADR 0007's OS-adapter boundary for
no concrete MVP gain over what `forge update`'s own exit code already
reports — named as deliberately out of scope rather than silently
overclaimed.

### Workflow area: a structural self-check, not a sprint

Nothing in `StartupPipeline` touches workflow correctness. `forge eval`
builds the canonical `ImplementationCriticalGraphBuilder` graph and runs it
through `SprintGraphValidator.IsValid` (pure, already used by
`SprintOrchestrator.CreateSprintAsync`) and confirms every schema id
`ContractSchemas` embeds loads without throwing — both real, existing,
side-effect-free checks, exercised with no project and no sprint created.

### Model-policy gate: a per-provider allowlist, not full per-phase model selection

ADR 0006 describes a fuller "project model policy" (per-phase model
overrides) that nothing has built. Building that is a separate, larger
feature with its own design questions (which phases can diverge, how a
provider's own model catalog is discovered) and no MVP acceptance case yet.
What P12.9–P12.15 actually asks for — a **gate** — has a narrower, concrete
shape available today: a project can restrict which model id is acceptable
per enabled provider, and sprint creation is refused before any state is
written if the provider's fixed `ILlmProvider.DefaultModel` violates it.

New project-scoped configuration key `models.allowed_models`: an optional
array of `"<provider_id>:<model_id>"` strings (reusing
`ConfigurationSchemaCodec`'s existing `GetOptionalStringArray`/
`Add(..., IReadOnlyList<string>?)` machinery verbatim — no new typed
accessor needed). A provider with zero entries in the list is unrestricted
(the "no per-project model policy configured" default every sprint has had
until now); a provider with one or more entries may only resolve to a
listed one. `ModelPolicyGate.IsAllowed` is a pure function
(`Application/ModelPolicyGate.cs`) called from two places: `forge eval`'s
`ModelPolicy` area (a dry-run report, no sprint needed) and
`SprintOrchestrator.CreateSprintAsync`, immediately after `frozenProviders`
is resolved and before any event is appended — the same fail-closed
placement `SprintProviderCandidatesEmpty` already uses for the adjacent "no
routable provider" case. New `DiagnosticCodes.ModelPolicyViolation`
(`model_policy_violation`), documented in `docs/contracts/v1/README.md`
under category 11 (workflow), matching every other sprint-creation-blocking
code's category.

### CLI-only in this slice; Desktop parity deferred, matching established precedent

`capabilities.json`'s `quality.evaluate` entry requires eventual CLI/Desktop
parity, but every human-only or read capability added across Stage 11 shipped
CLI-first and added Desktop parity in a later, separately reviewed slice
(`workflow.confirm`/`workflow.test_work`/`workflow.finalize`, closed by ADR
0037 well after their own CLI-only ADRs). `forge eval` follows the identical
rhythm: `quality.evaluate` is *not* added to `CapabilityIds.Implemented` yet,
and `SurfaceParityTests` is left untouched. Desktop's `Quality/RunEvaluation`
page is deliberately out of this slice.

### Orchestration lives in `ForgeApplication`, not `CliApplication`

`RunEvaluationAsync` joins `ForgeApplication` directly, matching
`CollectDiagnosticBundleAsync`'s own precedent ("the single entry point both
surfaces use") and the item's own "keep evaluation orchestration out of
presentation code" requirement. `CreateEvaluateCommand` in `CliApplication.cs`
does nothing but parse, call, and print — no new dependencies beyond
`ForgeApplication` itself.

### Not routed through the Host

Like `forge doctor --bundle`, this is a read (report), not a mutation — ADR
0005's "one Host writer" rule governs mutations, not reads. `forge eval`
resolves directly against the local `ForgeApplication`, same as every other
read command.

## Consequences

- New `docs/contracts/v1/schemas/evaluation-result.schema.json`
  (`evaluation-result` contract, `schema_version` `"1.0.0"`).
- New `Application/EvaluationContracts.cs`
  (`EvaluationReport`/`EvaluationCheck`/`EvaluationArea`/`EvaluationState`),
  `Application/ModelPolicyGate.cs` (pure), and
  `ForgeApplication.RunEvaluationAsync`.
- New project configuration key `models.allowed_models`
  (`ConfigurationRegistry`, `ConfigurationSchemaCodec` — `project-manifest`
  `schema_version` `1.1.0` → `1.2.0`, an older manifest still validates with
  the key entirely absent, matching `context.token_budget`'s own precedent).
- New `DiagnosticCodes.ModelPolicyViolation`, applied in
  `SprintOrchestrator.CreateSprintAsync` and documented in
  `docs/contracts/v1/README.md`.
- New `forge eval [--area <name>]` CLI command.
- Explicitly **not** in this slice, named rather than silently absorbed:
  Desktop parity (`Quality/RunEvaluation`, `CapabilityIds.Implemented`); full
  per-phase model selection/override (ADR 0006's larger "project model
  policy" concept — this slice ships only the allowlist gate half with a
  concrete MVP case); an `EvaluationCompleted` control event
  (`capabilities.json` names one, but no read capability in this codebase
  persists a control event today either — `forge doctor --bundle`'s own
  `DiagnosticBundleCreated` is equally unwired, and this follows that exact
  precedent rather than being the first read to invent one).

## References

- ADR 0006 (supervised execution and review convergence — "the project model
  policy" source text)
- ADR 0038 (`forge doctor --bundle` — the `CollectDiagnosticBundleAsync`/
  per-section precedent this reuses)
- ADR 0029 (`context.token_budget` — the project-scoped optional-key
  precedent this reuses)
- `ExecutionProfilePolicy`'s own `ponytail` comment, the source of this
  item's "revisit once it does" pointer
