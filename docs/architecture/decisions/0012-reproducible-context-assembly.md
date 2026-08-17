# ADR 0012: Reproducible context assembly

- Status: Accepted
- Date: 2026-08-17
- Contract version: 1.0.0

## Context

Stage 10 (`docs/plans/implementation-plan.md` P10.1-P10.20) must give a model
node a bounded, sprint-scoped, reproducible context without inheriting a
conversational transcript. `docs/architecture/overview.md`'s "Context
assembly" section already commits to four progressive-disclosure layers:

1. always-on rules and workflow contracts;
2. sprint-scoped specifications and decisions;
3. project knowledge, accepted ADRs, and structured handoffs;
4. exact Git, file, and `rg` lookup under a recorded token budget.

ADR 0009 built layer 1's rules and layer 3's project knowledge/ADR source
(`.forge/rules/*.md` and `.forge/knowledge/*.md`, parsed by
`ForgeDocumentCompiler` into a `ForgeDocumentSet`) and explicitly deferred
layer 2 (sprint-scoped specifications/decisions) and layer 3's structured
handoffs to Stage 11's not-yet-built attempt/planning executor — confirmed
again here: `Handoff` (`src/Forge.Runtime/Domain/Handoff.cs`) has zero real
construction sites in the repository today, only its schema shape and a
durability validator. This ADR must assemble a manifest whose shape already
covers all four layers — so Stage 11 adds a source, not a new field — while
only ever populating layers 1 and 3's document half from real MVP content.

Layer 4 needs a declarative, bounded, read-only Git/file/`rg` lookup a model
can request through a plan Forge itself validates and executes — never a
model-authored script or shell pipeline, and never a widened capability.
Nothing in the repository reads file content at an arbitrary commit today:
`GitWorktreeManager` (Stage 7) is exclusively worktree lifecycle management
(create/reset/integrate/remove), never content retrieval, and no `rg` wrapper
exists anywhere.

## Decisions

### Context manifest

`ContextManifestCompiler.Compile` (`Forge.Compiler`) builds one
`ContextManifest` per sprint from a `ForgeDocumentSet` (ADR 0009), the
sprint's own already-frozen `SprintDefinition.Id`/`BaseCommit`/`Workflow`/
`WorkflowVersion` (Stage 6), and a caller-supplied token budget — no new
source of workflow identity or source commit is invented; both are read
straight from state Stage 6 already froze at sprint creation.

The manifest mirrors the four-layer model exactly:

- `rules` — every `ForgeDocumentKind.Rule` document (layer 1);
- `sprint_specifications` — always empty in the MVP (layer 2: no producer
  exists yet, per ADR 0009's own precedent for this exact gap);
- `knowledge` — every `ForgeDocumentKind.Knowledge` document whose optional
  `status` is absent or `accepted` (layer 3's project-knowledge/ADR half; see
  "Accepted ADRs" below);
- `handoffs` — always empty in the MVP (layer 3's other half: `Handoff` has a
  schema and domain shape but no producer, same gap ADR 0009 already named);
- `query_results` — populated only when the caller attaches a
  `ContextResultBundle` (layer 4; see "Declarative context-query plan"
  below) — a manifest built without one simply carries none.

Items admit in a fixed deterministic order — rules first, then knowledge,
each ordered by `RelativePath` with `StringComparer.Ordinal` (matching
`IntegrationSourceCompiler`'s existing ordering rule) — against the token
budget: an item that fits in the remaining budget is admitted; one that does
not is recorded in `truncated` with its estimated token count and skipped,
and the walk continues so a later, smaller item can still be admitted. This
is deliberately simple bin-packing, not an optimal knapsack solve: identical
input always produces an identical admitted/truncated split, which is the
only property reproducibility needs.

`ManifestDigest` is `sha256:` over a canonical, pipe-joined string of every
field that determines the manifest's content — sprint id, source commit,
workflow/workflow version, token budget, and each admitted item's relative
path and content digest, in the same fixed order used to build the manifest
— never a timestamp or a generator version, the same reproducibility rule
`IntegrationSourceCompiler.Digest` already established for ADR 0010's source
digest. Rebuilding a manifest from the same `ForgeDocumentSet`, sprint
state, and token budget always yields the same digest; nothing about it
depends on having kept a transcript of a prior build.

### Accepted ADRs

ADR 0009 already unifies "project knowledge" and "accepted ADRs" under one
`.forge/knowledge/*.md` source and one `ForgeDocumentKind.Knowledge` — it
does not introduce a second directory or a frontmatter `kind` override (kind
stays directory-derived, never a frontmatter field, per ADR 0009's own
rule). This ADR extends that source rather than building a parallel one:
`forge-document.schema.json` gains one new optional property, `status`
(`accepted | proposed | rejected | superseded`). The addition is backward
compatible — no existing document declares it, `required` is unchanged, and
an absent `status` still validates — so no `schema_version` bump is needed
(the same "additive minor" latitude ADR 0009 reserved for `scope`).

A knowledge document with no `status` (ordinary project knowledge, which has
no notion of acceptance) or an explicit `status: accepted` (an ADR that has
been accepted) is admitted to the manifest's `knowledge` layer.
`proposed`/`rejected`/`superseded` are parsed and validated exactly like any
other document but excluded from the manifest — a project can author and
review an ADR-shaped knowledge document before it ever reaches model
context. `ForgeDocument.Status` carries the parsed value (`null` when
absent); `ForgeDocumentCompiler` performs no filtering of its own — status
is a manifest-layer concern, not a parse-time rejection, so a
`forge integration skill generate` or any other full-set consumer still sees
every valid document regardless of status.

### Declarative context-query plan

A model proposes a `ContextQueryPlan` (`context-query-plan.schema.json`):
one pinned `source_commit` (a canonical 40- or 64-hex-character object id,
the same pattern `GitWorktreeManager.CommitPattern` already enforces) and 1
to 20 `operations`, each one of:

- `git_show` — read one file's content at `source_commit` (`path`, a
  forward-slash relative path with no `..`, drive, or backslash segment,
  identically shaped to ADR 0009's `references` path rule but validated
  against the project root rather than `.forge/`);
- `git_grep` — search line matches for `pattern` at `source_commit`, bounded
  to an optional `path_scope` subtree.

Every operation declares `max_result_bytes` (1-65,536, MVP default 4,096
when omitted — the same "schema-bounded ceiling plus a small code default"
shape ADR 0009 used for `context_limit_tokens`).

`git_grep` was chosen over spawning an external `rg` process. `rg` has no
native way to search a specific historical commit without first
materializing it into a real working tree — which would mean creating a
worktree Forge does not otherwise need, coordinating it against concurrent
sprint worktrees (Stage 7), and adding a new external binary dependency
Forge does not otherwise require. `git grep <commit> -- <path>` reads
directly from Git's own content-addressed object store, needs no working
tree, and reuses the exact `IProcessRunner`-driven, argument-array (never a
shell string) invocation style `GitWorktreeManager` already established.
Both `git_show` and `git_grep` are therefore pinned to one immutable
commit's object graph — Git's content addressing guarantees that replaying
the identical plan against the identical commit always returns identical
bytes, which is what makes the plan (not the content) the thing worth
recording durably.

`GitContextReader.ValidateAsync` (`Forge.Infrastructure`) checks the whole
plan before executing anything: schema conformance, operation-id uniqueness,
path/pattern/path-scope safety (same containment rules as ADR 0009's
reference check, rooted at the project root instead of `.forge/`), and that
every operation's required capability
(`context.git_show`/`context.git_grep`, `Forge.Domain.ContextCapabilityIds`
— a distinct namespace from the Host-protocol `Forge.Presentation
.CapabilityIds`) is present in the caller-supplied execution-profile
capability allowlist (`execution-profile.schema.json`'s
`capability_allowlist`). One invalid or unauthorized operation fails the
entire plan — nothing executes, and no partial result can imply a request
was partially honored. Validation and execution never throw for an expected
content problem (a missing path, a commit `git show` cannot resolve, a
binary blob); each is a per-operation diagnostic in the result instead.

`GitContextReader.ExecuteAsync` runs each validated operation from the
project root (not an attempt worktree — every worktree shares the same
object database, so no worktree needs to exist for a read pinned to a
commit) and truncates output at `max_result_bytes`, recording `Truncated` and
a `ContentDigest` (`sha256:` over the exact bytes admitted, truncated or
not) per operation. The result — a `ContextResultBundle`
(`context-result-bundle.schema.json`) — records `PlanDigest`,
`source_commit`, and each operation's `content_digest`/`byte_count`/
`truncated`, deliberately never the raw content itself: the same
digest-only, content-addressed shape `handoff.schema.json`'s `artifacts`
already uses. Durable state only ever needs to keep the plan and the source
commit; because both `git_show` and `git_grep` are pure functions of an
immutable commit, replaying the recorded plan against the recorded commit
regenerates byte-identical content on demand — proving "rebuild without a
transcript" by construction rather than by storing a transcript to rebuild
from.

## Consequences

Every layer the four-layer model requires now has either a real MVP source
(rules, knowledge/accepted-ADRs, bounded Git/file/`rg` lookup) or an
explicitly empty, schema-shaped placeholder (sprint specifications/decisions,
handoffs) that Stage 11 fills without a manifest schema redesign. Choosing
`git grep` over a separate `rg` dependency keeps every layer-4 read on one
already-required binary, pinned to one immutable commit, with no worktree
materialization and no new OS/path/lock coordination. Recording only plan
and digests (never raw content) in the durable, schema-versioned contract
keeps the manifest small and lets content regenerate on demand from Git's
own content-addressed store.

| Action | Recovery |
|---|---|
| build a `ContextManifest` | a document rejected by ADR 0009's parser is simply absent from the manifest, not a manifest-build failure |
| admit manifest items over budget | later items are recorded as `truncated` with their token count; earlier admitted items are unaffected |
| validate a `ContextQueryPlan` | any invalid or unauthorized operation rejects the whole plan before any Git process runs |
| execute a valid `ContextQueryPlan` operation | a missing path, unresolvable commit, or binary blob is a per-operation diagnostic; other operations in the same plan still execute |
