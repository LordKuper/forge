# ADR 0066: Provider model enumeration and per-sprint model selection

- Status: Accepted
- Date: 2026-08-28
- Contract version: unchanged (`execution-profile.schema.json` stays 1.0.0)

## Context

ADR 0014 froze three execution profiles per sprint and sourced each one's model from
`ILlmProvider.DefaultModel`; ADR 0063 made that property resolve Codex's real, config-resolved model
at runtime and routed it to the run. Neither gave anyone a *choice*: `ExecutionProfilePolicy` still
resolves exactly one model per provider and its own comment says "Revisit once real per-project,
per-phase model selection exists." The desktop parity plan's finding C2 is that gap, and its
decisions Q10 and Q11 are binding here:

- **Q11** — the list of selectable models comes from a new `ILlmProvider.ListModelsAsync` per adapter,
  not a hand-maintained config list and not free text. It is the only option that does not rot at the
  next vendor release.
- **Q10** — the picker appears at *sprint creation only*; the header shows the frozen model read-only.
  A mutable mid-sprint model was rejected outright: it breaks ADR 0014's frozen-profile invariant and
  the reproducibility guarantee that rests on it.

This slice is the backend half of both. No UI ships here.

## Decisions

### `ILlmProvider.ListModelsAsync`, and why the two adapters answer it differently

`ILlmProvider` gains one member, `ListModelsAsync(cancellationToken)`, returning the model ids a
caller may select in the vendor's own presentation order. It is vendor-owned exactly like `Id` and
`DefaultModel`; neutral code still names no model anywhere.

The two implementations are deliberately asymmetric, mirroring the asymmetry ADR 0063 already
established for `DefaultModel` — Codex resolves live, Claude is a constant — and for the same reason:
what the vendor can actually be asked differs.

**Codex reads `codex debug models` live.** This is the one place that command is the right source,
and the apparent contradiction with ADR 0063 is not one. ADR 0063 rejected the catalog for
`DefaultModel` because the question there is "what will a run started here right now actually use",
which the user's own `~/.codex/config.toml` answers and a generic catalog does not. The question here
is the different one the catalog is exactly right for: "what may a user choose". Its other two
objections do not apply either — the two fetch modes disagreeing, and `priority` not being unique —
because both bite only an attempt to identify one single row, and this reads the listed rows as a
set. Entries Codex marks `"visibility": "list"` are offered; `hide` entries (internal, retired, or
preview models a user has no business selecting) are not. Nothing is re-sorted: the vendor already
emits the catalog in the order it wants a picker to show, and `priority` is not a key. Verified
against Codex CLI 0.149.1, whose live catalog lists six of its eight entries; a trimmed verbatim
capture is `tests/Forge.Tests/Unit/fixtures/providers/codex-debug-models.json`.

**Claude ships its documented alias set.** Claude Code publishes no catalog command of any kind, so
this adapter returns the exact three aliases the vendor's own `claude --help` names for `--model`
("Provide an alias for the latest model (e.g. 'fable', 'opus', or 'sonnet')"), in that order,
verified against Claude Code 2.1.250 rather than read from documentation. It is `ponytail:`-marked, on
exactly the terms ADR 0014 used for `DefaultModel`: a fixed value that stays honest about being one,
to revisit if the vendor ever ships an enumeration. `--model` also accepts a full dated model name,
which the list deliberately does not try to predict — an alias always resolves to the current model,
and a hardcoded slug would rot at the next release. `haiku` is not included: the vendor's own help
does not name it, and inventing a fourth entry would be the guess this repository refuses elsewhere.

**Empty means "could not be asked", never "offers nothing."** Every Codex failure mode — no installed
executable, a non-zero exit, a timeout, output that is not JSON, a missing or wrongly-typed node, a
catalog whose slugs all fail model-name validation — returns an empty list, never an exception. That
distinction is load-bearing in the validation below. Claude's list can never be empty, because there
is nothing to fail.

### Caching reuses ADR 0063's, exactly

`FileProviderModelCatalogCache` is a per-provider, per-instance JSON file beside the release and
default-model caches, written atomically, degrading to "no cache" when missing or corrupt.
`ProviderInstallation.ResolveModelCatalogAsync` is `ResolveDefaultModelAsync`'s shape verbatim: the
same no-install short circuit (so an uninstalled provider cannot write a failure entry that throttles
the first real probe after an install), the same 24-hour success / one-hour failure windows, the same
"validate on the way out of the cache as well as on the way in" rule, and the same "only the parse
delegate ever sees raw process output". A third small sibling type rather than a generalization of
either existing cache, for the reason ADR 0063 already gave: the payloads carry different values with
different meanings, and the existing entries' shapes are load-bearing in existing tests.

The cache is not speculative even though enumeration is human-initiated. The Forge Host is
long-lived and serves every Desktop sprint creation, so a picker opened repeatedly would otherwise
spawn a vendor process and parse a ~320 KB catalog every time, on a path a user is waiting on. The
30-second deadline is the default-model probe's, reused rather than reinvented — one deadline is one
thing to reason about, and the measured cost is well under a second.

### Selection is frozen at creation, and overrides one provider's entry rather than three profiles

`CreateSprintCommand` gains an optional `RequestedModel`. `SprintOrchestrator.CreateSprintAsync`
applies it through `ExecutionProfilePolicy.ApplyRequestedModelAsync` to the map
`ResolveModelsAsync` just produced, **before** the `ModelPolicyGate` check, replacing only the
primary provider's (`frozenProviders[0]`) entry.

One entry, not three profiles, because a model id is provider-specific: ADR 0014 already freezes one
model per *distinct* provider, so overriding the primary provider's entry is what reaches planning,
implementation, and — in the single-provider case, where review falls back to that same provider —
review and its lineage too. A review phase that genuinely ran on a different provider keeps that
provider's own resolved default, because `sonnet` means nothing to Codex.

Placing the override before the gate is what makes this safe with no new policy surface: the
requested model is validated by exactly the allowlist the default would have been, and frozen by
exactly the same `Freeze` call, so there is no second code path to keep in agreement and no way for a
picker to become a route around `models.allowed_models`. ADR 0063's single-resolution invariant is
untouched — this replaces a value in the one already-resolved map rather than reading anything again.

Two checks run before the gate. `NormalizeModelName` is unconditional: a requested id becomes both a
vendor command-line argument and durable sprint state, and it arrives from a caller rather than from
a vendor, so it earns at least the hygiene a probed value gets. The enumeration check is conditional
on there *being* an enumeration — an empty `ListModelsAsync` means the vendor could not be asked, and
refusing every explicit choice whenever a vendor probe is unavailable would trade a rare bad run for
a common blocked one. The check that actually protects policy is unconditional and runs regardless.

A `null` or blank request is byte-identical to the behaviour before this ADR: nothing is enumerated
(proven by a test that counts `ListModelsAsync` calls on the default path), nothing is replaced, and
the gate and freeze read the untouched resolved map. Blank counts as omitted rather than as a
refusal, matching the rule the same method already applies to a blank title, and letting a picker's
own "auto" entry send an empty value instead of needing a separate flag.

**No mid-sprint change exists, by construction.** Q10 rejected one, and this slice adds no way to
express one: the only place a model can be chosen is the command that freezes the sprint. ADR 0014's
"frozen exactly once, never re-resolved even if configuration changes while the sprint runs" is
unweakened.

### The refusal reuses `model_policy_violation`

A request that is not a usable model id, or that the provider does not offer, is refused with
`DiagnosticCodes.ModelPolicyViolation` and no sprint is registered — the same fail-closed placement
as the empty-candidates and allowlist checks. The code names the policy rather than the real cause,
which is imprecise, and it is the same tradeoff ADR 0063 documented for its own imprecise case: a
distinct code is a public contract addition spanning `DiagnosticCodes`, `ExitCodes`, every localized
surface, and the README's diagnostic table, and it changes no outcome. Deferred on those terms rather
than silently.

## What stays deferred

- **The picker itself, and the transport it needs.** Slice S15. Desktop reads and mutates through the
  Host control protocol, which carries no model field and no enumeration query; adding either now
  would be uncalled infrastructure of exactly the kind ADR 0014 removed from its own scope. S15 owns
  the wire shape because S15 is what knows what the picker needs.
- **Per-phase model selection.** All three phases still take one model per provider. ADR 0014's
  `ponytail:` note stays accurate: this slice replaces *which* model that is, not the one-per-provider
  rule.
- **Per-model effort.** `SupportedEffortLevels` stays the model-independent common denominator ADR
  0063 fixed it at. Widening it per selected model is slice S4's concern, not this one's.

## Consequences

| Action | Recovery |
|---|---|
| create a sprint with no model request | unchanged in every respect; nothing is enumerated |
| create a sprint requesting a model the provider offers and the project allows | that model is frozen into all three profiles and the review lineage |
| create a sprint requesting a model the provider does not offer | `model_policy_violation`; no sprint registered |
| create a sprint requesting a model the project allowlist excludes | `model_policy_violation`; no sprint registered |
| create a sprint requesting a model while the vendor probe fails | the enumeration check is skipped; `ModelPolicyGate` and model-name validation still decide |
| open a picker repeatedly | at most one vendor process per 24 hours per machine, and none when the provider is not installed |
| a hand-edited or truncated catalog cache | rejected as corrupt, re-probed once, and overwritten on the next enumeration |
