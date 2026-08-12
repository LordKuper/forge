# ADR 0001: Stage 0 MVP foundation

- Status: Accepted
- Date: 2026-07-27
- Revised: 2026-08-12
- Contract version: 1.0.0

## Context

Forge needs stable boundaries before runtime code can safely implement updates,
provider execution, durable workflows, or two presentation surfaces. The
machine-readable contracts under `docs/contracts/v1/` are normative. This ADR
records the decisions and recovery guarantees behind them.

## Decisions

### MVP boundary

- The MVP supports a per-user Windows installation and distribution only. This
  release boundary does not permit platform-specific reusable code; ADR 0007
  requires a cross-platform core and minimal leaf OS adapters.
- `implementation-critical` is the only enabled workflow.
- The updater core is platform-neutral. Only `WindowsUpdateStrategy` is
  registered by the Windows composition adapter; any other target returns
  `platform_not_supported` before network
  or filesystem mutation.
- `<project-root>/.forge/` is the sole canonical project-configuration tree.
  User configuration lives at `%LOCALAPPDATA%\Forge\config.json`.
- Linux and macOS distributions and native adapters, package-manager
  distribution, machine-wide installation, lightweight workflows, distributed
  workers, SaaS, and enterprise policy are deferred.

### Release and update trust

- The official release identity is `github.com/LordKuper/forge`.
- Only the latest published, non-draft, non-prerelease SemVer release is
  eligible. Downgrades and equal-version activation are forbidden.
- Every asset must match the release checksum manifest by name, size, and SHA-256.
- Verification, staging self-test, activation, one-use restart token, startup
  handshake, and rollback are mandatory. Any failure is fail-closed for project
  and sprint work.
- The Windows layout is
  `%LOCALAPPDATA%\Forge\versions\<semver>\`, with an immutable version directory,
  a stable shim, atomically replaced `current.json`, and one retained previous
  version. Recovery restores the previous pointer and records the failed
  version. Project content cannot change release origin, channel, or trust roots.
- Conditional HTTP requests are allowed; a stale TTL is never an offline bypass.

The platform-neutral target is the immutable tuple
`UpdateTarget(OsFamily, Architecture, Packaging)` where normalized values are
lower-case machine identifiers such as `windows`, `x64`, and `portable_bundle`.
Unknown values are preserved for diagnostics but never matched by fallback.
The core depends on this contract:

```csharp
public interface IPlatformUpdateStrategy
{
    bool Supports(UpdateTarget target);
    ValueTask<StageResult> StageAsync(VerifiedRelease release, UpdateTarget target, CancellationToken cancellationToken);
    ValueTask<ActivationResult> ActivateAsync(StagedRelease staged, RestartContext restart, CancellationToken cancellationToken);
    ValueTask<RollbackResult> RollbackAsync(ActivationReceipt receipt, CancellationToken cancellationToken);
}
```

The resolver requires exactly one matching strategy before release lookup or
mutation. Zero matches return `platform_not_supported`; multiple matches are an
invalid composition and return `internal_error`.

### Independent sprints

- Each sprint owns an immutable input snapshot, base commit, branch, state
  namespace, artifacts, and write-attempt worktrees.
- Cross-sprint input is legal only through an explicit dependency on an
  immutable commit or content-addressed published artifact.
- Mutable branches, handoffs, outputs, worktrees, or database state of a
  non-terminal sprint are forbidden inputs.
- Finalization rechecks the base and dependency identities. A mismatch blocks
  integration and requires explicit rebase or replanning.

### Trust boundaries and credentials

Forge trusts its shipped code and pinned policy, not project text. GitHub release
metadata remains untrusted; release checksums protect against accidental corruption.
Provider CLI output is untrusted typed input. Project files, generated artifacts, prompts,
hooks, and tool instructions are untrusted content and never become executable
policy without validation and authorization.

Credentials remain in provider-managed authentication or OS secret storage.
Forge never accepts secrets in project/user config, command arguments, prompts,
artifacts, or logs; never dumps the full environment; never copies subscription
tokens between providers; and never asks an LLM to redact secrets. The
presentation-independent redaction pipeline runs before persistence or display.

### Presentation parity and orchestration

CLI/TUI and .NET MAUI Desktop are equal clients over immutable commands, one
project snapshot, and typed events. ADR 0005 refines process ownership: a headless Forge Host
owns orchestration and mutation, and both presentations use its versioned local
protocol. Presentation projects may format, navigate, and collect input, but may
not call providers, Git, event stores, filesystem mutation, or updater
implementations, define business orchestration, or define separate permission
rules. A public capability is releasable only when every field in
`capabilities.json` is implemented and its shared acceptance test passes on both
surfaces.

Desktop uses one client process per Windows user and may own multiple project windows.
Recent projects and per-project navigation intent are user-scope conveniences,
not workflow state. Restart restores a view by querying durable application state;
it never resumes work from serialized UI objects. A second launch activates the
existing process and forwards only a validated navigation intent.

Mutations require validation, authorization, confirmation according to safety
class, an idempotency key, and expected state version. Suggested actions dispatch
the same commands and cannot bypass those controls.

### Status and deterministic guidance

`ProjectSnapshot` is the immutable, versioned read model. Summary, next-action,
tree, sprint-detail, startup, and integration-status views are projections of
that DTO, not separate application queries. An active sprint is the
explicit surface selection or the sole non-terminal sprint. With multiple
non-terminal sprints, Forge selects none.

Recommendations use this priority order:

1. update or provider recovery that blocks safe startup;
2. `awaiting_human`;
3. `blocked`;
4. `failed` but recoverable;
5. ready-to-finalize;
6. resumable/runnable;
7. create the first sprint;
8. optional inspection.

Ties use safety class, sprint creation sequence, stable sprint ID, then action
ID. Every recommendation includes rationale, preconditions, target, safety class,
and expected state version. A changed state returns `suggestion_stale` with no
side effect.

### Localization and scoped configuration

English is the default and ultimate UI/interaction fallback; Russian is built in.
One catalog serves both presentation surfaces. Commands, flags, properties,
identifiers, codes, telemetry, hashes, Git/provider raw output, and durable state
are culture invariant.

User scope owns `language.ui`, `language.interaction`, `language.llm`, and
interaction preferences. Project scope owns artifact languages. Key spaces do
not overlap. A wrong-scope key returns `configuration_scope_violation`.

Resolution is `session override -> user value -> inherited/default value`.
`language.interaction` inherits `language.ui`; `language.llm` inherits
`language.interaction`; ultimate fallback is `en`. Session overrides never
persist and cannot change project artifact policy. Stores have independent
schema versions/migrations and use write-temp, flush, atomic replace, and
recover-previous semantics.

Generators declare exactly one audience: `user_facing`, `agent_facing`, or
`machine`. There is no implicit `mixed`. User- and agent-facing language comes
from the project snapshot and is stored in artifact metadata. Machine artifacts
are invariant. A missing artifact-language capability blocks generation; it does
not silently fall back.

Language packs use normalized BCP 47 tags, stable keys, typed named placeholders,
and explicit plural/select variants. Placeholder compatibility with the complete
English catalog is mandatory.

## Technology choices

- .NET 10; .NET MAUI with WinUI 3 for the minimal Windows Desktop host, with
  reusable presentation state kept neutral under ADR 0007.
- `System.CommandLine` for CLI parsing; Terminal.Gui for the optional full-screen
  TUI adapter.
- `Microsoft.Extensions.DependencyInjection` for composition.
- `System.Text.Json` plus JsonSchema.Net for Draft 2020-12 validation.
- YamlDotNet only at the project YAML adapter boundary; canonical state and
  external machine contracts remain JSON-compatible.
- Shared RESX-backed localization catalog with ICU-style plural/select semantics
  implemented behind `ILocalizationCatalog`.

Dependencies are pinned centrally when Stage 1 creates the solution. Substitution
is allowed only through a superseding ADR that preserves these contracts.

## Consequences

Network or update uncertainty can block project work by design. Two presentation
implementations and strict schemas add early cost, but prevent permission drift,
unsafe recovery, localized persisted state, and hidden cross-sprint coupling.

All mutating actions define recovery:

| Action | Recovery |
|---|---|
| install/update Forge | restore previous `current.json`; preserve failed version for diagnosis |
| initialize `.forge/` | delete staging tree; never overwrite an unknown existing tree |
| update provider | provider strategy rollback or explicit repair; sprint work remains blocked |
| write config | atomic replace from validated temp file; restore previous file |
| run/cancel/rebase sprint | durable event replay; isolated worktree remains inspectable |
| approve/finalize | optimistic concurrency rejects stale state; Git integration retains pre-operation refs |
