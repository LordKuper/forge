# ADR 0009: `.forge/` canonical document format

- Status: Accepted
- Date: 2026-08-17
- Contract version: 1.0.0

## Context

Stage 9 (P9.1-P9.8) needs a parser that turns authored `.forge/` content into
validated semantic input before any provider-native view can be generated
(P9.9-P9.16) or any context can be assembled (Stage 10). Stage 0 froze
`.forge/` as the sole canonical project-configuration tree and pinned
YamlDotNet at the project YAML adapter boundary (ADR 0001), but never defined
the format of authored project knowledge. `docs/architecture/overview.md`'s
"Context assembly" section already commits to progressive-disclosure layers
that this ADR must be able to source content for:

1. always-on rules and workflow contracts;
2. sprint-scoped specifications and decisions;
3. project knowledge, accepted ADRs, and structured handoffs;
4. exact Git, file, and `rg` lookup under a recorded token budget.

Only layer 1's rules and layer 3's project knowledge/ADRs have MVP acceptance
cases today. Sprint-scoped specifications/decisions (layer 2) and structured
handoffs (layer 3) are produced by Stage 11's not-yet-built attempt/planning
executor and are out of scope here (`docs/plans/implementation-plan.md`
P11.1-P11.12); this ADR's directory layout and scope model must not block
that later addition.

## Decisions

### Directory layout

Authored canonical documents live under two flat, optional directories,
sibling to the existing `manifest.yaml` and `workflows/`:

- `.forge/rules/*.md` — always-on rules (context-assembly layer 1);
- `.forge/knowledge/*.md` — project knowledge and accepted ADRs
  (context-assembly layer 3).

Neither directory is created by `ProjectInitializer`; both are optional and a
project with zero documents is valid. Subdirectories are not scanned in the
MVP (`SearchOption.TopDirectoryOnly`) — nesting is deferred until a project
demonstrates a real organizational need. Sprint-scoped directories
(context-assembly layer 2) are deferred to Stage 11, which first produces
sprint specifications/decisions to store there.

### Document format

Every document is UTF-8 Markdown with a YAML frontmatter block delimited by a
`---` line at byte 0 and a second `---` line, matching the format used by
static-site generators (Jekyll/Hugo/Obsidian) rather than inventing a new
convention:

```markdown
---
schema_version: "1.0.0"
id: testing-invariant
title: Implementation-first testing invariant
scope: project
references:
  - knowledge/adr-0006-review-convergence.md
context_limit_tokens: 1200
---

Markdown body content, admitted to model context verbatim...
```

The frontmatter is deserialized with a bare YamlDotNet `Deserializer`
(matching `YamlConfigurationStore`'s pattern: normalize to
`Dictionary<string, object?>`, round-trip through `JsonSerializer` to a
`JsonElement`, validate against `forge-document.schema.json` with
JsonSchema.Net Draft 2020-12, `RequireFormatValidation = true`). The Markdown
body is stored and admitted to context verbatim; the MVP compiler does not
build a CommonMark AST. Heading-based chunking or link-graph extraction from
the body is deferred to P9.9-P9.16 if generation ever measures a need for it
— the MVP requires no new Markdown-parsing dependency.

`forge-document.schema.json` requires `schema_version` (const `"1.0.0"`),
`id` (a stable DNS-label-shaped slug, unique per document set), `title`, and
`scope`. `references` and `context_limit_tokens` are optional.

### Kind

A document's kind (`rule` or `knowledge`) is derived from which directory
contains it, never declared in frontmatter. This removes an entire class of
"declared kind contradicts actual location" validation from the format:
moving a file changes its kind, with no separate field to keep in sync.

### Scope

`scope` is a required frontmatter field so the schema can validate it, even
though the MVP directories only ever produce `scope: project` today (the
schema's `scope` enum currently allows only `"project"`). Modeling `scope` up
front means Stage 11 can add `"sprint"` and a `.forge/sprints/{id}/...`
document root as an additive minor contract version, without redesigning the
parser or its output shape.

### Safe paths and references

A `references` entry is a forward-slash relative path from `.forge/` to
another canonical document (e.g. `knowledge/adr-0006.md`), validated before
the referencing document is admitted:

- rejected outright if it is empty, contains a `..` segment, starts with `/`,
  contains a backslash, or contains a Windows drive/UNC prefix;
- resolved via `Path.GetFullPath` against the `.forge/` root and rejected if
  the result does not fall strictly inside that root (path containment,
  compared with `OrdinalIgnoreCase` on Windows and `Ordinal` elsewhere — the
  same OS-capability-only branch already used for named-mutex session scoping
  in Stage 8, not a new adapter boundary);
- if the resolved path is a reparse point/symlink, its final target
  (`FileInfo.ResolveLinkTarget(returnFinalTarget: true)`) is resolved and
  containment is re-checked against that target, so a symlink cannot smuggle
  a reference outside `.forge/`;
- rejected unless it names a document the compiler itself discovered under
  `rules/` or `knowledge/` in the same parse pass — a reference is a link
  between two canonical documents, not an arbitrary file pointer into the
  project tree. General file/`rg` access for a model is Stage 10's separate,
  explicitly bounded declarative context-query plan, not this mechanism.

An unsafe or unresolved reference fails only the referencing document, as a
typed parse error — it never throws and never blocks unrelated documents in
the same parse pass.

### Context limits

Every document has an effective token budget: the frontmatter's
`context_limit_tokens` when present, otherwise an MVP-wide default of 4,000
tokens; both are bounded by a hard schema ceiling of 8,000 tokens (`minimum:
1, maximum: 8000`) so no single document can consume Stage 10's future
sprint-level token budget outright. Token count is estimated with the
standard MVP heuristic of one token per four UTF-8 characters of the
Markdown body — precise enough to bound cost without adding a
provider-specific tokenizer dependency, and revisited only if Stage 10
measurements show it under- or over-counts materially. A document exceeding
its effective limit is a typed parse error on that document alone, not a
silent truncation — truncation with a recorded rationale is Stage 10's
context-assembly concern, not this compiler's.

### Result shape

Parsing a `.forge/` tree never throws for expected content problems (missing
directories, malformed frontmatter, schema violations, unsafe references,
oversized documents, duplicate ids). It returns a `ForgeDocumentSet`: the
list of successfully validated documents plus a list of per-file typed
errors, so one malformed document degrades gracefully instead of blocking
every other rule and knowledge entry in the project — the same
per-item-error-collection posture `StartupPipeline` already uses for its
bounded checks.

## Consequences

Two new flat, optional directories and one new schema are enough to satisfy
today's only two context-assembly layers with MVP acceptance cases. Deferring
kind-as-a-field, subdirectory nesting, Markdown AST parsing, and sprint scope
avoids building structure nothing consumes yet, at the cost of a schema
change when Stage 11 needs sprint-scoped documents. `references` staying
closed to the compiler's own document set (rather than arbitrary project
paths) keeps the safe-path surface small and auditable; Stage 10's
declarative context-query plan remains the only path to broader, explicitly
bounded file access.

| Action | Recovery |
|---|---|
| parse `.forge/rules/` or `.forge/knowledge/` | malformed/unsafe/oversized documents are per-file typed errors; valid documents in the same directory are unaffected |
