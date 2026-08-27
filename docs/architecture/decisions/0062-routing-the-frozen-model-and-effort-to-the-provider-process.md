# ADR 0062: Routing the frozen model and effort to the provider process

- Status: Accepted (revised 2026-08-27)
- Date: 2026-08-27
- Contract version: unchanged (`execution-profile.schema.json` stays 1.0.0)

## Context

ADR 0006 gave every sprint three frozen `ExecutionProfile`s, and ADR 0014 made
`ExecutionProfilePolicy` resolve them once at creation time. Each profile carries a `model` and an
`effort` — both `required` in `execution-profile.schema.json` — and the policy freezes `medium` for
planning and implementation and `high` for review. The values are written to durable sprint state and
surfaced to the user.

`ILlmProvider.RunAsync` accepted neither. Neither `ClaudeLlmProvider` nor `CodexLlmProvider` put a
model or effort argument on the vendor command line, and the three node executors that call
`RunAsync` — planning, implementation, review — had the profile in hand at the call site and passed
only the prompt and the working directory. Every attempt in every release up to v0.84.0 therefore ran
at whatever the vendor CLI defaulted to. Review's `high` was not a description of Forge's behaviour;
it was a durable claim about behaviour that never happened.

This ADR is as much "what changed for the user and why it matters" as "what we decided". The fix is
plumbing, but its effect is a real change in how hard the model thinks, how long a sprint takes, and
what it costs.

## Decisions

### The flags are the ones the installed CLIs actually have, verified by running them

Same rule as ADR 0060 and ADR 0061: nothing here is mapped from documentation or from memory. Every
value below came from executing the installed vendor CLIs.

**Claude Code 2.1.233** (`claude --help`):

- `--effort <level>` — "Effort level for the current session (low, medium, high, xhigh, max)".
- `--model <model>` — "Provide an alias for the latest model (e.g. 'fable', 'opus', or 'sonnet') or a
  model's full name". A real `claude -p --model sonnet --effort low --output-format json` run
  succeeded and reported `modelUsage` keyed `claude-sonnet-5`, confirming both flags are honoured and
  that the alias this adapter freezes resolves.
- An unrecognized effort is not fatal and not honoured: `--effort bogus` prints `Warning: Unknown
  --effort value 'bogus' — ignoring it and using the default effort. Valid values: low, medium, high,
  xhigh, max.` and runs at the default.

**Codex CLI 0.149.1** (`codex exec --help`, `codex debug models`):

- Effort is a config override, not a flag: `-c model_reasoning_effort=<level>`. Verified end to end —
  `codex exec -c model_reasoning_effort=medium` prints `reasoning effort: medium` in its own run
  header.
- `codex exec` validates the value not at all: `-c model_reasoning_effort=bogus` reaches the run
  header verbatim and would be sent onward to the API.
- `-m, --model <MODEL>` exists, and `codex debug models` renders the catalog of slugs the release
  serves: `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`. Each
  entry lists its own `supported_reasoning_levels`; the levels common to all of them are `low`,
  `medium`, `high`, `xhigh`. The wire enum additionally contains `none`, `minimal`, `max`, and
  `ultra`, but no catalogued model offers `none` or `minimal`, and only some offer `max`/`ultra`.

### Forge decides the effort value, because neither vendor will

`execution-profile.schema.json` types `effort` as any non-empty string, so an adapter can be handed a
level its vendor does not offer. Both failure modes above are unacceptable: Codex would forward
nonsense to its API, and Claude would silently run at a level Forge did not choose while continuing
to display the frozen one — reproducing the exact defect this ADR fixes.

`ProviderEffortLevels.Resolve` therefore always sends a level the vendor lists, or sends nothing.
The ladder (neutral, an ordering of effort words) is `none < minimal < low < medium < high < xhigh <
max < ultra`; the accepted set is owned by each adapter, the same split ADR 0008 draws for every other
vendor fact.

| Frozen `effort` | Claude Code (`--effort`) | Codex (`model_reasoning_effort`) |
| --- | --- | --- |
| `medium` (planning, implementation) | `medium` | `medium` |
| `high` (review) | `high` | `high` |
| `none`, `minimal` | `low` | `low` |
| `low` | `low` | `low` |
| `xhigh` | `xhigh` | `xhigh` |
| `max`, `ultra` | `max` | `xhigh` |
| anything else | no flag | no override |

Rules, in order: an accepted level passes through; a level on the ladder but outside the accepted set
clamps to the nearest accepted neighbour, ties going to the cheaper one, since an approximation must
never spend more than the policy asked for; a level not on the ladder at all is vocabulary Forge does
not understand, so no flag is sent and the vendor default explicitly stands. `null` means the caller
genuinely has no frozen profile and means the same thing — leave the vendor default alone. An omitted
flag is omitted entirely, never sent empty.

Codex's accepted set stops at `xhigh` rather than `max` because only some catalogued models offer
`max`/`ultra`; `xhigh` is the highest level every one of them accepts. It stays the common
denominator even after ADR 0063 made the resolved model known, deliberately: widening the set per
model would make a sprint's effort depend on a value resolved after its profile was frozen.

### `model` and `effort` are required parameters, not defaulted ones

`ILlmProvider.RunAsync` gains `string? model` and `string? effort` with no default value, so every
call site — three node executors, two adapters, two test doubles — had to be visited. A defaulted
parameter would let the next call site silently reintroduce this exact defect: a profile frozen,
recorded, and shown, but never applied. There are few enough sites to review them all, matching the
precedent of ADR 0057, ADR 0058, and ADR 0060.

### Claude sends the frozen model; Codex deliberately does not

Claude Code accepts stable aliases, and `sonnet` — the value `ClaudeLlmProvider.DefaultModel`
declares and therefore the value neutral code freezes into every Claude profile — is one of them. It
is sent, and the recorded model is now true for Claude attempts.

Codex is sent no model flag at all. `CodexLlmProvider.DefaultModel` still returns `gpt-5`, a slug the
installed release does not serve: `codex exec -m gpt-5 …` returns

```
ERROR: {"type":"error","status":400,"error":{"type":"invalid_request_error",
"message":"The 'gpt-5' model is not supported when using Codex with a ChatGPT account."}}
```

Sending the frozen value would fail every Codex attempt outright — strictly worse than the defect
being fixed. The alternatives were both rejected: pinning today's top slug (`gpt-5.6-sol`) hardcodes
a name that rots at the vendor's next release and overrides the model the user configured, and
inventing a "current default" lookup would put network I/O behind a policy documented as pure and
deterministic. Not sending it leaves a Codex run on the model the user's own Codex configuration
resolves, which is what happens today and what happened before this ADR.

The consequence is named rather than hidden: **`ExecutionProfile.Model` remains inaccurate for Codex
sprints.** That is a pre-existing defect in what `CodexLlmProvider.DefaultModel` reports, not in how
this ADR consumes it. It was left to a separate follow-up slice, which needed its own design for
caching (the query is a real subprocess call, not something to run on every attempt), a refresh
cadence, and a documented fallback when the query itself fails -- concerns this "route what's already
frozen" fix does not have.

**Revised 2026-08-27 by ADR 0063**, which is now authoritative for everything in this section. Two
things it changed: the source proposed here (`codex debug models`, taking the lowest-numbered
`visibility: "list"` entry by `priority`) turned out to answer the wrong question -- it describes what
the release serves, loses to the user's own `~/.codex/config.toml`, and its `priority` values are not
unique -- so resolution reads `codex doctor --json` instead; and the `-m` omission above no longer
holds, since a model resolved that way is exactly as safe to send explicitly as Claude's `sonnet`
alias. Codex attempts from v0.85.0 carry `-m`, except for the two values ADR 0063 suppresses.

### Nothing about the frozen contract changes

`ExecutionProfilePolicy`, `execution-profile.schema.json`, and the shape of `ExecutionProfile` are
untouched. The values were always computed correctly; only their consumption was broken. No durable
format changes, so no contract version moves and no migration is needed. Sprints created before this
release resume with their existing frozen profiles and now have them applied, which is the point.

## Consequences

- **Behaviour and cost change from v0.84.1.** A phase whose frozen effort is above the vendor CLI's
  own default now thinks harder, runs longer, and spends more tokens. Review (`high`) is the largest
  change; planning and implementation (`medium`) change only where the vendor default is lower. This
  is not a silent internal correction and is stated as such in the changelog and release notes.
- The reverse also holds: where a vendor CLI defaulted *above* Forge's frozen level, attempts now run
  cheaper and faster than they did — and, in both directions, at the level the sprint actually
  records.
- Effort is now a real lever. When per-phase or per-project effort configuration is built, it reaches
  the provider process by construction; nothing further has to be wired.
- Claude attempts are pinned to the frozen model rather than inheriting whatever model the user's
  Claude Code session default happens to be. For a user whose default was not Sonnet, this is a model
  change, in exchange for runs matching what Forge records.
