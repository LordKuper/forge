# ADR 0028: Intake node execution

- Status: Accepted
- Date: 2026-08-19
- Contract version: 1.0.0

## Context

Stage 11 P11.56-P11.66 needs a node executor. Nothing in this repository has
ever driven a sprint's node graph through a real attempt:
`SprintScheduler.StartAttemptAsync`/`CompleteAttemptAsync`,
`SprintGitIsolation`'s whole lifecycle, and `ILlmProvider.RunAsync` all have
zero production callers — the same fact ADR 0018, 0020, 0021, 0022, 0025,
0026, and 0027 each independently reconfirmed, and which this slice
reconfirmed again before starting.

A general node executor is not one slice. The model-bearing path needs
prompt assembly, the provider I/O protocol (ADR 0016), attempt deadlines and
process-tree teardown (ADR 0017), rate-limit deferral (ADR 0018), worktree
isolation (ADR 0004), and structured handoffs (ADR 0009) — each already
designed, none wired to anything.

Exactly one Work role needs none of that. `ExecutionProfilePolicy.PhaseFor`
returns `null` for `NodeRole.Intake`, so the built-in
`implementation-critical` graph's `intake` node (`ImplementationCriticalGraphBuilder`)
has no frozen execution profile, invokes no provider, consumes no routing
budget, has no session or idle deadline, and needs no worktree. Its job is
entirely deterministic: parse the project's `.forge/` rules and knowledge
(ADR 0009) and freeze the sprint's reproducible context manifest — which is
ADR 0012's own stated purpose, and whose compiler
(`ContextManifestCompiler.Compile`) had no production caller either.

That makes `intake` the one node that can be executed today, honestly and
completely, without stubbing any of the deferred machinery. This slice ships
that and nothing else.

It is also the first code in this repository that mutates durable workflow
state with no human command behind it. `ResumeSchedulerHostedService` and
`NotificationDeliveryHostedService` only re-derive, read, and deliver; neither
starts or completes an attempt. Idempotency and crash-resumability are
therefore treated as correctness requirements here, not polish.

## Decisions

### A new `IntakeExecutionHostedService`, named for what it actually does

`src/Forge.Host.Runtime/IntakeExecutionHostedService.cs`, mirroring
`ResumeSchedulerHostedService` structurally: an options record with a
test-overridable `PollInterval`, a `PeriodicTimer` loop over a private
`TickAsync`, `LoggerMessage.Define` logging, and no per-sprint memory at all —
every tick re-derives entirely from durable state. Registered as a plain
singleton and started/stopped by `ControlPlaneHostedService` only after that
Host has won the project lease, exactly like both siblings. That gating
matters most here: this is the one service that drives an attempt, so a Host
that lost the lease race must never reach a tick.

The name is deliberately not `NodeExecutionHostedService`. It executes the
`intake` role and refuses everything else; naming it for the general case
would imply shipped scope that does not exist.

The node is found by `NodeRole.Intake` + `NodeKind.Work`, never by the
`"intake"` string literal — a custom graph (the shape most of this
repository's tests use) legitimately has no intake node, and a node merely
named `intake` without the role carries none of the guarantees this executor
depends on, above all "needs no execution profile."

### A `.forge/` parse error records a diagnostic; it never fails the node

Intake succeeds with every `ForgeDocumentError` recorded as a
`NodeDiagnostic` (code = the parser's own `forge_document_*` code, category
`context`, message key `diagnostic.<code>`, one argument `relative_path`).
Two independent reasons:

- **Precedent.** `IntegrationGenerationService` already takes the identical
  `ForgeDocumentSet.Errors` input and treats it as degradation, not failure —
  one malformed document never blocks generation from the documents that did
  parse (ADR 0009's own "collect, do not throw" design).
- **Retry semantics.** Failing would invoke `CompleteAttemptAsync`'s bounded
  auto-retry policy, re-running a deterministic parse `MaxAutomaticRetries`
  more times against an unchanged file and then leaving the node `failed`
  with the sprint blocked. A malformed rule document is not something a retry
  can ever fix, and blocking a whole sprint on one unparsable optional
  document is a worse outcome than running with the context that did parse
  and saying so in the durable record.

Only the `.forge/`-relative path travels in the diagnostic arguments — never
the parser's own message (which can quote document content or a YAML parse
excerpt) and never an absolute path.

### Token budget: a fixed `32_000`, explicitly marked as debt

`ContextManifestCompiler.Compile` requires a token budget and no
configuration key for one exists anywhere — `docs/contracts/v1/configuration.json`
has none. Rather than invent a config key in this slice, the budget is a
fixed literal `IntakeExecutionHostedService.DefaultTokenBudget = 32_000`,
carrying a `ponytail:` comment naming the real fix, matching this
repository's own precedent for a frozen fallback with no configuration source
(`SprintScheduler.DefaultRateLimitBackoff`, ADR 0018).

**The number is an unverified MVP guess, not a measured value.** It is eight
times `ForgeDocumentCompiler`'s own 4,000-token per-document default cap, so a
project with a handful of ordinary rules and ADRs fits without truncation.
Guessing low degrades by truncation, never by failure (ADR 0012's own admit-
or-truncate policy), so the cost of being wrong is admitted context rather
than correctness.

### `input_digest` is the manifest digest; `outputs` are the admitted items' digests

`ContextManifest.ManifestDigest` is a pure function of the sprint's frozen
identity, the token budget, and every admitted item's own content digest
(ADR 0012) — precisely the description of what this attempt consumed, so it
is the `NodeResult`'s `input_digest`. `outputs` is every admitted item's
`ContextManifestItem.Digest`, rules then knowledge, in the manifest's own
deterministic order: the content-addressed handles to what intake actually
selected. Both shapes already satisfy `node-result.schema.json`'s required
`^sha256:[0-9a-f]{64}$` pattern, so `CompleteAttemptAsync`'s own
`WorkflowRecordCodec.ValidateNodeResult` fail-closed check needs no
pre-validation from this service.

### Crash-resumability is `SprintScheduler`'s, not a second mechanism

A node in `NodeState.Running` is treated as this service's own interrupted
attempt and resumed, not skipped: nothing else in this codebase starts a Work
node's attempt, and no other verb moves a `running` node onward, so skipping
it would strand the node permanently. `StartAttemptAsync` returns the node's
already-recorded `CurrentAttemptId` for a node it already moved to `running`,
and `CompleteAttemptAsync` treats a replay against an already-saved
`NodeResult` as done rather than as a conflict. A tick killed between the two
therefore finishes on the next tick — no duplicate attempt, no second
recovery mechanism of this service's own invention.

Corollary, deliberately accepted: if `.forge/` changed between the
interrupted start and the resume, the recorded manifest reflects the resume,
not the start. The manifest is frozen at completion, not at attempt creation.

### Poll interval 15 seconds, first tick immediate, all eligible sprints per tick

Reuses `ResumeSchedulerHostedService`'s exact interval: both services answer
the same question ("is durable state sitting on progress nothing else will
make?"), and a second, differently-tuned number would need a justification
this slice does not have. First tick is immediate rather than deferred
(`ResumeSchedulerHostedService`'s shape, not `NotificationDeliveryHostedService`'s):
an unexecuted intake node is a sprint making no progress at all, so waiting
out a full interval on Host startup is a real, if bounded, stall.

Every eligible sprint is swept every tick with per-sprint failure isolation,
copying `ResumeSchedulerHostedService` — intake does no provider work and
holds no exclusive resource, so there is nothing to serialize, and executing
one sprint per tick would make startup latency scale with sprint count for no
benefit.

`AdvanceGraphAsync` is called by this service itself rather than relied upon
from the resume scheduler's tick: the two run on independent timers, and
`AdvanceGraphAsync` is idempotent, so calling it from both costs nothing.

### Failure boundary: one `try`/`catch` per sprint, with an explicit guard for the one gap

The whole per-sprint body — the graph advance, the definition load, the
`.forge/` parse, the manifest compile, and both scheduler calls — is inside a
single `try`/`catch` matching `ResumeSchedulerHostedService`'s filter exactly
(`IOException or UnauthorizedAccessException or InvalidDataException or
InvalidOperationException`, never swallowing `OperationCanceledException`
while the stopping token is set). This is the defect class
`NotificationDeliveryHostedService`'s own comment history records: an
unhandled exception on a plain singleton `BackgroundService` faults
`ExecuteTask` permanently with nothing else ever observing it.

One reachable exception falls outside that filter:
`ContextManifestCompiler.Compile` throws `ArgumentException` for a blank
`source_commit`/`workflow`/`workflow_version`, which a `definition.json`
damaged after freeze could produce. Widening the filter to `ArgumentException`
would also swallow genuine programming errors, so the fields are checked
explicitly first and such a sprint is skipped with a dedicated log instead —
the same "this sprint cannot be served, the rest still can" outcome, reached
deliberately.

A rejected `StartAttemptAsync`/`CompleteAttemptAsync` (including
`WorkflowRecordInvalid`) is logged and left for the next tick's identical
idempotent path, never thrown — throwing after a start would leave a
`running` node behind.

## Round 1 review

Independent review found three issues, all fixed:

1. **A second, unnamed failure-boundary gap.** This ADR's own "one gap"
   claim above was incomplete: `CompleteAttemptAsync` reads existing node
   results via `ISprintStore.GetNodeResultsAsync`
   (`FileSprintEventLog.cs`), which — unlike `LoadAsync`/`LoadDefinitionAsync`
   — never normalized `JsonException`/`FormatException`/`OverflowException`
   into `InvalidDataException`. A corrupt `results/*.json` file therefore
   escaped this service's per-sprint catch filter entirely, permanently
   faulting `ExecuteTask` with nothing observing it — exactly the
   `NotificationDeliveryHostedService` defect class this ADR already cites,
   reproduced by a path this stage's node executor is the first to reach in
   production (`CompleteAttemptAsync` had zero production callers before
   this PR). Fixed at the root: `GetNodeResultsAsync` now wraps those three
   exception types into `InvalidDataException` per file, matching
   `LoadAsync`'s own contract, so every `ISprintStore` caller — not just
   this one — is entitled to treat a corrupt on-disk record as
   `InvalidDataException`.
2. **`ContextManifest.Truncated` was computed and then discarded.** A
   budget-truncated document left no diagnostic, no log, and no durable
   trace, while the strictly less consequential per-document token-cap
   overflow already produced one. At `DefaultTokenBudget = 32_000`
   (roughly 128 KB of `.forge/` text), an ordinary project of a few dozen
   rules and ADRs could truncate silently — and silently defeated this
   ADR's own "an unverified guess degrades by truncation, not failure"
   argument, since the only signal that would reveal a bad guess was being
   thrown away. Fixed: every `ContextManifestTruncatedItem` is now recorded
   as its own `NodeDiagnostic` (new `context_item_truncated` code, no
   diagnostic code having been reserved for this anywhere before), the same
   way a `.forge/` parse error already was.
3. **This ADR's own "reproducible by construction" claim, in the
   durably-persisting-the-manifest deferral below, did not hold.** ADR
   0012's reproducibility guarantee is conditional on a fixed
   `ForgeDocumentSet`; this executor parses the live, editable `.forge/`
   working tree while recording `source_commit = definition.BaseCommit`,
   which pins the sprint's code baseline, not `.forge/` content. Corrected
   to state the real, honest position: the manifest cannot be recomputed
   once `.forge/` changes after intake runs, and no downstream consumer
   exists yet to need it recomputed. The deferral itself is unchanged, only
   its justification.

## Round 2 review

Independent review found two further issues, both fixed at the root in
`FileSprintEventLog`, not just in this service's own catch filter:

1. **Round 1's `GetNodeResultsAsync` fix was incomplete.** It normalized a
   syntactically-invalid file into `InvalidDataException`, but a
   syntactically-*valid* file with an explicit `"outputs": null` or
   `"diagnostics": null` — `DefinitionJsonOptions` does not set
   `RespectNullableAnnotations`, so a hand-edited or torn-write file can
   carry that value despite `PersistedNodeResult`'s declared (but
   unenforced) non-nullable `List<T>` types — still threw a raw
   `ArgumentNullException` from `Enumerable.Select`, escaping every catch
   filter the same way the round-1 defect did. Fixed at both ends:
   `PersistedNodeResult.Outputs`/`Diagnostics` are now honestly typed
   `List<T>?`, and `GetNodeResultsAsync` null-coalesces both to `[]` before
   use. Regression-tested with
   `GetNodeResultsAsyncTreatsAnExplicitNullOutputsAndDiagnosticsAsEmptyRatherThanThrowing`;
   mutation-verified by reverting to the unguarded reads and confirming the
   test reproduces the exact `ArgumentNullException`.
2. **The "nothing this method reaches is outside this boundary" claim was
   still false, one call further out.** `AdvanceGraphAsync`'s own
   `IsTestWorkEligibleAsync` reads confirmations
   (`ISprintStore.GetConfirmationsAsync`), and `CompleteAttemptAsync`'s own
   `EvaluateCompletionAsync` reads findings (`GetFindingsAsync`, including
   its own unguarded `MigrateLegacyFindingsAsync` deserialize) — neither
   normalized a corrupt file the way `GetNodeResultsAsync` now does. Latent
   only because no executor can yet drive a sprint far enough for either
   read to reach a real corrupt file, so the gap would have gone live
   silently at the next node-executor slice. Fixed with the identical
   normalization pattern in `GetFindingsAsync`, `GetConfirmationsAsync`,
   and `MigrateLegacyFindingsAsync`, restoring the boundary comment's claim
   to actually true rather than removing it.
   Regression-tested with `GetFindingsAsyncWrapsACorruptFindingFileInAnInvalidDataException`/
   `GetConfirmationsAsyncWrapsACorruptConfirmationFileInAnInvalidDataException`;
   both mutation-verified. `MigrateLegacyFindingsAsync`'s own narrower fix
   (its legacy-file deserialize is reached only once, before a project's
   first-ever findings migration) shares the same fix but has no dedicated
   test — accepted, named rather than silently skipped, since duplicating
   `GetFindingsAsync`'s own coverage for a corner already exercised by the
   identical exception-normalization code path was judged not worth a
   third near-identical fixture.

## Round 3 review (final full-scope round)

Independent review found two further issues — the same defect class
recurring a third time across `FileSprintEventLog`, both fixed at the
root:

1. **Round 2's null-list fix (`PersistedNodeResult`) was applied to only
   one of the two structurally identical types it should have covered.**
   `PersistedConfirmation.Evidence` had the same gap: an explicit
   `"evidence": null` threw `ArgumentNullException` from
   `FromPersisted`'s `confirmation.Evidence.Select(...)`, unguarded by
   `GetConfirmationsAsync`'s own catch filter, reachable from
   `AdvanceGraphAsync`'s own `IsTestWorkEligibleAsync`.
   `PersistedFinding.Evidence` was independently confirmed **not**
   affected — its own `FromPersisted` assigns the list directly, with no
   `.Select` to throw on a null source. Fixed the same way as round 2:
   `PersistedConfirmation.Evidence` is now `List<PersistedEvidence>?`,
   coalesced to `[]` before use. Regression-tested with
   `GetConfirmationsAsyncTreatsAnExplicitNullEvidenceAsEmptyRatherThanThrowing`;
   mutation-verified.
2. **A fourth failure-boundary gap, in a different `FileSprintEventLog`
   method this PR had not touched before this round.**
   `CompleteAttemptAsync` calls `RoutingLedger.GetRouteDecisionsAsync` on
   every successful completion (to refund the routing budget unit),
   reaching `LoadValidatedEventsAsync`, which caught only `JsonException`
   — narrower than `LoadAsync`'s own `JsonException or FormatException or
   OverflowException`. Its own `MigrateLegacyRoutingAsync` (a one-time
   migration for a pre-v0.11 routing sidecar) reads hand-parsed
   `JsonElement.GetProperty`/`.GetGuid`/`Guid.Parse` values that can also
   raise `KeyNotFoundException` (a missing property) or
   `ArgumentNullException` (`Guid.Parse(null)` from a JSON `null` where a
   string was expected) on a damaged legacy file — none of which were
   caught. Fixed by widening `LoadValidatedEventsAsync`'s catch to
   `JsonException or FormatException or OverflowException or
   KeyNotFoundException or ArgumentNullException`. Regression-tested with
   `ALegacyRoutingSidecarMissingARequiredPropertyIsWrappedInAnInvalidDataException`
   (`tests/Forge.Tests/Unit/RoutingLedgerTests.cs`, reusing that file's
   own existing legacy-sidecar fixture pattern); mutation-verified.

After three full-scope rounds, five distinct instances of the same
"unwrapped JSON/parse exception escapes a per-sprint catch filter"
defect class have been found and fixed across `FileSprintEventLog`
(`GetNodeResultsAsync` twice, `GetFindingsAsync`, `GetConfirmationsAsync`
twice, `MigrateLegacyFindingsAsync`, `LoadValidatedEventsAsync`/
`MigrateLegacyRoutingAsync`). Round 3 traced every `ISprintStore` read
method reachable from `TickAsync` (directly or transitively through
`AdvanceGraphAsync`, `StartAttemptAsync`, `CompleteAttemptAsync`) and
found none remaining unnormalized; `GetHandoffsAsync`/
`GetReviewIterationsAsync` share the same historical gap but are
genuinely unreachable from this service's own call chain today, so
fixing them is out of this PR's scope.

## Round 4 review (critical-only)

Independent review found one further critical issue — a **sixth**
instance of the same defect class, and the first found inside code
round 1 itself added rather than in a sibling method:

1. **`GetNodeResultsAsync`'s own `Guid.Parse(persisted.AttemptId)`
   (line 242) was never covered.** An explicit `"attempt_id": null`
   survives deserialization for the identical reason round 2/3's fixes
   applied elsewhere (`DefinitionJsonOptions` does not respect nullable
   annotations), and `Guid.Parse(null)` throws `ArgumentNullException` —
   the exact hazard round 3's own fix comment for
   `LoadValidatedEventsAsync` already named verbatim, left uncovered 425
   lines above it in a sibling method nobody re-checked. Unlike
   `Outputs`/`Diagnostics`/`Evidence`, a null `AttemptId` is not a
   legitimate empty value to coalesce — it is corrupt data — so the fix
   widens the catch filter (`ArgumentNullException` added) rather than
   defaulting the value. Regression-tested with
   `GetNodeResultsAsyncWrapsAnExplicitNullAttemptIdInAnInvalidDataException`;
   mutation-verified.

Six instances of the same defect class across four review rounds is
itself the finding worth naming plainly: this was whack-a-mole, not
systematic. A single shared deserialization helper that validates
required-field presence before any per-field access — rather than each
`Get*Async` method independently reading, catching, and re-checking its
own `Persisted*` DTO — would have caught all six at once and is the
right fix for the *next* time this class of bug appears, not merely a
seventh patched call site. Recorded as deferred cleanup below rather
than attempted in this PR, whose own scope is intake execution, not a
`FileSprintEventLog` deserialization redesign.

## Round 5 review (critical-only)

Independent review found two further critical issues — the seventh and
eighth instances of the same defect class, a different variant of it
this time:

1. **`GetNodeResultsAsync`'s `?? []` (round 2) guards a null *list*, not
   a null *element* inside a present one.** `"diagnostics": [null]`
   survives the coalesce untouched, and `FromPersisted(PersistedDiagnostic)`
   dereferences the null element directly, throwing
   `NullReferenceException`, uncaught by every prior round's filter.
   Fixed by adding `NullReferenceException` to the catch filter (not by
   filtering the null element out — a null diagnostic entry is corrupt
   data, matching this file's own "treat as corrupt, do not guess"
   convention, not something to silently drop). Regression-tested with
   `GetNodeResultsAsyncWrapsANullDiagnosticElementInAnInvalidDataException`;
   mutation-verified.
2. **Identical case for `GetConfirmationsAsync`'s `Evidence`**, on the
   exact line round 3 patched with `?? []`. Fixed the same way.
   Regression-tested with
   `GetConfirmationsAsyncWrapsANullEvidenceElementInAnInvalidDataException`;
   mutation-verified.

While closing these two, a **ninth** instance was self-identified before
launching the next round rather than left for it to find: `LoadDefinitionAsync`
has the identical null-list-element hazard across its own `graph`/
`dependencies`/`execution_profiles` arrays — and `LoadDefinitionAsync`
is `IntakeExecutionHostedService.ExecuteIntakeAsync`'s own *first* read,
ahead of `GetNodeResultsAsync`/`GetConfirmationsAsync` entirely, so an
unwrapped exception there escapes straight to `TickAsync`'s own filter.
Fixed the same way (`NullReferenceException` added to
`LoadDefinitionAsync`'s catch filter). Regression-tested with
`LoadDefinitionAsyncWrapsANullGraphElementInAnInvalidDataException`;
mutation-verified.

Nine instances of the same defect class across five review rounds
confirms rather than merely suggests the round-4 assessment above: this
is not converging by one-off patches. The deferred shared-validation-helper
item below is upgraded from "worth doing eventually" to the load-bearing
justification for why round 6, if it finds a tenth instance, should
trigger that redesign rather than a tenth narrow patch — recorded here
so that decision is not made silently in the moment.

## Round 6 review (critical-only) — the tenth instance, and a reconsidered decision

Round 6 was asked to exhaustively trace every `Persisted*` field
reachable from `IntakeExecutionHostedService`'s call chain, specifically
to test whether the round 5 commitment above should trigger. It found
the tenth instance: `LoadDefinitionAsync`'s own `graph`/`dependencies`/
`execution_profiles` — round 5 fixed this method's null-*element* case
(`NullReferenceException`) but not the null-*list* case one level up
(`"graph": null` itself, not `"graph": [null]`), which still reached
`Enumerable.Select`'s own null-check as an uncaught `ArgumentNullException`.
Round 6's own trace also surfaced two related, structurally identical
gaps in the same method: `PersistedNode.DependsOn` (a null list inside
one graph node) and a theoretical `PersistedNode.Id: null` reaching
`SprintGraphValidator`'s `Regex.IsMatch`.

**The round 5 commitment — that a tenth instance should trigger the
systematic redesign rather than another narrow patch — was reconsidered
here, not silently dropped.** Two systematic options were available: (1)
`RespectNullableAnnotations = true` on a store-read-scoped clone of
`DefinitionJsonOptions`, which would reject every non-nullable field's
`null` at the deserialization layer itself, as a `JsonException` already
covered everywhere; (2) the shared `Deserialize<T>` helper already named
as deferred cleanup. Both were rejected for this PR, for a reason
neither round 5's commitment nor round 6's own recommendation accounted
for: correctness of option (1) depends on every `Persisted*` type's
*every* field being correctly annotated (`?` exactly where, and only
where, `null` is legitimately possible) — an audit round 6's own
per-field trace effectively already did as a side effect, but which a
mechanical `RespectNullableAnnotations` flip does not itself verify, and
getting it wrong fails silently in the dangerous direction (a field that
should reject `null` but is mis-annotated `?` continues to accept it).
Doing that audit properly, plus the `Deserialize<T>` helper redesign, is
real, separate design work deserving its own slice and its own review
cycle — not a redesign folded into round 6 of an already six-round PR
under continued review pressure, which is exactly the condition most
likely to introduce the eleventh instance rather than close the class.

Fixed instead with the same proven pattern, applied completely this
time — but **not uniformly**, because the trace surfaced a real semantic
difference the mechanical pattern would have gotten wrong:

- `dependencies`/`execution_profiles`/`depends_on`: coalesced to `[]`,
  matching every prior instance — an empty dependency set, execution-profile
  set, or per-node dependency list is a legitimate value (the
  execution-profiles case was already a documented backward-compatibility
  rule in this method before this round).
- **`graph`: deliberately left uncoalesced.** An empty graph trivially
  passes `SprintGraphValidator.IsValid` (no nodes to violate any check),
  so coalescing it to `[]` would silently produce a frozen sprint
  definition with zero executable nodes — corrupt data masquerading as a
  valid, empty one, the opposite of every other coalesce in this file's
  established "treat unrecoverable corruption as corruption, not as
  empty" convention (the same reasoning round 4 already applied to a
  null `attempt_id`). Left to throw `ArgumentNullException` naturally,
  now caught by this method's own filter (widened to include it).
  Regression-tested with `LoadDefinitionAsyncWrapsAnExplicitNullGraphInAnInvalidDataException`;
  a companion `LoadDefinitionAsyncTreatsAnExplicitNullExecutionProfilesAsEmptyRatherThanThrowing`
  proves the asymmetry is intentional, not an oversight — mutating either
  branch to match the other fails its own test. Both mutation-verified.

Ten instances across six review rounds is recorded, not smoothed over:
the shared-validation-helper item below is restated as a concrete future
slice (design a validated read path plus the field-by-field nullability
audit round 6's trace already started), not merely "worth doing
eventually."

## Round 7 review (critical-only) — the eleventh instance, outside `FileSprintEventLog` entirely, and the actual systematic fix

Round 7 first verified round 6's own fix directly (the `graph`-throws /
others-coalesce asymmetry, the widened catch filter's scoping, and both
new tests) and found nothing wrong with it. It then found an eleventh
instance of the defect class — but this one broke the pattern the
previous six rounds had all shared, and that break is the actually
useful finding:

**`SprintScheduler.StartAttemptAsync`'s own `running`-node resume path
(`new(Guid.Parse(resumedAttemptId))`) parses `NodeSnapshot.CurrentAttemptId`
without a guard.** That value is not a `Persisted*` DTO field at all —
it is a free-form event-journal argument
(`docs/contracts/v1/schemas/event.schema.json` types every argument as
`string|number|boolean|null`, never validated as a GUID), copied
verbatim by `WorkflowFold.Apply` from whatever a `workflow.node_running`
event's `current_attempt_id` argument says. A corrupted (non-GUID) value
there throws an unguarded `FormatException` that no `FileSprintEventLog`
fix — including the still-deferred `RespectNullableAnnotations`/
`Deserialize<T>` redesign from rounds 4 through 6 — would ever have
caught, because it targets `Persisted*` deserialization specifically,
and this value never passes through a `Persisted*` type at all.

This is the finding that actually settles the "narrow patch vs.
systematic fix" question rounds 4 through 6 kept re-litigating one call
site at a time: **the eleven instances do not share a single root cause
narrow enough to fix at its source, but they do share a single place
they all become observable** — `IntakeExecutionHostedService`'s own
per-sprint boundary in `TickAsync`, the one point every one of the eleven
exceptions, from whichever inner method or file, has to pass through
before reaching the loop that would otherwise fault silently. Fixed
there instead of at an twelfth (or first-of-many-more) inner call site:
the per-sprint catch filter is widened to
`IOException`/`UnauthorizedAccessException`/`InvalidDataException`/
`InvalidOperationException` (unchanged, `ResumeSchedulerHostedService`'s
own precedent) plus every exception type any of the eleven instances has
actually produced — `FormatException`, `ArgumentNullException`,
`NullReferenceException`, `OverflowException`, `KeyNotFoundException`.
This does not replace the individual `FileSprintEventLog` fixes already
made (they still produce a specific, contextual `InvalidDataException`
message rather than a bare BCL exception, which is worth keeping for
diagnosability) — it is the backstop that makes a twelfth instance, wherever
it turns out to live, land as a logged-and-skipped sprint instead of a
silently faulted service, without this service having to re-audit
`SprintScheduler`'s or `FileSprintEventLog`'s internals every time either
one changes.

Regression-tested with
`ASprintWithACorruptCurrentAttemptIdDoesNotFaultTheServiceOrStopAnotherSprintsIntake`,
which required a genuinely targeted corruption technique: naively
replacing the attempt id's bare GUID text throughout `events.jsonl`
also corrupts the `AttemptChanged` event's own `aggregate_id` (the
identical GUID value, recorded separately) — `WorkflowFold.Apply` parses
*that* unconditionally on every load, so a broad replace makes
`LoadAsync` itself fail closed through the already-existing
`InvalidDataException` path, proving nothing about the resume-path
hazard the test exists to reproduce. The corruption is scoped to the
`"current_attempt_id":"<guid>"` substring specifically. Mutation-verified:
reverting the filter widening reproduces the exact silent-fault symptom
(`WaitForLogAsync` times out, `ExecuteTask` never observed to fault
because nothing polls it) rather than a clean assertion failure — the
same "test correctly proves absence of the fix" shape every prior
round's mutation verification has required.

- **Every model-bearing role: `planning`, `implementation`, `review`.** None
  execute. They need prompt assembly, the ADR 0016 provider I/O protocol, ADR
  0017 deadlines and process-tree teardown, ADR 0018 rate-limit deferral, and
  ADR 0004 worktree isolation — all designed, none wired. `ILlmProvider.RunAsync`
  still has zero production callers after this slice.
- **Every other Work role: `confirmation`, `test_work`, `finalization`.**
  None execute either. They need no provider, but they do need artifact
  producers this slice does not build (`ConfirmationArtifact`,
  `Handoff`), so they are not free the way `intake` is.
- **`SprintGitIsolation`'s whole lifecycle.** Still zero production callers.
  `intake` reads `.forge/` in the project root and owns no worktree.
- **`forge sprint rebase` and Desktop `sprint.rebase`.** Unchanged from ADR
  0027, and this slice does **not** unblock them: `WorktreeBaseMismatch` can
  only arise once attempts are driven through `SprintGitIsolation.IntegrateAsync`,
  which is the *model-bearing* executor path, not intake.
- **The two re-scoped snapshot fields** (integration status, phase profile).
  Blocked on the same model-bearing executor path, for the same reason —
  unchanged from ADR 0027.
- **A token-budget configuration key.** See the decision above.
- **Per-node context-manifest scoping.** ADR 0014 already deferred it and it
  is still deferred: one manifest is compiled per sprint from the whole
  `.forge/` set, with no mechanism to narrow it to what a specific node needs.
- **Durably persisting the manifest itself.** Only the digests reach durable
  state, through the `NodeResult`. There is no manifest store, so the
  layers, per-item token estimates, and truncation list are recomputed rather
  than read back. This is **not** the reproducible-by-construction guarantee
  ADR 0012 describes: that guarantee holds for a fixed `ForgeDocumentSet`,
  but this executor parses the live, editable `.forge/` working tree while
  recording `source_commit = definition.BaseCommit`, a value that pins the
  sprint's *code* baseline and does not pin `.forge/` content at all. Once
  `.forge/` is edited after intake runs, the recorded `input_digest`/`outputs`
  can no longer be recomputed from the project's current state — only from
  whatever `.forge/` looked like at the moment intake ran, which nothing
  keeps. Accepted anyway, honestly: a store that actually pins `.forge/`
  content (e.g. by committing or content-addressing it, not merely reading
  the working tree) is the consuming executor's concern, not intake's, and
  no downstream consumer of the manifest exists yet to need one.
- **`ContextManifestCompiler.WithQueryResults` (ADR 0012's layer 4).** Needs a
  model-proposed `ContextQueryPlan`, which needs the model-bearing executor.
  `SprintSpecifications` and `Handoffs` stay empty for the same reason.
- **A bound on recorded document diagnostics.** A project with hundreds of
  malformed `.forge/` documents writes one diagnostic each into the
  `NodeResult`. Bounded in practice by the file count under `.forge/rules`
  and `.forge/knowledge`; not worth a cap until a real project needs one.
- **Backing off a permanently-rejected completion.** If `CompleteAttemptAsync`
  rejects a well-formed result every time (it cannot with the digests this
  service produces, but a future corruption could), the tick retries it every
  interval forever. Logged, not rate-limited.
- **A shared, systematic deserialization-validation helper for
  `FileSprintEventLog`'s `Persisted*` DTOs, plus a field-by-field
  nullability audit of every `Persisted*` type — now explicitly a
  diagnosability improvement, not a correctness backstop.** Eleven
  instances of the identical "unwrapped exception escapes the catch
  filter" defect were found and fixed one call site at a time across
  seven review rounds; round 7's own eleventh instance broke the pattern
  the first ten shared (a `Persisted*` DTO nullability gap) — it was a
  free-form event-journal argument instead, which no `FileSprintEventLog`
  redesign could ever have caught, proving the systematic fix that
  actually closes this defect class for `IntakeExecutionHostedService`
  is the widened per-sprint catch filter in that round's own section
  above, not a `FileSprintEventLog`-level one. This item is downgraded
  accordingly: a `FileSprintEventLog`-level fix would still produce
  better, more specific `InvalidDataException` messages than the bare
  BCL exceptions the outer filter now settles for, and would benefit
  other future `ISprintStore` callers beyond this one service, but it is
  no longer load-bearing for this service's own correctness. A concrete
  future slice, at whatever priority that diagnosability improvement
  warrants: audit every `Persisted*` type's field nullability against
  what its own consumer actually requires, then either enable
  `RespectNullableAnnotations` on a store-read-scoped options clone or
  build the shared validating `Deserialize<T>` helper. Out of this PR's
  own scope (intake execution, not a `FileSprintEventLog` redesign).

## Round 8 review (critical-only) — a genuine concurrency race, root cause not conclusively identified

Round 8 stress-tested the service against a foreground command
(`RunSprintAsync`) repeatedly advancing the same sprint while the
background `IntakeExecutionHostedService` ticked against it every 50ms,
and found a real, reproducible race: `AppendTransitionAsync`'s outer
`catch (IOException) { return new(false, null,
DiagnosticCodes.WorkflowStoreBusy); }` fired at roughly 15-22% of
iterations, surfacing as both spurious background-service warnings and
actual foreground command failures — not a false alarm, a genuine
correctness gap under concurrent load.

Two fix attempts were tried before the one that worked:

1. **A torn-tail retry loop in `ReadEventsAsync`.** The theory: an
   in-flight `AppendLineAsync` write could be observed mid-write by an
   unlocked read, look like a torn tail (no trailing newline), and get
   truncated out from under the writer. Retrying the read briefly before
   concluding a tail is genuinely torn (rather than transiently
   in-flight) mirrors `ReadAllBytesWithRetryAsync`'s existing precedent.
   Kept — it is independently correct regardless of the outcome below —
   but stress testing showed it **did not reduce the failure rate** on
   its own.
2. **A `callerHoldsLock`-parameterized redesign** making the final
   truncate decision itself lock-protected (re-checking under the
   per-directory `Locks` semaphore immediately before truncating, so a
   truncate can never race a since-completed append), applied to all 7
   `ReadEventsAsync` call sites. Also kept as independently correct — it
   closes a real, if rarer, torn-write-truncation race — but stress
   testing showed it **made the observed failure rate worse in
   isolation (9/40)**, not better.

Neither matched the actual collision. Temporary rethrow instrumentation
(distinctive message prefixes at `AppendTransitionAsync`'s outer catch,
`TruncateAsync`'s own open, and `AppendLineAsync`'s own open, captured
via `dotnet test --logger trx` for untruncated output) traced the fault
to `AppendLineAsync` itself: `IOException: The process cannot access the
file '...events.jsonl' because it is being used by another process.`
Every one of `AppendLineAsync`'s four call sites already holds the same
directory's `Locks` semaphore, by inspection — static review of lock
acquire/release pairing, `SprintDirectory`/`EventsPath` string
consistency, and `SprintOrchestrator.RunSprintAsync`'s await chain found
no in-process second writer. **The exact OS-level mechanism was not
conclusively identified**; the working theory, recorded directly in the
code, is a short-lived handle-close/reopen window rather than a second
logical writer genuinely running concurrently.

The fix that resolved it does not depend on knowing the mechanism:
wrapping `AppendLineAsync`'s own file-open-and-write in a retry-on-
`IOException` loop (5 attempts, `20ms × attempt` backoff) — the same
"don't need to know why, just retry briefly, it's always transient"
philosophy `ReadAllBytesWithRetryAsync` already applies to the read side
of this identical file. Verified empirically, not just by inspection:
two independent stress-test batches of 35 iterations each, 0/35 and
0/35 (0/70 combined), against a documented ~15-22% baseline. Full suite
re-run clean at unchanged counts (788/788 net10.0, 876/876
net10.0-windows) — the pre-existing, previously-flaky integration tests
serve as this fix's regression coverage; no new test was added, since
the flake was never reliably reproducible on demand in a way a
deterministic unit test could pin without reintroducing the same
timing-dependent flakiness into the test itself.

All three changes ship together: the torn-tail retry and the
`callerHoldsLock` redesign are kept despite neither resolving the race
in isolation, because each is independently correct for the narrower
hazard it targets and neither appears to cause harm now that
`AppendLineAsync` also retries — reverting either would reopen a real,
if rarer, race for no benefit to this fix.

**CI proved the fixed 5-attempt budget itself insufficient.** The first
push of this fix (0/70 against local stress testing) still failed CI's
`Validate .NET solution` (Windows) job: `ReadAllBytesWithRetryAsync`
itself exhausted its own 5 attempts and surfaced the raw `IOException`
through `LoadAsync`, on a real CI runner, not a hypothetical one — the
same "a fixed attempt count or time budget survives local stress
testing but not CI-shaped load" shape ADR 0024's rounds 7-8 already hit
for a different hosted service's own file, measured there at 6.5x-20x
local slowdown under oversubscription. Fixed the same way ADR 0024 was:
replaced the fixed attempt counts on both `ReadAllBytesWithRetryAsync`
and `AppendLineAsync` with a single shared `RetryOnIOExceptionAsync`
helper retrying against a 10-second wall-clock deadline (capped
per-attempt backoff, `min(20ms × attempt, 200ms)`), so the budget scales
with however contended the actual host turns out to be rather than a
number picked from one local measurement. No local reproduction of the
CI-level contention that exposed the original gap was available (a
scripted way to synthesize it was attempted and blocked by this
environment's own tooling); the fix is the direct, previously-proven
precedent for this exact failure shape, verified locally at 0/40 under
ordinary load plus a clean full-suite run, with CI's own next run as the
real confirmation. That run was green, including the previously-failing
job.

## Round 9 review (critical-only) — two defects in round 8's own unreviewed fix

Round 9 reviewed the commit CI's own follow-up run had not yet been
independently scrutinized (the wall-clock retry-budget correction) and
found two further critical issues, both in `FileSprintEventLog.cs`:

**A self-deadlock.** `AppendTransitionAsync` holds the per-directory
`Locks` semaphore for its whole body and, in its idempotent-replay
branch, called the *public* `LoadAsync` to return the already-applied
state. `LoadAsync` reads with `callerHoldsLock: false` by default; if a
torn trailing line (ordinary crash residue) is present, `ReadEventsAsync`'s
own final-truncate path re-acquires that same non-reentrant semaphore on
the same async flow after exhausting `MaxTornTailAttempts` — a
self-deadlock, reachable through two entirely ordinary, designed-for
paths (crash residue plus a replayed idempotency key) rather than an
edge case. The method's own doc comment named `AppendTransitionAsync`
as a `callerHoldsLock: true` caller but missed this nested `LoadAsync`
call specifically. Round 9 reproduced it directly against `13c0676`
(append with key K, append a torn tail, append again with key K — still
pending at 20 seconds; the identical scenario against `86e5074`, the
state that passed rounds 1-7, completes immediately). Fixed by
extracting a private `LoadCoreAsync(projectRoot, id, cancellationToken,
callerHoldsLock)` that both the public `LoadAsync` (`callerHoldsLock:
false`) and this one replay call site (`callerHoldsLock: true`) share,
so the replay path passes the lock-already-held flag straight through
instead of going through the public entry point that always assumes
otherwise. Regression-tested with
`ReplayingAnIdempotencyKeyAfterATornTrailingLineDoesNotDeadlock`,
bounded by a 10-second cancellation deadline so a regression fails the
assertion instead of hanging the test run; mutation-verified (reverting
the fix reproduces `OperationCanceledException` at the semaphore wait,
inside `ReadEventsAsync`, exactly at the deadlock point).

**Non-idempotent write retry.** `AppendLineAsync`'s own `IOException`
retry (this PR's round-8-then-CI-follow-up fix) re-opens the file in
`FileMode.Append` and rewrites the whole line on every attempt, which is
only safe if a prior attempt wrote none of those bytes to disk — exactly
what the method's own comment already says is unknown ("the exact
mechanism was not conclusively identified"). If a prior attempt's bytes
reached disk before it threw, a retry either produces an unparsable torn
line (permanent `InvalidDataException`/`workflow_log_corrupted` on the
next read) or, if the whole line landed and only the post-write
housekeeping failed, a genuine duplicate — for a routing decision event
specifically, `RoutingLedger.BuildBudget` counts `Routed` decisions with
no dedup, so a duplicate silently burns a real budget unit. The CI
follow-up's own wall-clock deadline made this materially worse: up to
~50 retries in the worst case (10-second deadline, 200ms-capped
backoff) versus the original ≤4. Fixed by recording the file's length
before the first attempt and truncating back to it at the start of
*every* attempt, including the first — safe because the caller already
holds the directory's lock for the whole call, so nothing else can
observe or extend the file in between. Regression-tested with
`AppendTransitionAsyncRecoversFromATransientSharingViolationWithoutDuplicatingTheLine`,
using the same exclusive-lock technique
`LoadAsyncRecoversFromATransientSharingViolationOnTheJournal` already
uses for the read side, proving the retry recovers to exactly one
well-formed new line rather than a duplicate. Honestly scoped, not
silently treated as complete: a sharing violation on open never leaves
partial bytes on disk, so this test does not by itself exercise the
truncate-on-retry branch for a genuine mid-write failure — no
production hook exists to interrupt a `FileStream` between `WriteAsync`
and disposal to force that specific case deterministically, matching
this PR's own established honesty pattern for timing-dependent gaps
(round 8's own root-cause acknowledgment) rather than adding a
production-only test seam to close it artificially.

Both fixes were mutation-verified individually before being combined:
each one reverted in isolation reproduces its own specific symptom
against its own new test, with the other fix left in place.

## Consequences

- New `src/Forge.Host.Runtime/IntakeExecutionHostedService.cs`:
  `IntakeExecutionOptions` (`ProjectRoot`, optional `PollInterval`, 15s
  default) and `IntakeExecutionHostedService`, with a public
  `DefaultTokenBudget` constant.
- `ForgeHostApplication` registers both, as plain singletons, matching its two
  siblings; `ControlPlaneHostedService` takes the service as a constructor
  parameter and starts/stops it alongside `resumeScheduler`/`notificationDelivery`.
- First production callers of `SprintScheduler.StartAttemptAsync`,
  `SprintScheduler.CompleteAttemptAsync`, and
  `ContextManifestCompiler.Compile`.
- One new diagnostic code, `context_item_truncated` (round 1 review),
  recorded against a budget-truncated context item exactly like a `.forge/`
  parse error already was. No new message keys, contracts, schemas,
  configuration keys, or protocol verbs. A parse-error diagnostic reuses
  `ForgeDocumentDiagnosticCodes`' existing values verbatim.
- `FileSprintEventLog.GetNodeResultsAsync` (round 1 review) now normalizes
  `JsonException`/`FormatException`/`OverflowException` into
  `InvalidDataException` per file, matching `LoadAsync`'s own contract —
  every `ISprintStore` caller, not just this service, benefits. Rounds
  2-6 extended the same normalization to `GetFindingsAsync`,
  `GetConfirmationsAsync`, `MigrateLegacyFindingsAsync`,
  `LoadValidatedEventsAsync`/`MigrateLegacyRoutingAsync`, and
  `LoadDefinitionAsync`.
- `IntakeExecutionHostedService.TickAsync`'s own per-sprint catch filter
  (round 7 review) is widened past `IOException`/
  `UnauthorizedAccessException`/`InvalidDataException`/
  `InvalidOperationException` to also catch `FormatException`,
  `ArgumentNullException`, `NullReferenceException`, `OverflowException`,
  and `KeyNotFoundException` — the actual systematic closure for this
  service's own correctness, since round 7 found a corrupt-durable-state
  exception that no `FileSprintEventLog` fix could ever reach.
- No behavior change for a project with no sprints, a project whose sprints
  are all draft/terminal, or a sprint created with a custom graph that has no
  intake-role node.

## References

- ADR 0005 (local Host and control plane — the lease-gated hosted-service
  lifetime this service reuses)
- ADR 0006 (supervised execution — the routing/deadline policy `intake` is
  explicitly outside of)
- ADR 0009 (Forge document format — `ForgeDocumentCompiler` and its
  collect-do-not-throw error contract)
- ADR 0012 (reproducible context assembly — the manifest this node freezes,
  and its admit-or-truncate budget policy)
- ADR 0013 (implementation-critical DAG — the graph whose `intake` node this
  executes)
- ADR 0014 (frozen execution profiles — `PhaseFor(Intake) == null`, the fact
  that makes this slice possible; also the source of the still-deferred
  per-node context scoping)
- ADR 0018 (rate-limit deferral — `DefaultRateLimitBackoff`, the precedent for
  a frozen fallback constant with no configuration source)
- ADR 0024 (best-effort local notifications — the sibling hosted service whose
  whole-tick failure-boundary lesson this service applies)
- ADR 0027 (Desktop sprint-lifecycle controls — the `sprint.rebase` and
  snapshot-field deferrals this slice explicitly does not unblock)
