# ADR 0029: Context token-budget configuration key

- Status: Accepted
- Date: 2026-08-19
- Contract version: 1.0.0

## Context

ADR 0028 shipped `IntakeExecutionHostedService` with a fixed
`DefaultTokenBudget = 32_000` literal passed to
`ContextManifestCompiler.Compile`, documented explicitly as debt: "a fixed
literal, because no token-budget configuration key exists in
`docs/contracts/v1/configuration.json` to read one from -- make
configurable once one does." This ADR is that follow-up, picked as the
smallest well-scoped item remaining on Stage 11's own deferred list after
PR #68 closed: unlike the model-bearing executors, `SprintGitIsolation`'s
lifecycle, or the navigation shell (each requiring its own design), this
is a single configuration key with an already-obvious shape and an
already-generic read/write path (`forge config user|project show|set`,
built once in ADR 0019 and never touched since).

## Decisions

### Project scope, not user scope

`ConfigurationRegistry` has exactly two scopes. `notifications.enabled`
(ADR 0024) is `User`-scoped because it is a per-machine attention
preference, independent of which project is open. A token budget is the
opposite: how much of a project's own `.forge/rules`/`.forge/knowledge`
content fits a given budget is a property of *that project's document
set*, not a preference that should follow the operator across every
project on the machine. It is scoped `Project`, matching
`artifacts.language.*`'s own precedent rather than
`notifications.enabled`'s.

### The first integer-typed configuration key

Every key `ConfigurationRegistry` has registered before this one is a
string, boolean, or string array. Adding an integer key touches three
places that had never needed to handle one:
`ConfigurationSchemaCodec.GetOptionalInt32` (mirroring
`GetOptionalBoolean`'s existing shape exactly), a new `Add(..., int?)`
overload, and `project-manifest.schema.json`'s new optional
`context.token_budget` property (`"type": "integer", "minimum": 1`).
`docs/contracts/v1/configuration.json`'s own `contract_version` bumps
1.2.0 -> 1.3.0; `project-manifest.schema.json`'s `schema_version` enum
gains `"1.1.0"` alongside the existing `"1.0.0"`, the same
tolerant-enum pattern `user-config.schema.json` already established for
`notifications.enabled` -- a manifest written before this key existed
still validates on read with `context` entirely absent.

### `IntakeExecutionHostedService` reads it fresh every attempt, never caches it

`ResolveTokenBudgetAsync` calls `ForgeApplication.GetProjectConfigurationAsync`
once per intake attempt (not once per tick, not once at startup),
matching this service's own established "no per-sprint memory, re-derive
from durable state" discipline from ADR 0028. The added cost is one
config read per attempt, not per tick -- intake attempts happen once per
sprint, ticks happen every 15 seconds for the sprint's whole lifetime.

### Falls back to `DefaultTokenBudget`, never fails the node, on any untrusted value

Three cases collapse to the same fallback: the project configuration is
unreadable (`ConfigurationView.DiagnosticCode != None`), the key is
absent (an unconfigured project, the common case), or the resolved value
is not a positive integer. The last case is deliberately defensive
rather than trusting the schema: `project-manifest.schema.json`'s
`minimum: 1` already rejects a non-positive value at write time and
(per the bug below) at read time too, but `ResolveTokenBudgetAsync` does
not own that validation and must not assume it always ran, since
`ContextManifestCompiler.Compile`'s own `ArgumentOutOfRangeException`
for a non-positive budget is deliberately outside
`IntakeExecutionHostedService`'s per-sprint catch filter (round 7 review
of PR #68 widened that filter to every exception eleven
`Persisted*`-DTO-corruption instances actually produced, not to every
exception a differently-shaped future bug could ever produce) -- a bad
value reaching `Compile` would fault the service, not degrade cleanly.

## A real bug found while implementing, not by review: YamlDotNet silently stringified the value

The first working version of this key round-tripped incorrectly: writing
`context.token_budget=40000` via `forge config project set` succeeded
(no exception, no rejected write), but reading it back always returned
the registered default, `32000`, as if the write had never happened.

Root cause: `YamlConfigurationStore.ReadFileAsync` deliberately
deserializes the on-disk YAML into an untyped `object` graph first
(`rawDeserializer.Deserialize<object>(yaml)`), *before* validating
against the schema or converting to the typed `ProjectConfiguration`
DTO -- it has to, since `NormalizeYaml`/`StripLegacySprintRegistry` must
inspect and strip unknown/legacy top-level shapes ahead of any typed
schema applying. YamlDotNet's default untyped `Deserialize<object>`
stringifies every scalar, so `token_budget: 40000` in the YAML became
the JSON string `"40000"`, not the number `40000`, once converted for
schema validation -- failing `project-manifest.schema.json`'s
`"type": "integer"` check with `InvalidDataException`.

That exception should have surfaced loudly. Instead, `YamlConfigurationStore
.ReadAsync`'s own recovery path (`catch (Exception error) when
(IsRecoverable(error) && File.Exists($"{path}.previous"))`) caught it,
found the automatic `.previous` backup `AtomicConfigurationFile.WriteAsync`
retains from the prior write, and silently restored that backup --
returning the *old*, pre-write document with no error surfaced to the
caller at all. The write had genuinely succeeded; the very next read
silently discarded it. This is the exact same "stringifies every
scalar" behavior `ForgeDocumentCompiler`'s own `typedDeserializer` field
comment already documents as "a real, verified behavior -- not just a
risk" for `.forge/` frontmatter parsing -- but that fix (deserializing
directly into a typed class) is not available here, for the same reason
given above: this store's untyped pass has a job typed deserialization
cannot do.

Fixed with YamlDotNet's own builder option for exactly this case:
`.WithAttemptingUnquotedStringTypeDeserialization()` on `rawDeserializer`,
which makes the untyped pass itself scalar-type-aware (int, bool, etc.)
instead of defaulting every plain scalar to string. Every other
project-manifest field is a string or UUID, so nothing else changes
behavior; verified by reverting the fix in isolation and confirming
`TokenBudgetRoundTripsAsAProjectScopedIntegerDefaultingToTheRegisteredValue`
fails with the exact `Actual: 32000` (silently-discarded-write) symptom.

Round 1 review of this PR probed the option directly against YamlDotNet
18.1.0 rather than trusting this claim on inspection alone: it does
**not** coerce YAML 1.1's other boolean-like tokens (`yes`/`no`/`on`/`off`/
`y`/`n` -- a real hazard for `language.ui: "no"`, a genuine Norwegian
language tag, had this option been applied to `user-config.schema.json`'s
own `rawDeserializer` too, which it was not), UUID-shaped strings, or
version-shaped strings like `1.1.0`. The claim holds, but not because the
deserializer itself refuses to touch those shapes on principle -- it
holds because `YamlConfigurationStore`'s own serializer writes every
string scalar unquoted (`project_id: af33aba4-...`, not
`project_id: "af33aba4-..."`), and the *schema*'s own `format: "uuid"`/
`pattern`/`const` constraints are what actually catch a genuine
misinterpretation, not the deserializer's type inference. Recorded here
so a future field added without a matching schema constraint cannot rely
on the same accidental protection.

**Named, not silently fixed and forgotten**: the underlying
recovery-swallows-the-real-error behavior in `YamlConfigurationStore
.ReadAsync` is a separate, pre-existing risk this ADR did not fix --
*any* write that produces a document failing re-validation on the very
next read (not just this specific type-inference bug) degrades silently
to the previous value with no diagnostic surfaced anywhere, for both
user and project stores. Out of this slice's own scope (a config-key
addition, not a `YamlConfigurationStore` redesign); flagged as a
candidate for a future slice that gives this failure mode an actual
diagnostic path instead of a silent revert.

## Round 1 review (full-scope) -- a second real bug, this time genuinely new

Round 1 found and confirmed by direct reproduction a second, more
serious bug distinct from the write-time-discard one above: a
`context.token_budget` value satisfying `project-manifest.schema.json`'s
original `"integer, minimum: 1"` (JSON Schema's `"integer"` type has no
bit-width of its own) but exceeding `Int32.MaxValue` -- reachable only
by hand-editing `manifest.yaml` directly, since
`ConfigurationSchemaCodec.GetOptionalInt32`'s own `TryGetInt32` check
already rejects it cleanly on the normal write path -- threw an
unguarded `JsonException` from the later typed
`Deserialize<ProjectConfiguration>` call. That exception escaped
`ProjectRootResolver.ReadManifestAsync`'s own catch filter (which listed
`FormatException` but not `JsonException`) entirely uncaught, propagating
out through `ForgeApplication.GetProjectConfigurationAsync` -- reachable
through `rootResolver.ResolveAsync`, called before that method's own
try block even begins, not through the read wrapped by its try -- and
into `IntakeExecutionHostedService.ResolveTokenBudgetAsync`, which has
no try/catch of its own and assumes `GetProjectConfigurationAsync`
already degrades every failure. The result: `TickAsync`'s per-sprint
catch filter does not include `JsonException` either, so the whole
`BackgroundService` faulted and no sprint's intake ran again until a
Host restart -- strictly worse than the graceful degradation this ADR's
own "falls back... on any untrusted value" decision claims to guarantee.

Reproduced directly against the built assembly (not inferred from
reading code): a probe test writing `context: {token_budget:
3000000000}` into a real `manifest.yaml` and calling
`ForgeApplication.GetProjectConfigurationAsync` threw exactly the
predicted `JsonException`/inner `FormatException`, with a stack trace
confirming the escape path through `ProjectRootResolver.ReadManifestAsync`.

Fixed at the root and in depth, three layers:

1. `project-manifest.schema.json`'s `token_budget` property gains an
   explicit `"maximum": 2147483647` (`Int32.MaxValue`), so schema
   validation itself now rejects an out-of-range value *before* it ever
   reaches typed deserialization -- the value never gets far enough to
   throw `JsonException` at all for this specific field.
2. `ProjectRootResolver.ReadManifestAsync`'s catch filter gains
   `JsonException`, so even without the schema bound, a corrupt manifest
   degrades to `DiagnosticCodes.ProjectDirectoryUnknown` instead of
   faulting the caller.
3. `YamlConfigurationStore.IsRecoverable` (the filter guarding its own
   `.previous`-recovery attempt) gains `JsonException` too, for the same
   reason -- a second, independent place a caller can observe
   `ReadFileAsync`'s typed-deserialization throw.

Each layer verified independently by reverting it in isolation and
confirming `AnOutOfInt32RangeTokenBudgetInTheManifestFallsBackToTheDefaultWithoutFailingIntake`
still passes on layers 2+3 alone (schema bound reverted), then
confirming all three reverted together reproduces the original fault
exactly (`WaitForNodeStateAsync` times out with the node stuck at
`Running`, matching the "no sprint runs again" symptom), then restoring
all three. A companion unit test,
`AnOutOfInt32RangeTokenBudgetIsRejected`, pins that the normal write
path was never actually vulnerable -- confirming the corruption really
does require a hand-edited file, not a gap in `SetConfigurationAsync`.

Two further findings from the same round, both addressed without new
production risk: `docs/architecture/overview.md`'s explicit
"Project scope owns:" list had not been updated for
`context.token_budget` (fixed); and this ADR's own claim that
`WithAttemptingUnquotedStringTypeDeserialization()` leaves "every other
project-manifest field" unaffected was verified directly against
YamlDotNet 18.1.0 rather than trusted on inspection, and narrowed to
state precisely *why* it holds (see the "Fixed with YamlDotNet's own
builder option" paragraph above, extended with this round's findings).
A fifth, moderate finding -- that
`AnUnreadableProjectConfigurationFallsBackToTheDefaultTokenBudgetWithoutFailingIntake`
cannot by itself distinguish `ResolveTokenBudgetAsync`'s explicit
diagnostic-code guard from simply falling through to the same default,
since `GetProjectConfigurationAsync` returns an empty `Values` list on
every failure path regardless -- is recorded honestly in that test's own
comment rather than papered over with a claim the test cannot back.

## Consequences

- `docs/contracts/v1/configuration.json`: `contract_version` 1.2.0 ->
  1.3.0; new `context.token_budget` key (`scope: project, default:
  32000, session_override: false, sensitive: false`).
- `docs/contracts/v1/schemas/project-manifest.schema.json`:
  `schema_version` enum gains `"1.1.0"`; new optional `context` object
  with `token_budget` (`integer, minimum: 1, maximum: 2147483647` --
  the upper bound is round 1's own fix, see above).
- `ConfigurationSchemaCodec`: `ProjectContractVersion` 1.0.0 -> 1.1.0;
  new `GetOptionalInt32`/`Add(..., int?)` helpers; new `ProjectContext`
  DTO wired into `ToProject`/`FromProject`.
- `YamlConfigurationStore`: `rawDeserializer` now built with
  `.WithAttemptingUnquotedStringTypeDeserialization()` (see the bug
  above) -- a correctness fix for the store generally, not specific to
  this one key; `IsRecoverable` also widened to include `JsonException`
  (round 1).
- `ProjectRootResolver.ReadManifestAsync`'s catch filter widened to
  include `JsonException` (round 1) -- the actual escape point for the
  out-of-int32-range bug above.
- `IntakeExecutionHostedService`: new `ForgeApplication` constructor
  dependency; `DefaultTokenBudget` is now the documented fallback, not
  an unconditional literal; `ResolveTokenBudgetAsync` resolves the
  effective budget fresh every intake attempt.
- `forge config project set context.token_budget <n>`, `forge config
  show`, and the Desktop app's own generic configuration editor
  (`MainPage`'s scope picker plus key/value entry, `MainPageViewModel
  .SetConfigurationAsync`) already work for this key with zero new CLI
  or Desktop code -- all three are fully generic over
  `ConfigurationRegistry`, confirmed by tracing both surfaces' existing
  `config` implementations before starting this slice, not assumed.
- Explicitly deferred, and named as such: the `YamlConfigurationStore`
  silent-recovery diagnosability gap above; per-node context-manifest
  scoping (still deferred from ADR 0014); durably persisting the
  manifest itself (still deferred from ADR 0028); every model-bearing
  role executor and `SprintGitIsolation`'s lifecycle (unchanged from
  ADR 0028's own deferred list).

## References

- ADR 0012 (reproducible context assembly) -- the manifest and
  admit-or-truncate budget policy this key tunes.
- ADR 0014 (frozen execution profiles) -- the still-deferred per-node
  context scoping this key does not attempt to solve.
- ADR 0019 (human-gate and supersession CLI commands) -- the generic
  `forge config user|project show|set` command this key needed no new
  code from.
- ADR 0024 (best-effort local notifications) -- `notifications.enabled`,
  the immediately preceding configuration-key precedent this ADR's
  registry/schema/codec changes mirror structurally.
- ADR 0028 (intake node execution) -- `DefaultTokenBudget`'s own
  originating debt note, and the per-sprint catch filter this key's
  fallback logic must not rely on for a non-positive value.
