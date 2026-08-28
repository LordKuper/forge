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

`ILlmProvider` gains two members. `ListModelsAsync(bypassCache, cancellationToken)` returns the model
ids a caller may select in the vendor's own presentation order; `IsReservedModelName(model)` answers
whether a value is a placeholder the adapter reserves for itself (see "Reserved sentinels are refused
at creation" below). Both are vendor-owned exactly like `Id` and `DefaultModel`; neutral code still
names no model anywhere.

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

### Caching reuses ADR 0063's, exactly — including its refresh path

`FileProviderModelCatalogCache` is a per-provider, per-instance JSON file beside the release and
default-model caches, written atomically, degrading to "no cache" when missing or corrupt.
`ProviderInstallation.ResolveModelCatalogAsync` is `ResolveDefaultModelAsync`'s shape verbatim: the
same no-install short circuit (so an uninstalled provider cannot write a failure entry that throttles
the first real probe after an install), the same 24-hour success / one-hour failure windows, the same
"validate on the way out of the cache as well as on the way in" rule, and the same "only the parse
delegate ever sees raw process output".

**`forge models --refresh` clears this cache too.** The first draft of this slice hardcoded
`bypassCache: false` on the enumeration path, reasoning that nothing there carries a `--refresh`
intent. Round 1 review of PR #123 showed that argument inverted: it made the catalog the one provider
cache nothing in the product could reach, so a model Codex had just released — the exact event Q11
chose live enumeration to survive — stayed invisible to the picker *and produced a refusal for an
explicit request* for up to 24 hours, recoverable only by deleting
`Forge/{InstanceId}/providers/model-catalog-codex.json` by hand. Strictly worse than the ADR 0063 case
it claimed to mirror, which relies on `--refresh` as its escape hatch. `ListModelsAsync` therefore
takes the same `bypassCache` flag the release check already carries, and `DiscoverAsync`/
`InstallOrUpdateAsync` forward it alongside `RefreshDefaultModelAsync`. Sprint creation still passes
`false` and honours the throttle. Riding the capability pass also warms a cold cache, so the first
picker opened after one answers without spawning anything.

The cache is not speculative even though enumeration is human-initiated. The Forge Host is
long-lived and serves every Desktop sprint creation, so a picker opened repeatedly would otherwise
spawn a vendor process and parse a ~320 KB catalog every time, on a path a user is waiting on. The
30-second deadline is the default-model probe's, reused rather than reinvented — one deadline is one
thing to reason about, and the measured cost is well under a second.

**One implementation of the file mechanics, three payloads.** The first draft cloned
`FileProviderDefaultModelCache` character for character, on ADR 0063's "the payloads carry different
values with different meanings". Round 1 review of PR #123 was right that the reason does not survive
the obvious refactor: the payloads differ, but the *file mechanics* — the per-instance directory,
snake_case JSON, atomic write, corrupt-file degradation, best-effort write — are identical, and
stating that contract in three places means a fix to any of them has to be made three times or
silently diverge. `FileProviderJsonCache<TEntry>` now holds it once; `FileProviderReleaseCache`,
`FileProviderDefaultModelCache`, and `FileProviderModelCatalogCache` each contribute only a payload
record and a file-name prefix, so all three keep distinct serialized shapes, distinct file names, and
distinct meanings, and no on-disk entry shape changed at all.

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

Three checks run before the gate, and only the third is conditional.

`NormalizeModelName` is unconditional: a requested id becomes both a vendor command-line argument and
durable sprint state, and it arrives from a caller rather than from a vendor, so it earns at least the
hygiene a probed value gets.

`IsReservedModelName` is unconditional (see below).

The enumeration check is conditional on there *being* an enumeration — an empty `ListModelsAsync`
means the vendor could not be asked, and refusing every explicit choice whenever a vendor probe is
unavailable would trade a rare bad run for a common blocked one. When there *is* one, the model
already resolved for that provider is accepted alongside it even if the catalog omits it. Round 1
review of PR #123 found that omission is reachable and its consequence perverse: `DefaultModel` comes
from `codex doctor --json` (whatever the user's own `config.toml` resolves) while the catalog is
`codex debug models` filtered to `"visibility": "list"`, so a `model = "..."` naming a `hide` entry, or
a custom `model_providers`/OSS slug that is in no served catalog at all, yields a non-empty catalog
without the current default in it. Omitting the request froze that model happily; asking for the same
value was refused — the "safe" explicit choice punished and the implicit one not, and a picker's
pre-selected entry unreachable. The default is trivially a valid choice, because it is precisely what
a sprint that requests nothing freezes and runs.

The operand of that comparison is the already-resolved map entry, never a second reading of
`DefaultModel`. Round 2 review of PR #123 found the first fix re-read the property, which reopens the
same asymmetry through freshness rather than through the rule: the property is resolvable at runtime,
so a refresh landing while `ListModelsAsync` is awaited moves it, and the request is then compared
against a value this sprint never uses — refusing precisely the model an omitted request would have
frozen. One resolution feeds the enumeration check, the allowlist gate, and the freeze alike, which is
what `ResolveModelsAsync` exists to guarantee.

The check that actually protects policy is unconditional and runs regardless.

### Reserved sentinels are refused at creation

`CodexLlmProvider.RunAsync` deliberately does not send two literals as `-m`: `vendor-default` (no probe
has succeeded) and `gpt-5` (frozen by releases up to v0.84.1 and rejected outright by Codex 0.149.1).
Both leave the attempt on whatever the user's own configuration resolves. That suppression is correct
and stays — it exists for profiles that were *already* frozen, and making those sprints fail instead
would help nobody.

Round 1 review of PR #123 found that neither literal was refused as a *fresh* explicit request, which
is a different thing entirely. Both pass `NormalizeModelName`, and on the fail-open branch (vendor not
enumerable) with no allowlist configured, creation reported success, the three profiles and the review
lineage recorded the sentinel, and every attempt then ran on a different model with nothing reporting
the divergence — manufacturing exactly the "no probe has succeeded" state `vendor-default` exists to
signal, on a sprint whose probe may have worked fine, and turning ADR 0014's frozen profile into
misleading evidence.

`ILlmProvider.IsReservedModelName` closes it, and is a separate member rather than a filter inside
`ListModelsAsync` for a specific reason: a caller falls through the enumeration check whenever the
vendor could not be asked, which is the very branch that reached this state. The Codex adapter answers
from the same constant set `RunAsync` suppresses, read from one place so the two can never drift;
Claude reserves nothing.

A `null` or blank request is byte-identical to the behaviour before this ADR: nothing is enumerated
(proven by a test that counts `ListModelsAsync` calls on the default path), nothing is replaced, and
the gate and freeze read the untouched resolved map. Blank counts as omitted rather than as a
refusal, matching the rule the same method already applies to a blank title, and letting a picker's
own "auto" entry send an empty value instead of needing a separate flag.

**No mid-sprint change exists, by construction.** Q10 rejected one, and this slice adds no way to
express one: the only place a model can be chosen is the command that freezes the sprint. ADR 0014's
"frozen exactly once, never re-resolved even if configuration changes while the sprint runs" is
unweakened.

### Each refusal reports its own cause

The first draft reused `DiagnosticCodes.ModelPolicyViolation` for every refusal on this path, on ADR
0063's precedent. Round 1 review of PR #123 showed the precedent does not transfer. ADR 0063's
imprecise case fires only for "a project whose `models.allowed_models` lists `codex:<model>`" — an
allowlist exists, so the code is at least literally true — and it offers `forge models --refresh` as a
concrete escape hatch. This path has neither property: it fires with **no allowlist configured at
all**, which is the default, on caller-supplied input, with nothing stale to clear. Naming a policy
the project never set is not imprecise but misleading, sending an operator to a `models.allowed_models`
key that is not there.

Three materially different causes now report separately, and no sprint is registered for any of them
— the same fail-closed placement as the empty-candidates and allowlist checks:

| Cause | Code | Exit |
|---|---|---|
| not a usable model id (blank, whitespace-bearing, non-printable, too long) | `sprint_model_invalid` | 2, usage |
| the provider does not offer it, or reserves it as a placeholder | `sprint_model_not_offered` | 7, provider |
| a configured project allowlist excludes it | `model_policy_violation` | 11, workflow |

A fourth state, "the vendor could not be enumerated", is distinguished by producing no refusal here at
all: the request falls through to the allowlist and model-name checks. `ExecutionProfilePolicy`
returns the reason rather than a bare `null`, so the orchestrator reports it without re-deriving it,
and `ExitCodes.For` and `docs/contracts/v1/README.md` carry both new codes.

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
| create a sprint requesting the provider's current default that its catalog omits | accepted and frozen — the default is always a valid choice |
| create a sprint requesting a model the provider does not offer, or a placeholder it reserves | `sprint_model_not_offered` (exit 7); no sprint registered |
| create a sprint requesting a value that is not a usable model id | `sprint_model_invalid` (exit 2); no sprint registered |
| create a sprint requesting a model the project allowlist excludes | `model_policy_violation` (exit 11); no sprint registered |
| create a sprint requesting a model while the vendor probe fails | the enumeration check is skipped; the reserved-name check, `ModelPolicyGate`, and model-name validation still decide |
| create a sprint on a multi-provider project | the request replaces the primary provider's model only; a review phase on a different provider keeps its own resolved default |
| open a picker repeatedly | at most one vendor process per 24 hours per machine, and none when the provider is not installed |
| a vendor releases a new model | `forge models --refresh` makes it selectable immediately; otherwise it appears within the 24-hour window |
| a hand-edited or truncated catalog cache | rejected as corrupt, re-probed once, and overwritten on the next enumeration |
