# ADR 0040: Versioned-contract fail-closed test coverage

- Status: Accepted
- Date: 2026-08-21

## Context

Stage 12's P12.16–P12.32 audit (ADR 0039) named "migration" as covered
per-artifact but not as a unified versioned-schema contract. Investigation
found the concern is narrower than it first sounds: Forge's 22
`docs/contracts/v1/schemas/*.schema.json` files each declare their own
`schema_version` property as a closed set (`const` for a single supported
version, `enum` for a documented backward-compatible range) — never an open
`{"type": "string"}` — so JSON Schema evaluation already rejects an
unsupported version by construction wherever a document is actually
validated against its schema.

The real gap was coverage, not design: `tests/Forge.Tests/Contracts/fixtures/contract-cases.json`
drives `ContractTests.Draft202012SchemasMatchCompatibilityFixtures` (which
evaluates every named case against its schema and asserts the expected
`valid` outcome) for 18 of the 22 schemas, but only one case in the whole
file — `user-config`'s own "rejects an unknown major schema version" — ever
supplied an out-of-range `schema_version`. The other 21 schemas' fail-closed
behavior on a bad version had never actually been exercised, and 4 schemas
(`context-manifest`, `context-query-plan`, `context-result-bundle`,
`test-work-result`) had no fixture cases at all.

Two further items from ADR 0039's own audit turned out to be false
positives on direct inspection, not additional migration gaps:
"runtime provider-selection fallback among enabled candidates" — ADR 0008
explicitly forbids a runtime fallback ("an empty intersection blocks
execution... rather than silently selecting another provider"); the only
real selection (`ExecutionProfilePolicy.SelectReviewProvider`) happens once,
at sprint-freeze time, and is already fully tested both ways in
`ExecutionProfilePolicyTests`. "Same-lineage review fallback under
mid-sprint exhaustion" — review's provider/model is frozen once; a
mid-sprint circuit-open or budget-exhausted routing outcome takes the same
generic, already-tested `RoutingLedgerTests` path every other phase takes,
with no phase-specific fallback code to test.

## Decisions

### Add fixture-driven fail-closed coverage for every schema, not new validation code

No production code changes: the fail-closed behavior already exists at one
chokepoint (`SchemaValidation.Validate`, used identically by every codec
that deserializes one of these contracts) and needs no new mechanism, only
proof it actually holds for every schema.

`contract-cases.json` gains one "rejects an unknown major schema version"
case per schema (`schema_version: "2.0.0"`, outside every schema's own
allowed set), reusing each schema's own existing valid instance with only
the version field changed, plus a first valid instance and version-rejection
case for the 4 previously-uncovered schemas. `Draft202012SchemasMatchCompatibilityFixtures`
needs no change — it already evaluates every case in the file generically.

### Guard against a future schema silently missing this coverage

A new `ContractTests.EveryContractSchemaRejectsAnUnsupportedSchemaVersion`
walks `docs/contracts/v1/schemas/*.schema.json` directly (not a hardcoded
name list), reads each schema's own `schema_version` `const`/`enum`
definition, and asserts at least one fixture case for that schema is both
`valid: false` and carries a `schema_version` outside that set. A schema
added later with no such case fails this test by construction, rather than
silently extending the same unverified gap this slice closes. Confirmed via
a live mutation check: removing one schema's new rejection case fails this
test naming exactly that schema, before the case was restored.

## Consequences

- All 22 contract schemas now have at least one fixture case proving they
  reject an out-of-range `schema_version`; the 4 previously-uncovered
  schemas (`context-manifest`, `context-query-plan`, `context-result-bundle`,
  `test-work-result`) also gained a first valid-instance case.
- `EveryContractSchemaRejectsAnUnsupportedSchemaVersion` keeps this property
  from silently regressing as new contracts are added.
- No production code changed; no behavior change for any conforming
  document — this closes a verification gap, not a live defect.
- The P12.16–P12.32 "runtime provider-selection fallback" and "same-lineage
  review fallback under mid-sprint exhaustion" items are corrected from
  open gaps to false positives: both name behavior that either does not
  exist by design (ADR 0008) or is already covered generically
  (`ExecutionProfilePolicyTests`, `RoutingLedgerTests`).
- Deliberately out of scope, named rather than silently dropped: the
  remaining lower-confidence P12.16–P12.32 items (permissions/licenses —
  no production policy exists yet; accessibility — known MAUI-headless
  limitation).

## References

- ADR 0039 (the audit that first named this item and the two now-corrected
  false positives)
