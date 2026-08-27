# ADR 0063: Resolving and routing the Codex default model

- Status: Accepted
- Date: 2026-08-27
- Contract version: unchanged (`execution-profile.schema.json` stays 1.0.0)

## Context

ADR 0062 routed each sprint's frozen `ExecutionProfile.Model` and `Effort` to the provider process
and closed a defect where both were recorded but never applied. It could not finish the job for
Codex. `CodexLlmProvider.DefaultModel` returned the hardcoded string `gpt-5`, which the installed
Codex CLI rejects outright, so ADR 0062 sent Codex no model flag at all and named the inaccurate
recorded value as a known, deferred defect: **`ExecutionProfile.Model` is still a lie for every Codex
sprint.**

ADR 0062 also named the follow-up's design: resolve Codex's real default from `codex debug models`,
whose catalog carries a `priority` field, and take the lowest-numbered `visibility: "list"` entry as
the vendor's own top pick. **That assumption was wrong.** Everything below was verified by running
the installed Codex CLI 0.149.1, not read from documentation.

## Decisions

### The source is `codex doctor --json`, not `codex debug models`

`codex debug models` answers "what does this Codex release serve, and which does the vendor
recommend generically". It does not answer "what will a run started on this machine right now
actually use", which is the only question `ExecutionProfile.Model` is asking. Three findings, each
reproduced live, rule it out:

- **Configuration wins over the catalog.** A user's own `~/.codex/config.toml` may set
  `model = "..."`, and `codex exec` with no `-m` uses that, not the catalog's top pick. Confirmed by
  override: `codex exec -c model=gpt-5.4-mini` (no `-m` flag) ran exactly that model. A catalog-derived
  value would therefore have contradicted the run it claimed to describe — the same class of defect
  ADR 0062 exists to remove, reintroduced one layer down.
- **The catalog is not internally consistent.** Its two fetch modes (`--bundled` versus the default
  network refresh) disagree with each other.
- **`priority` is not a key.** Its values are not unique across entries, so "the lowest-numbered
  listed entry" does not identify a single row.

`codex doctor --json` reports the real, config-resolved model at
`checks["config.load"].details.model`. The same override test confirms it: with
`model = "gpt-5.4-mini"` configured, `doctor` reported `gpt-5.4-mini`. Two further properties make it
usable as a routine probe:

- It does **not** require authentication. `config.load` is independent of `auth.credentials`, so the
  probe is safe from a freshly installed, not-yet-logged-in state — the exact state a first provider
  check runs in.
- It costs about 1.7 seconds and runs two dozen checks including network reachability probes. That
  is far heavier than a `--version` or authentication probe, so it gets a 30-second deadline of its
  own rather than the 15-second one those use, and it is throttled (below).

`USERPROFILE` was already on `ProviderEnvironmentPolicy`'s allowlist, so `~/.codex/config.toml` was
already visible to Forge's Codex child processes. This introduces no new exposure. The probe is run
with the same minimal environment `RunAsync` builds, deliberately: an answer resolved under a
different environment than the attempt would describe a run that never happens.

### Caching and refresh cadence reuse the release check's, exactly

`FileProviderDefaultModelCache` is a per-provider, per-instance JSON file beside the release cache,
written atomically, degrading to "no cache" when missing or corrupt — the same file, the same
directory, the same failure behaviour as `FileProviderReleaseCache`. It is a small sibling type
rather than a generalization of `IProviderReleaseCache`: the payloads carry different values with
different meanings, and the release entry's shape is load-bearing in existing tests.

The throttle windows are the release check's own 24-hour success / one-hour failure pair (ADR 0008),
reused rather than reinvented. Both answer "what does the vendor say today", both are refreshed by
the same pass, and both are cheap to be a day stale — one cadence is one thing for a user to reason
about instead of two.

The probe rides `DiscoverAsync` and `InstallOrUpdateAsync`, forwarding their existing
`bypassReleaseCache` flag (so `forge models --refresh` refreshes this too). That is the
provider-capability pass which already runs before any sprint can be created, so no new call site had
to be invented and `ILlmProvider.DefaultModel` stays a synchronous property — no interface shape
change.

Every failure mode returns "unresolved", never an exception and never a partial value: no installed
executable (the cache is not even read or written, so an uninstalled provider cannot throttle the
first real probe after an install), a non-zero exit, a timeout, output that is not JSON, a missing or
wrongly-typed node at any level of `checks.config.load.details.model`, or a value that is not a
usable model id. A failure after a success does **not** clear the resolved value: the last
known-good model stands for the process lifetime, and the cache's shorter failure window governs
retry.

A resolved id is validated before it is ever used, on the way out of a fresh probe *and* on the way
out of the cache — the cache is an ordinary file, not a trusted channel. A model id must be a single
opaque token: non-empty after trimming, free of embedded whitespace, at most 64 characters, printable
ASCII. This is data hygiene on a value that becomes both a command-line argument and durable sprint
state, not injection defence — arguments are passed as a list with no shell anywhere.

### The resolved model is sent as `-m` in the same slice

This was the deliberate choice over the more cautious "resolve now, send later". The value being sent
is the one Codex would have resolved for itself anyway, so on day one it changes nothing about which
model runs — it changes what Forge can honestly claim. Deferring `-m` would leave
`ExecutionProfile.Model` a prediction about a run rather than a fact about it, which is precisely the
defect ADR 0062 exists to kill; recording a value and not applying it is how that defect is spelled.

Two values are suppressed rather than sent, and both degrade to exactly ADR 0062's command line:

- The unresolved sentinel `vendor-default`. It is a word rather than an empty string because
  `execution-profile.schema.json` requires `model` to have `minLength: 1`, and it is worded to read
  correctly wherever a frozen profile is displayed — a sprint frozen in that state really does run on
  whatever the user's own configuration resolves.
- `gpt-5`, the placeholder every release up to v0.84.1 froze into Codex sprints. Codex 0.149.1
  rejects it (`400 invalid_request_error`), so sending it would turn a value that was merely
  inaccurate into one that fails the run. A sprint frozen before v0.85.0 keeps running on the user's
  own configured model — which is what it has always actually done.

### `ExecutionProfilePolicy` reads each provider's model once per freeze

`DefaultModel` is now resolvable at runtime, so it can return different values on two consecutive
reads within one process. `ExecutionProfilePolicy.Freeze` records a model in four places — three
profiles plus the review lineage — and previously re-read the property for each. It now reads once
per distinct provider and reuses the value, including the single-provider case where review falls
back to the implementation provider. One freeze is one decision about one sprint; a lineage claiming
the implementation ran on a model the implementation profile does not name is unreadable evidence.

### Codex's accepted effort set stays model-independent

`SupportedEffortLevels` remains the `low`/`medium`/`high`/`xhigh` common denominator across every
model Codex catalogues, even though this ADR now knows which model a run will use. Widening it per
resolved model is a deliberate non-goal, not an oversight: it would make the effort a sprint runs at
depend on a value resolved after that sprint's profile was frozen. Per-model widening belongs with
real per-project model selection.

## Consequences

- **`ExecutionProfile.Model` becomes true for Codex sprints.** Sprint history and every surface that
  displays a frozen profile now show the model Codex actually resolves, instead of the non-functional
  `gpt-5` placeholder.
- **A stale `models.allowed_models` entry can now block sprint creation where it silently passed.**
  `ModelPolicyGate` has always evaluated whatever `DefaultModel` returns; it was previously always
  evaluating `gpt-5`. A project whose allowlist names `gpt-5`, or names a model Codex no longer
  serves, will now be refused at creation rather than proceeding on a model the policy never
  approved. This is the gate working correctly for the first time, but it is a real behaviour change
  for such a project and is stated as such in the changelog rather than buried.
- Each Codex provider check may spawn one additional short-lived vendor process, at most once per 24
  hours per instance (once per hour after a failure), and never when Codex is not installed.
- Sprints frozen before v0.85.0 keep the `gpt-5` value in their durable state and are unaffected at
  run time: that value is suppressed rather than sent, so nothing about how those attempts choose a
  model changes.
- The same mechanism is available to any future adapter whose vendor can be asked what it would do:
  the cache, the throttle, and the validation are all provider-neutral core.
