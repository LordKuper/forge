# ADR 0013: `implementation-critical` DAG and the confirmation gate

- Status: Accepted
- Date: 2026-08-17
- Contract version: 1.0.0

## Context

Stage 11 (`docs/plans/implementation-plan.md` P11.1-P11.12) must give every
managed project a concrete `implementation-critical` graph — Forge's one
enabled workflow (ADR 0001) — with isolated implementation, confirmation,
and test-work nodes, followed by deterministic final gates, review, human
approval, and finalization. `docs/architecture/overview.md`'s "Workflow
durability" section already commits to the load-bearing rule this stage must
make concrete: "The Host schedules separate implementation, confirmation,
and test-work nodes; test work is not eligible until a valid confirmation
artifact exists." AGENTS.md's Quality section states the same rule for
contributors: implement first, confirm the definition of done or user
expectations through inspection/execution/existing checks, and only then
select or author the smallest risk-based set of new tests — a no-new-test
result needs evidence, not just a decision.

Nothing in the repository builds this graph today. `SprintScheduler`
(Stage 6) is a deterministic engine over a caller-supplied graph; nothing
constructs an `implementation-critical` graph for it to run, and
`NodeKind` distinguishes only `Work` from `HumanGate` — nothing distinguishes
an implementation node from a confirmation or test-work node except a
string id a caller happens to choose. `ExecutionProfile.Phase` similarly has
only `planning`/`implementation`/`review` (ADR 0006), and no node executor
exists anywhere yet to actually run a provider inside one of these nodes —
that lands with this stage's later execution-profile and provider-attempt
items (P11.13 onward). This ADR is scoped to what is buildable without that
executor: the graph shape, the confirmation artifact a confirmation node
records, and the scheduler-level gate that keeps a test-work node from ever
running ahead of it. `docs/architecture/ai-agentic-software-development
-workflow.md`'s "evaluated patterns" table explicitly rejects "importing a
large role catalog" — the plan's own wording, "use behavior nodes and
rubric data, not a seven-role catalog," is that rejection applied to this
stage: nodes are tagged with what they *do*, not cast as personas, and
intake/planning assess a change against small, fixed rubric data rather
than a role hierarchy.

## Decisions

### Node roles

`NodeRole` (`Forge.Domain`) is a new, purely descriptive tag on
`NodeDefinition`, additive alongside the existing `NodeKind`:
`Generic | Intake | Planning | Implementation | Confirmation | TestWork |
Review | HumanApproval | Finalization`. `NodeDefinition.Role` defaults to
`Generic`, so every existing caller-constructed graph (tests, and any future
non-`implementation-critical` graph) keeps compiling and behaving exactly as
before. Every role except `TestWork` is descriptive only today — a label
the built-in graph carries so a future executor and presentation layer can
tell node kinds apart without parsing ids — not a distinct scheduler code
path. `TestWork` is the one role `SprintScheduler` treats specially (below).

### The built-in graph

`ImplementationCriticalGraphBuilder.Build()` (`Forge.Compiler`) is a pure,
deterministic function producing the one canonical graph:

```
intake -> planning -> implementation -> confirmation -> test_work -> review -> human_approval -> finalization
```

`human_approval` is the only `HumanGate` node; every other node is `Work`.
`SprintOrchestrator.CreateSprintAsync` uses this graph whenever a caller
passes `Graph: null` — `implementation-critical` being the only enabled
workflow (ADR 0001) makes this the correct default for every managed
project, not a per-caller choice. A caller that supplies its own graph
(including an explicitly empty one) keeps full control; every existing test
that exercises a minimal or custom graph already does this and needed no
change.

### Confirmation artifact

`ConfirmationArtifact` (`Forge.Domain`, `confirmation-result.schema.json`)
is a confirmation node's recorded judgment: an `Outcome`
(`Confirmed`/`NotConfirmed`), the `DefinitionOfDone` or expectation text it
was judged against, a non-empty list of `ConfirmationEvidence`
(`Inspection`/`Execution`/`ExistingCheck` — matching AGENTS.md's own
"inspection and execution... existing checks may support confirmation"
wording exactly, schema-required the same way `finding.schema.json` already
requires at least one evidence entry), and `RecordedAt`.
`SprintScheduler.RecordConfirmationAsync` persists it through the same
state-independent, digest-free pattern `RecordHandoffAsync` already
established — no attempt id, no required node state — so it can be recorded
(or replayed) without racing the node's own transitions. `nodeId` must name
a node in the sprint's frozen graph tagged `Confirmation` (`node_not_found`/
`node_kind_mismatch` otherwise); an unchecked node id would let an artifact
gate nothing real, or block a sprint on an id nothing controls once a real
change starts feeding this method from provider output.

`RecordedAt` exists because a confirmation node is not write-once — it can
be re-attempted (its own rejection, a retried node), producing more than one
artifact for the same node id. Eligibility (below) always reads only the
*most recently recorded* artifact per node, never "any `Confirmed` ever
recorded": otherwise an early `Confirmed` verdict would permanently latch a
gate open even after a later `NotConfirmed` verdict for the same node.

What a test-work node then does with a `Confirmed` verdict — select tests,
or record a justified no-new-test decision — is not modeled here. Nothing
constructs a test-work node's own result yet, the same "shape now, producer
later" gap ADR 0009 left for `Handoff`; that lands once P11.13 onward gives
test-work a real executor to drive. AGENTS.md's contributor-facing no-new-test
justification rule continues to apply procedurally in the meantime, exactly
as it does today.

### The test-work gate

`SprintScheduler.IsTestWorkEligibleAsync` is the one new chokepoint: for a
`TestWork`-role node, every `Confirmation`-role dependency's *latest*
recorded `ConfirmationArtifact` (ordered by `RecordedAt`) must have
`Outcome == Confirmed` before the node is eligible. It is checked in exactly
two places:

- `AdvanceGraphAsync`'s promotion loop — a `TestWork` node's dependencies
  reaching `succeeded`/`skipped` is necessary but no longer sufficient; the
  node stays `pending` until a `Confirmed` artifact exists for its
  confirmation dependency. This is the literal "Host state transitions must
  reject premature test work": the graph's own structural completion can
  never promote it early.
- `StartAttemptAsync` — an explicit attempt to start an ineligible
  `TestWork` node is rejected with the new `workflow_blocked` diagnostic
  (`docs/contracts/v1/README.md` already reserved this code for "durable
  workflow cannot safely advance"; this is its first implementation) rather
  than falling through to a generic version-conflict code.

`RecordConfirmationAsync` re-drives `AdvanceGraphAsync` after a `Confirmed`
save, so a test-work node already sitting `pending` on that exact
confirmation is promoted immediately rather than waiting for an unrelated
caller to advance the graph. A `NotConfirmed` verdict instead blocks a
`Running` (or already `ReadyToFinalize`) sprint outright, mirroring how a
late open `Finding` already blocks one in `RecordFindingAsync` — a fourth
`BlockedReasonArgument` value, `confirmation`, joins the existing `node`/
`finding`/`gate` set, and, like those, requires the operator's explicit
`resume_sprint`/`run_sprint` decision rather than recovering on its own.
That blocking append is retried (bounded, five attempts, re-reading sprint
state each time) rather than fired once: it is the sprint's only
operator-visible signal that a `NotConfirmed` verdict landed, so a lost
append here would leave the sprint silently `running` with nothing to draw
attention to it — even though `IsTestWorkEligibleAsync` itself would still
correctly deny eligibility either way, since it reads the artifact directly
rather than the sprint's blocked state.

### Threat/rule rubric

`BuiltInRubric` (`Forge.Domain`) is a small, fixed `RubricItem` catalog —
five threat categories (secret exposure, destructive action, untrusted
input, dependency risk, scope creep) and three rule categories drawn from
AGENTS.md's own sections (portability, implementation-first testing, commit
and version discipline) — each a plain `(Id, Category, Description)` triple,
not a role or persona. Deliberately data only, with no assessment/evaluation
type alongside it: unlike `ConfirmationArtifact` and `Handoff`, which at
least have a full schema, store, and codec pipeline waiting for a producer,
an assessment record would have had none of those — no producer, no
consumer, no persistence, nothing to test beyond its own field names. That
is speculative modeling the plan item does not ask for yet, not the "shape
now, producer later" precedent ADR 0009 set; the catalog itself is what the
item's "rubric data" phrase requires, and is added here on its own.

## Consequences

Every managed project's sprint now gets the same frozen, inspectable DAG by
default, with implementation, confirmation, and test-work as genuinely
separate, independently addressable nodes rather than a convention a caller
has to know. The one behavioral rule the plan requires — test work cannot
run ahead of a valid confirmation — is enforced at the single chokepoint
every node promotion and every attempt start already passes through, so a
future executor cannot bypass it by calling the scheduler differently.
Intake, planning, review, and finalization remain named nodes with no
executor behind them, and the rubric catalog remains unevaluated data, until
Stage 11's execution-profile and provider-attempt work (P11.13 onward) gives
them one.

| Action | Recovery |
|---|---|
| promote a `TestWork` node whose confirmation is missing or `NotConfirmed` | node stays `pending`; no diagnostic (same as any other unmet dependency) |
| start a `TestWork` node's attempt while ineligible | rejected with `workflow_blocked`; no state change |
| record a `NotConfirmed` confirmation on a `Running`/`ReadyToFinalize` sprint | sprint moves to `Blocked` with reason `confirmation` (retried up to 5 times on a version conflict); requires an explicit `resume_sprint`/`run_sprint` |
| record a `Confirmed` confirmation | graph is re-advanced immediately; any now-eligible `TestWork` node promotes to `ready` in the same call |
| record a `NotConfirmed` confirmation after an earlier `Confirmed` one for the same node | the later, `RecordedAt`-latest artifact governs; eligibility already granted is not itself revoked, but no further promotion or attempt start on that dependency succeeds |
| record a confirmation against an unknown node id or a node not tagged `Confirmation` | rejected with `node_not_found`/`node_kind_mismatch`; nothing is persisted |
