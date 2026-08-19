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

## Deliberately deferred

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
  `FileSprintEventLog`'s `Persisted*` DTOs.** Round 4 named this
  directly: six instances of the identical "unwrapped exception escapes
  the catch filter" defect were found and fixed one call site at a time
  across four review rounds, and no seventh instance is guaranteed not
  to exist in code this PR did not touch. A single read path that
  validates required-field presence and type before any per-field
  access would close the whole defect class at once. Out of this PR's
  own scope (intake execution, not a `FileSprintEventLog` redesign).

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
  every `ISprintStore` caller, not just this service, benefits.
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
