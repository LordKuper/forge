# ADR 0011: Provider integration install and removal

- Status: Accepted
- Date: 2026-08-17
- Contract version: 1.0.0

## Context

Stage 9 (P9.17-P9.24) needs to "generate, inspect, install, and remove one
canonical Forge integration for each enabled provider." ADR 0010 already
builds the generation engine — `Forge.Application.IntegrationGenerationService`
produces in-memory `GeneratedArtifact`s (`CLAUDE.md`/`AGENTS.md` content with
a content-addressed `SourceDigest` and an ownership marker) — but nothing
writes them to disk, and there is no CLI command. `docs/contracts/v1/capabilities.json`'s
`integration.skill` capability already names the CLI shape:
`forge integration skill <generate|install|remove>`, contract
`GenerateInstallAgentIntegration`, permission `integration_write_confirm`.

ADR 0005 adds the constraint ADR 0010 deferred here: "unknown user-owned
files are never overwritten, and duplicate installations are detected rather
than left ambiguous." It also names a broader documentation scope this
generation must communicate: "the project snapshot, events, commands,
addressing, status reporting, recovery, and built-in workflow invariants,
including implementation confirmation before new test selection or
authoring" — the model reads this in the generated file; Forge Host enforces
the actual invariant.

Two things this item does *not* build, and says so explicitly rather than
silently omitting them:

- **`AgentIntegrationChanged` events.** Every existing durable event lives in
  a per-sprint journal (`.forge/sprints/{id}/events.jsonl`); there is no
  project-level (non-sprint) event stream to record a project-scoped action
  like installing an integration file into. Building one is a real subsystem,
  not a natural extension of this item's scope, and nothing downstream reads
  such an event yet. Deferred until a project-level event store exists to
  receive it.
- **Project-snapshot integration status.** `implementation-plan.md`'s P8.25-33
  entry already deferred "integration status, phase profile, and active
  deadline" to Stage 11 P11.56-66, "which only this stage's executor first
  produces" — there is still no executor to make that status meaningful, so
  this item does not add it either.

## Decisions

### Three CLI verbs, matching the existing contract exactly

- `forge integration skill generate` — **read-only**. Computes every enabled
  provider's artifact (via `IntegrationGenerationService`) and, for each one,
  inspects the target file at the project root: missing, current (Forge-owned,
  digest matches), stale (Forge-owned, digest differs — regenerating would
  change it), or foreign (exists, not Forge-owned). Nothing is written. This
  is both "generate" and "inspect" from the plan's own wording — a preview
  naturally reports what generation would produce and what installing it
  would change, so no separate `inspect`/`status` verb is needed beyond what
  `capabilities.json` already names.
- `forge integration skill install [--yes]` — **write**. Re-runs the same
  inspection, then for each artifact: missing or stale → write it; current →
  no-op (idempotent, matching ADR 0005); foreign → refuse and report it
  distinctly, never touching the file. A foreign file is exactly ADR 0005's
  "duplicate installation" case — an unrelated or hand-written file already
  occupying `CLAUDE.md`/`AGENTS.md` — detected, not silently overwritten or
  silently ignored.
- `forge integration skill remove [--yes]` — **write**. Missing → no-op;
  Forge-owned (current or stale) → delete; foreign → refuse, for the same
  reason: a file this command didn't write is never a file it deletes.

### Ownership is decided by the marker, not by best-effort heuristics

`IntegrationSourceCompiler.Marker` already embeds `source_digest=sha256:...`
as the first line of every generated file (ADR 0010). Whether an existing
file is Forge-owned is decided by parsing that exact prefix back out — a
paired `IntegrationSourceCompiler.TryParseSourceDigest(content, out digest)`
sharing the same `MarkerPrefix` constant the writer uses, so the two can
never drift apart the way `ClaudeIntegrationGenerator`'s hand-rolled marker
copy briefly did during ADR 0010's own review. A file without a
recognizable marker is foreign, full stop — there is no secondary heuristic
(file size, a content snippet, a "looks like ours" guess) that could turn a
foreign file into a false-Current/Stale read.

### The install/remove target path is a trusted constant, not untrusted input

ADR 0009's safe-path/symlink-containment machinery exists because a
`references` entry in a `.forge/` document is untrusted project content. A
`GeneratedArtifact.RelativePath` is nothing like that: it is a fixed string
literal (`"CLAUDE.md"`, `"AGENTS.md"`) returned by exactly two first-party
`IProviderIntegrationGenerator` implementations this same build ships,
never read from a file, a request payload, or any other external source.
Reusing ADR 0009's full containment/symlink-resolution logic here would
defend against an input class that cannot occur. Instead,
`IntegrationInstallationService` rejects any `RelativePath` containing a
path separator (`/` or `\`) as an internal error before combining it with
the project root — a correctness assertion on first-party code, not a
security boundary — after which a plain `Path.Combine` can only ever name a
direct child of the project root.

### Confirmation follows `RecoverStartupAsync`, not `InitializeProjectAsync`

`project.initialize` carries `Confirmed`/`ExpectedStateVersion`/`IdempotencyKey`
because a stale initialize request could race a `ProjectSnapshot` the CLI
already rendered. Integration state is not projected into `ProjectSnapshot`
(by design, see Context) and is not offered as a `SuggestedAction`, so there
is nothing for a request to go stale against — every call re-derives
install/remove state fresh from the current `.forge/` content and the
current target files, exactly like `RecoverStartupAsync` re-derives startup
state fresh on every call. `InstallIntegrationAsync`/`RemoveIntegrationAsync`
therefore take only `(string? projectRoot, bool confirmed)`, honoring the
same `interaction.confirm_destructive` gate `RecoverStartupAsync` and
`InitializeProjectAsync` already do.

### Write path reuses the existing atomic single-file writer

`Forge.Configuration.AtomicConfigurationFile.WriteAsync` (temp file, flush,
`File.Replace`/`File.Move`, directory flush) is already a generic
arbitrary-path atomic writer, not `.forge/`-specific. `IntegrationInstallationService`
calls it directly for `CLAUDE.md`/`AGENTS.md` with `retainPrevious: false` —
these are regenerable, Forge-owned files, not precious user data, so no
`.previous` sidecar is left in the project root.

### Routing follows the established Host-mutation pattern exactly

`generate` is a plain read — like `GetStartupStatusAsync`, it runs directly
against the caller's local `ForgeApplication`, no Host round-trip. `install`
and `remove` are mutations and join `IForgeMutations`
(`InstallIntegrationAsync`, `RemoveIntegrationAsync`), routed through
`RemoteForgeMutations` → `ControlProtocol.InstallIntegrationKind`/
`RemoveIntegrationKind` → `ControlPlaneHostedService.DispatchInstallIntegrationAsync`/
`DispatchRemoveIntegrationAsync` → `ForgeApplication`, mirroring
`RecoverStartupAsync`'s existing wire shape field-for-field (a single
`Confirmed` boolean request, a typed result response).

## Consequences

Three CLI verbs and one new Host-routed write pair are enough to satisfy
`capabilities.json`'s existing contract without inventing new wire concepts.
The explicit non-goals (events, snapshot status) keep this item from growing
into building a project-level event store or Stage 11's execution profile
ahead of need. `AgentIntegrationChanged` and integration-status snapshot
fields remain open work items, not silent gaps.

| Action | Recovery |
|---|---|
| generate (inspect) | pure read; a language-capability failure returns a typed diagnostic, nothing is written |
| install | a foreign file is refused per-artifact, reported, and left untouched; other enabled providers' artifacts still install |
| remove | a foreign file is refused the same way; a missing file is a no-op |
