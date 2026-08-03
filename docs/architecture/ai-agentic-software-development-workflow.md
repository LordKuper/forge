# AI-assisted software delivery research

**Status:** research summary  
**Updated:** 2026-08-03

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

Start with repository rules, task artifacts, Git, search, and targeted file
reads. Add syntax, language-server, graph, or semantic indexes only when they
improve a concrete lookup. Derived indexes are accelerators and must prove
freshness against the source commit and tool version.

### Keep configuration and generated outputs scoped

Personal interaction preferences and reproducible project policy have different
owners and lifecycles. Generated harness-native files are derived outputs, not
the source of truth. This avoids synchronizing the same rule set manually
across tools.

### Make reviews independent and convergent

A reviewer needs a clean task context and must inspect the change rather than
the implementer's hidden reasoning. Findings are resolved through bounded
iterations; later passes focus on critical regressions. A review that finds no
issues must say so explicitly.

## Evaluated patterns

| Pattern | Adopt | Do not adopt as-is |
|---|---|---|
| Agentic Software Development | durable phases, independent review, traceability | a harness owning the top-level workflow |
| Graph engineering | bounded nodes, deterministic edges, explicit convergence | LLM-managed routing and retries |
| Docker Agent | declarative configuration and provider fallback semantics | replacing official coding harnesses without equivalent isolation guarantees |
| Codebase Memory | rebuildable structural retrieval | treating an index as source of truth |
| Spec-driven development | explicit intent, plan, and acceptance artifacts | generating documents without execution traceability |
| Agent catalogs | shared source and progressive disclosure | importing a large role catalog before it is needed |
| Tree-sitter, LSP, and SCIP | semantic lookup where text search is insufficient | requiring an index for ordinary repository work |

## Deferred questions

- Whether a distributed runtime is needed after the local workflow proves its
  limits.
- Which optional semantic index has measurable retrieval value for Forge users.
- Whether organization-scale artifact distribution should use OCI bundles.
- Which provider capabilities are sufficiently stable for automatic toolchain
  update and verification.

These are product decisions, not prerequisites for the Windows MVP.
