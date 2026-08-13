# AI-assisted software delivery research

**Status:** research summary  
**Updated:** 2026-08-13

This document preserves the research conclusions that inform Forge. It is not a
second architecture specification: the [architecture overview](overview.md),
[ADR 0001](decisions/0001-stage-0-foundation.md), versioned
[contracts](../contracts/v1/), and [implementation plan](../plans/implementation-plan.md)
are canonical.

## Research question

How can a local tool coordinate AI coding harnesses without making a model the
owner of workflow state, permissions, Git operations, or completion criteria?

The answer is a small deterministic control plane around official harness CLIs.
Models perform bounded judgment work; code controls state transitions, retries,
validation, isolation, and release gates.

## Conclusions

### Use a durable workflow, not a chat transcript

Sprint inputs, base commits, attempts, findings, decisions, and artifacts must
be durable records. A transcript is optional context, not application state.
This makes restart, audit, and independent review possible.

### Keep execution attempts isolated

Every write attempt starts from an explicit base commit in its own worktree.
Provider or model fallback replays the attempt from that clean base rather than
continuing partial edits. This prevents hidden cross-model state and makes a
failed attempt inspectable.

### Treat models as replaceable workers

Provider selection, retry budgets, cooldowns, and normalized failures belong to
the control plane. A model may propose or implement a bounded task, but cannot
self-certify a test, decide a permission gate, or mutate durable state outside
the application command path.

### Retrieve code context progressively

Use repository rules, task artifacts, structured handoffs, Git, `rg`, and
targeted file reads. The MVP owns no semantic index; add one only after a
measured exact-retrieval failure justifies its freshness and lifecycle cost.

For multi-step retrieval, accept a bounded declarative query plan rather than
model-authored executable code. Deterministic code validates the read-only
operations, applies budgets, and returns only the selected structured bundle to
the next model call while retaining enough provenance to rebuild it.

### Treat context and capabilities as explicit inputs

Every model node starts fresh and receives only a frozen context manifest and the
capabilities resolved for its execution profile. Parent transcripts, ambient
tools, and human-only commands are not inherited. A child cannot widen the
parent's authority.

### Require an explicit terminal result

Process completion is not workflow completion. A worker must return one
schema-valid terminal result for its attempt; normal exit without that result is
a failure. Deterministic validation and gates, never provider wording or exit
code alone, decide success.

### Keep configuration and generated outputs scoped

Personal interaction preferences and reproducible project policy have different
owners and lifecycles. Generated harness-native files are derived outputs, not
the source of truth. This avoids synchronizing the same rule set manually
across tools.

### Learn through reviewable proposals

Reusable lessons may become post-MVP knowledge proposals backed by durable
events and artifacts. They remain inert until a human approves a diff against
the canonical `.forge/` source. Generated provider skills, transcripts, and raw
provider output never write project knowledge directly. Optional personal
memory is separate, hard-capped, frozen per attempt, and limited to preferences
and verified environment facts.

### Prefer independent and require convergent reviews

A reviewer needs a clean task context and must inspect the change rather than
the implementer's hidden reasoning. Findings are resolved through bounded
iterations; later passes focus on critical regressions. A review that finds no
issues must say so explicitly.

Forge uses one scope/rubric-driven review engine. It first tries configured
reviewers whose provider/model lineage differs from implementation, then falls
back in normal priority order when full separation is unavailable. Provider/model
independence is recorded and best-effort; a new attempt, clean context, file and
rubric coverage, deterministic gates, and review itself remain mandatory. Design
and implementation each own a durable counter, and cumulative low/medium/high/
critical budgets raise the severity floor. Identical normalized external finding
sets or the cumulative iteration limit create an explicit human
continue/accept-or-override/abort gate. Git or diff inactivity is not treated as
proof of convergence.

## Evaluated patterns

| Pattern | Adopt | Do not adopt as-is |
|---|---|---|
| Agentic Software Development | durable phases, independent review, traceability | a harness owning the top-level workflow |
| Graph engineering | bounded nodes, deterministic edges, explicit convergence | LLM-managed routing and retries |
| Docker Agent | declarative configuration and provider fallback semantics | replacing official coding harnesses without equivalent isolation guarantees |
| Codebase Memory | rebuildable structural retrieval | treating an index as source of truth |
| Spec-driven development | explicit intent, plan, and acceptance artifacts | generating documents without execution traceability |
| Agent catalogs | shared source and progressive disclosure | importing a large role catalog before it is needed |
| Tree-sitter, LSP, and SCIP | defer until exact retrieval measurably fails | requiring an index for ordinary repository work |
| Hermes Agent | bounded hot context, on-demand exact recall, fresh subagent context, explicit worker outcomes, compact tool pipelines, and approval-gated skill learning | arbitrary model-authored execution, transcript-backed project truth, silent skill mutation, model-judged completion, shadow Git checkpoints, and unrestricted hooks |

## Deferred questions

- Whether a distributed runtime is needed after the local workflow proves its
  limits.
- Which optional semantic index has measurable retrieval value for Forge users.
- Whether organization-scale artifact distribution should use OCI bundles.
- Which additional provider and platform adapters justify expanding the explicit
  built-in composition beyond the Windows MVP.
- Whether approved knowledge proposals and hard-capped personal memory improve
  measured task quality enough to justify their post-MVP lifecycle and security
  cost.

These are product decisions, not prerequisites for the Windows MVP.

## Hermes Agent references

- [Programmatic tool calling](https://hermes-agent.nousresearch.com/docs/user-guide/features/code-execution/)
- [Subagent delegation](https://hermes-agent.nousresearch.com/docs/user-guide/features/delegation/)
- [Persistent memory](https://hermes-agent.nousresearch.com/docs/user-guide/features/memory/)
- [Approval-gated skill writes](https://hermes-agent.nousresearch.com/docs/user-guide/features/skills/)
- [Kanban worker lanes](https://hermes-agent.nousresearch.com/docs/user-guide/features/kanban-worker-lanes/)
