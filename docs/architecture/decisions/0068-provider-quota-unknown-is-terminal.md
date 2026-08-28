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

### Tests: one wording regression, two structural checks

- `LocalizationCatalogTests.TheUnknownQuotaStateIsWordedAsTerminalRatherThanPendingInBothLanguages`
  pins the corrected wording in both languages and, independently of the exact phrasing, asserts that
  neither value carries a pending marker. Mutation-verified: restoring either pre-fix resx value fails
  the pending-marker assertions on "not yet"/"пока". The Russian marker is matched word-bounded, so a
  future terminal rewording using the unrelated "пока-" stem ("показатель") does not trip it.
- Terminality is a structural claim, so it is checked structurally. No behavioral test can carry it:
  the projector never asks an adapter about quota (its only adapter read is `DefaultModel`), so
  asserting `Unknown` over the real catalog would stay green even after an adapter grew a real quota
  API — the defect PR #125's review found in the first revision of this section.
  - `ProviderCompositionTests.NeitherRealProviderAdapterExposesAQuotaSignalTheProjectorCouldRead`
    reflects over `ILlmProvider` and over both provider adapters, including inherited and non-public
    members, and fails if any is quota-shaped by name or by quota contract type. The adapter list is
    not duplicated on trust: the same test scans `src/` for every `.Add*Provider(` invocation and
    fails if that set differs from the two it registers, so a third adapter composed into any
    shipping root cannot skip inspection here. Mutation-verified against a `QuotaHint` property added
    to `CodexLlmProvider`.
  - `ProviderCompositionTests.TheProjectorsUnverifiedFactoryIsTheOnlyProductionProducerOfAQuotaSnapshot`
    scans production source and fails if a `ProviderQuotaSnapshot` is constructed anywhere but
    `ProviderQuotaProjector.Unverified`, or if that factory stops hardcoding `Unknown`.
    Mutation-verified against a second producer added to `ProviderCatalog` and against the factory
    returning `Stale`. This is a text scan, so its reach is bounded and stated rather than assumed: it
    recognizes an explicit `new ProviderQuotaSnapshot(...)`, and a target-typed `new(...)` or a `with`
    expression bound to a declaration naming the type — expression-bodied, assigned, or returned from
    a brace body. A construction site that names the type nowhere near itself (a `var` local, a
    declaration assigned in a later statement, a `return` behind a nested block) is out of reach. The
    companion
    `TheQuotaSnapshotConstructionScanRecognizesEveryFormItClaimsTo` pins both lists, so this paragraph
    cannot drift from what the scan does. It is the tripwire for a second producer appearing, not a
    compiler-grade proof that none can be written.
  - Together: nothing to read, and nothing else that produces a reading — so no production code path
    reaches another availability, amount, unit, or reset time. Either check failing is the signal
    that this ADR's terminal wording must be revisited. The first check is exhaustive by reflection;
    the second is exhaustive only over the construction forms listed above, which is why the two are
    stated with different strength.
- `ProviderCompositionTests.QuotaProjectsAsUnknownForBothRealProviderAdapters` remains as the
  composed-root demonstration (`ProviderQuotaProjectorTests` covers the projection over
  `FakeLlmProvider` only), and its own doc comment states that it pins today's behavior rather than
  proving terminality.

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
  and `QuotaStatusUnknownAccessible`; `MessageKeys.QuotaStatusUnknown`,
  `SurfaceFormatting.QuotaStatusSummary` and `SurfaceFormatting.ProviderQuotaRow` document the
  terminal-not-pending requirement (the row formatter's remarks previously still said "currently").
- `tests/Forge.Tests`: the wording regression, the two structural checks above, and the scan's own
  form-coverage check.
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
