# ADR 0068: `ProviderQuotaAvailability.Unknown` is a terminal state, not a pending one

- Status: Accepted
- Date: 2026-08-28
- Contract version: unchanged (`ProviderQuotaStatus` 1.0.0, capabilities.json 1.16.0)

## Context

`docs/plans/desktop-design-parity-execution.md` finding B7 / slice S5, decision Q5(a): the sidebar's
provider chips are restyled so their popover honestly states that no limit data is available. Q5
explicitly rejected building a quota signal by inference (plan section 6.5 forbids it), and ADR 0052
already established that neither shipped provider integration exposes one. ADR 0061 re-confirmed that
conclusion while capturing token usage from the same provider streams.

S5 is therefore not about producing quota data. It is about making "no limit data" a state S10 can
render as a final answer, rather than something a UI author would reasonably mistake for missing
wiring.

## Decisions

### The existing shape is sufficient: no new state and no new reason code

`ProviderQuotaAvailability.Unknown` plus `ProviderDiagnosticCodes.QuotaUnknown` already mean exactly
"no quota limit data exists for this provider". There is no second flavour of absence to distinguish
from it in this codebase:

- `ProviderQuotaProjector` has exactly one production factory (`Unverified`), reached by both
  `Project` overloads, and it hardcodes `Unknown` with a null amount, unit and reset time.
- No other production code path constructs a `ProviderQuotaSnapshot` at all.
- The projection is pure and synchronous over an already-computed toolchain/health set — there is no
  asynchronous fetch that could be "in flight", so a separate "not measured yet" member would have no
  producer.

Adding a `NotSupported`/`NotReported` member to sit beside `Unknown`, or a second diagnostic code,
would introduce a distinction no code makes and no surface could act on. ADR 0052 already paid the
"structurally complete for every state the plan requires" cost once, for the four unreachable verified
states; repeating it for an unreachable *unverified* state would be speculative structure, which
AGENTS.md's quality rules reject. Rejected.

### `Unknown` is documented, in code, as terminal and expected

ADR 0052 stated the finding in prose; the contract itself hedged. `ProviderQuotaAvailability.Unknown`'s
own remarks said only "the only state this codebase **currently** produces", which reads as a
placeholder awaiting wiring. That member and `ProviderQuotaProjector`'s consumer contract now state
directly that the state is terminal for both shipped providers, that rendering "no limit data
available" for it is correct and final, and that a consumer must never draw a spinner, a placeholder
awaiting a value, a retry affordance, or wording that promises a later reading. `MessageKeys.QuotaStatusUnknown`
and `SurfaceFormatting.QuotaStatusSummary` carry the same statement at their own layer.

This is the substance of S5: S10's author reads the contract, not the ADR index.

### The one real defect was the user-facing wording, and it is fixed

The audit for a code path treating `Unknown` as transient found no spinner, poll, retry, or
"loading" branch anywhere — the sidebar recomputes the row on every render and the CLI exits `ok`.
It found the defect one layer down, on the only surface a user actually sees:

| Key | Before | After |
| --- | --- | --- |
| `QuotaStatusUnknown` (en) | Quota status not yet available. | Provider quota limits are not reported. |
| `QuotaStatusUnknownAccessible` (en) | Quota status: not yet available. | Quota status: limits are not reported. |
| `QuotaStatusUnknown` (ru) | Статус квоты пока недоступен. | Лимиты квоты провайдера не сообщаются. |
| `QuotaStatusUnknownAccessible` (ru) | Статус квоты: пока недоступен. | Статус квоты: лимиты не сообщаются. |

"Not yet" / "пока" told every user, sighted and screen-reader alike, to wait for a value ADR 0052
established will never arrive. It is the exact transient-vs-terminal mistreatment S10 would otherwise
have had to render around, and the strings were the only place it existed.

The four corrected values still read as a statement about the reading itself, so they stay true under
`ProviderQuotaAggregation.Worst`'s worst-case-across-providers semantics. The other four availability
states' strings are untouched.

### Tests: one regression, one real-adapter confirmation

- `LocalizationCatalogTests.TheUnknownQuotaStateIsWordedAsTerminalRatherThanPendingInBothLanguages`
  pins the corrected wording in both languages and, independently of the exact phrasing, asserts that
  neither value carries a pending marker. Mutation-verified: restoring either pre-fix resx value fails
  the `DoesNotContain` assertions on "not yet"/"пока".
- `ProviderCompositionTests.QuotaProjectsAsTerminallyUnknownForBothRealProviderAdapters` verifies the
  terminality claim rather than asserting it: it composes the real
  `Forge.Providers.Codex.Windows`/`Forge.Providers.Claude.Windows` adapters exactly as a shipping
  composition root does and proves every projected snapshot is `Unknown`/`provider_quota_unknown` with
  no amount, unit, or reset time. `ProviderQuotaProjectorTests` only ever proved that over
  `FakeLlmProvider`.

No test asserts the absence of a spinner, because no rendering code exists to assert against — that is
S10's own scope.

## What stays deferred

- A real quota adapter for any provider, unchanged from ADR 0052/0061: it needs a vendor to publish a
  structured, scriptable quota API. If one lands it extends `ProviderQuotaProjector`, and this
  contract's shape does not change.
- All rendering. The two compact chips and their popover are S10; this slice ships no UI and touches
  no `Forge.Desktop`/`Forge.Desktop.Presentation` code.
- Promoting `provider.quota_status` to `CapabilityIds.Implemented` (ADR 0049/0050/0051/0052's own
  separable cleanup).

## Consequences

- `Forge.Runtime` (`Providers/ProviderQuota.cs`): `ProviderQuotaAvailability.Unknown` and
  `ProviderQuotaProjector` gain the terminal-state consumer contract. No member, signature, or
  behavior changed.
- `Forge.Runtime` (`Localization/`): `Messages.resx`/`Messages.ru.resx` reword `QuotaStatusUnknown`
  and `QuotaStatusUnknownAccessible`; `MessageKeys.QuotaStatusUnknown` and
  `SurfaceFormatting.QuotaStatusSummary` document the terminal-not-pending requirement.
- `tests/Forge.Tests`: the two tests above.
- No contract, schema, capability, CLI, or Desktop change. Every existing test asserts through message
  *keys*, not literals, so none needed updating.
- `VERSION` takes a PATCH bump from this branch's base, `main` @ `0.87.0`, to `0.87.1` (a wording fix
  to shipped user-facing text plus documentation; nothing additive and nothing breaking). Renumber to
  the next PATCH above whatever `main` carries if a concurrent slice merges first.

## References

- ADR 0052 (the investigation: neither vendor CLI exposes a verified quota signal; unknown is rendered
  as unknown, never inferred)
- ADR 0061 (re-confirmed that deferral while capturing usage from the same provider streams)
- `docs/plans/desktop-design-parity-execution.md` — finding B7, Part 3 slice S5, Part 4 decision Q5
