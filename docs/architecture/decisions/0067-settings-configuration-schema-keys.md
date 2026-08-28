# ADR 0067: Approval, theme, provider-priority and per-model-effort configuration keys

- Status: Accepted
- Date: 2026-08-28
- Contract version: `configuration.json` 1.5.0 -> 1.6.0; `user-config.schema.json` `schema_version`
  gains `1.4.0`

## Context

`docs/plans/desktop-design-parity-execution.md` finding G1: the Forge settings page the design calls
for binds to four settings that have no configuration key behind them — approval mode, theme,
provider priority, and per-model effort. Slice S4 supplies those keys; the page itself is S18.

This slice is deliberately **schema, validation, and resolution only**. Every one of the four keys
lands with **no consumer**, and the "Consumption status" section below records that per key so a
later slice's author does not have to re-derive it. The plan's Part 4 decisions Q22, Q23 and Q24
(shape only) bind the choices.

## Decisions

### Key names follow the existing group-prefix convention, not the design's labels

Every key in `ConfigurationRegistry` is `<group>.<name>`, and each group is one object in
`user-config.schema.json`. The four new keys join existing groups rather than inventing top-level
names: `interaction.auto_approve_gate` beside `interaction.confirm_destructive`,
`providers.priority` beside `providers.enabled`, `shell.theme` beside `shell.sidebar_collapsed`, and
a new user-scoped `models` group for `models.effort`.

`shell.theme` in particular is *not* a bare `theme`: there is no single-segment key anywhere in this
registry, and a theme is a per-installation shell appearance preference, exactly like
`shell.sidebar_collapsed`.

`models.effort` is user-scoped while the project manifest's `models.allowed_models` is
project-scoped. The dotted prefix is shared but the stores are not, so no collision exists: how hard
a model should think is an operator preference (Q23: user scope only), while which models are
acceptable is project policy.

### "Approval mode" is `interaction.confirm_destructive` plus one new boolean, not a tri-state enum

Per Q22, the design's "Ask on write / Auto / Autonomous" control does not become a literal enum.
Forge's execution profiles freeze `PermissionPolicy = "never"` and the human gate is mandatory, so a
three-valued approval mode would be describing states Forge cannot enter. The setting decomposes
into the already-shipped `interaction.confirm_destructive` and one new boolean,
`interaction.auto_approve_gate`, defaulting to `false` — the behavior every prior release shipped.

It is deliberately a *separate* key rather than an extra value of `confirm_destructive`, because the
two govern different layers: `confirm_destructive` governs surface confirmations, while this governs
the workflow's own human-approval node.

### The human gate is hard-mandatory today; this key is a placeholder, and wiring it is not a small change

**This is the safety-relevant finding of this slice.** A full inspection of the gate machinery found
**no existing skip, bypass, or auto-approve path** — no configuration flag, capability flag,
environment variable, or test-only hook. Concretely:

- `SprintScheduler.AdvanceGraphAsync` promotes **every** `NodeKind.HumanGate` node to
  `awaiting_human` with no predicate other than the node's kind.
- `WorkflowStateMachines` declares `AwaitingHuman -> [Running, Failed, Cancelled]`. There is no
  `AwaitingHuman -> Succeeded` edge and no `AwaitingHuman -> Skipped` edge, so a gate that has been
  reached cannot be skipped at all.
- `SprintScheduler.ResolveHumanGateAsync` is the only exit, has exactly two branches (approve /
  reject), and takes the decision from the caller; no Host executor calls it.
- `ForgeApplication`'s gate path deliberately omits the `confirmed || !RequiresConfirmationAsync()`
  fallback that `InitializeProjectAsync` uses, with a comment giving the reason:
  `interaction.confirm_destructive` is a value an agent could itself set through `forge config`.
- `EvaluateCompletionAsync` treats a `Failed` HumanGate as immediately stuck, bypassing the
  automatic-retry budget, so a rejected gate cannot auto-retry its way out.
- `SprintScheduler.SkipNodeAsync` has no `NodeKind` guard, but its only production caller
  (`StageTransitionCoordinator`) refuses any predecessor whose `Optional` flag is false, and the
  built-in `implementation-critical` graph declares no optional node.

ADR 0014 places gate authority in a vocabulary disjoint from the frozen profile's
`capability_allowlist`: `capabilities.json`'s `workflow.review`, whose sibling `workflow.finalize`
carries the note "stays human-only with no config-driven confirmation bypass".

**Therefore `interaction.auto_approve_gate` is declared, validated, and resolved, and nothing reads
it.** No enforcement was wired in this slice, because there is no existing, tested seam to wire it
into — building one means adding a genuine bypass to a safety-critical gate, which belongs in its own
slice with its own review, not in a schema slice.

One trap for whoever builds that slice: `StageTransitionAssessor.NodeSucceededWithLiveEvidence`
returns `true` for `NodeState.Skipped`. That is unreachable today (no `Skipped` edge from
`AwaitingHuman`, and `Optional` is always false), but it means that making the gate skippable would
*silently* satisfy the `HumanApproved` stage prerequisite as a side effect. It is the single
load-bearing assumption behind the gate's current guarantee.

### `light` is valid configuration with no palette behind it

Per Q24 (shape only), `shell.theme` accepts `dark`, `light`, and `system`, and defaults to `dark`.
`App.xaml` declares dark tokens only, and the light ramp is an external design deliverable, so
`light` and `system` are values a user can legitimately store today that **no rendering code
honours**. That is intentional: S24 owns the palette and the switching logic, and this slice ships
neither. The settings UI (S18) must not present the choice as effective before S24 lands.

### `providers.priority` is validated against the provider catalog, not against `providers.enabled`

The plan's Q23 phrases priority as ordering the enabled providers. It is nonetheless validated the
same way `providers.enabled` is — every id must exist in the composed `ProviderCatalog`
(`ForgeApplication.RequireRegisteredProviders`, extended to cover both keys), with duplicates
rejected by the schema's `uniqueItems`.

It is deliberately **not** additionally required to be a subset of the current `providers.enabled`
selection. The two keys are written independently, so a subset rule would make one of the two write
orders impossible — raising priority before enabling a provider, or disabling one before re-ordering
the rest. The eventual consumer intersects priority with the effective enabled set at read time,
where both values are known at once.

Unlike `providers.enabled`, priority draws no omitted-vs-empty distinction: empty means "no
preference", which is the registration order every release has used, so its default is `[]` rather
than `null`.

### The effort enum is the ladder the provider layer already understands

`models.effort` is an object mapping model id to an effort level. The levels are not a fresh
vocabulary: `ProviderEffortLevels` already owns the neutral ladder
`none | minimal | low | medium | high | xhigh | max | ultra`, which was `private`. It is now exposed
as `ProviderEffortLevels.KnownLevels` and `user-config.schema.json` restates it as an enum.

Restricting the key to `low`/`medium`/`high` was rejected: both shipped adapters accept `xhigh`, so
that set would reject configuration the providers can honour.

Because a schema enum and a code list can drift silently in both directions — a ladder-only level
becomes unconfigurable, a schema-only level is accepted at write time and then dropped without a
diagnostic by `ProviderEffortLevels.Resolve` — a contract test reads both actual sources and
compares them, rather than restating the list a third time.

Model ids are dictionary keys, and `ConfigurationSchemaCodec`'s `JsonSerializerOptions` sets
`PropertyNamingPolicy` (snake_case) but leaves `DictionaryKeyPolicy` unset, so ids survive verbatim.
A test asserts an id containing a capital letter round-trips unchanged, because a future change to
that option would otherwise silently stop every configured id from matching a real model.

## Consumption status

Each key is additive with no reader. `SprintOrchestrator.ConfigurationSnapshotAsync` — the one place
that sweeps *every* effective value of a scope into durable state — reads **project** scope only, and
all four keys are **user**-scoped, so none of them enters `SprintDefinition.ConfigurationSnapshot`
and no frozen sprint changes shape. The other user-scope readers select single keys by name
(`ConversationLanguageAsync` reads `language.llm`; `ScopedConfigurationProviderEnablementSource`
reads `providers.enabled`; `ForgeSettingsViewModel` picks its rows by name).

| Key | Status |
|---|---|
| `interaction.auto_approve_gate` | Schema only, **not consumed**. No enforcement path exists; see the mandatory-gate finding above before wiring one. |
| `shell.theme` | Schema only, not consumed by any rendering code. Blocked on S24's light palette. |
| `providers.priority` | Schema only, not consumed by routing. A later consumer intersects it with the effective enabled set. |
| `models.effort` | Schema only, not consumed by `ExecutionProfilePolicy.Freeze`. |

`ExecutionProfilePolicy` is a `static class` with no configuration dependency, and ADR 0014's
ADR-0063 revision deliberately deleted its catalog-taking overload so that no hidden resolution can
happen inside `Freeze`. Adding `models.effort` therefore could not silently change profile freezing,
and the honest wiring when a consumer is built is a new explicit `Freeze` parameter resolved by the
caller, mirroring how frozen models are already passed.

## Consequences

- All four keys resolve with provenance through the existing
  `ScopedConfigurationService.GetUserAsync`, so S18 needs no new read path.
- An older `config.json` missing all four still loads and resolves them to their defaults, matching
  ADR 0014's tolerant-read philosophy applied to configuration; the document is upgraded to
  `schema_version` `1.4.0` on its next save.
- Invalid values are rejected through the existing contract: `InvalidDataException` from the codec or
  schema, surfaced as `ConfigurationWriteResult(false, DiagnosticCodes.ConfigurationInvalid)`, with
  the previous file retained.
- No UI ships in this slice, and `Forge.Desktop` / `Forge.Desktop.Presentation` are untouched.
