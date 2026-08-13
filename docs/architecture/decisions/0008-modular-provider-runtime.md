# ADR 0008: Modular provider runtime and selection

- Status: Accepted
- Date: 2026-08-13
- Contract version: 1.2.0

## Context

The Stage 5 proof embeds Codex and Claude Code lifecycle and execution code in
`Forge.Runtime`, registers both providers unconditionally, treats both as
required, and exposes no update-availability or authentication gate. This makes
the core depend on concrete vendors, prevents a user from selecting only one
provider, and reruns vendor update mechanisms without a separate Forge-owned
availability decision.

Forge needs a provider-neutral runtime while retaining each vendor's supported
Windows paths, scripts, commands, authentication, update channel, environment,
and output normalization in exactly one owned integration. Provider selection
must remain personal because installation and credentials are per user, while a
project may constrain routing without enabling software the user disabled.

Review benefits from a provider/model lineage distinct from implementation, but
requiring that distinction would make a valid single-provider configuration
unusable. Fresh review context, bounded coverage, and deterministic review gates
remain mandatory; provider/model independence is a routing preference.

## Decisions

### The core knows only `ILlmProvider`

`Forge.Runtime` owns the provider-neutral `ProviderId`, lifecycle, update,
authentication, execution, event, failure, and aggregate status contracts. It
also owns generic selection, startup, retry, cache, and routing policy. The core
contains no provider enum, concrete provider identifier, vendor command, URL,
path, output parser, environment variable, or DI registration.

One `ILlmProvider` represents one complete integration: local discovery,
update-availability lookup, install/update, authentication status, and bounded
execution. `ProviderToolchainManager` consumes `IEnumerable<ILlmProvider>` and
never resolves a concrete implementation.

The Windows MVP supplies two marked provider-owned OS adapters:

- `Forge.Providers.Codex.Windows`, namespace `Forge.Providers.Codex`, owns all
  Codex paths, install/update scripts and endpoints, commands, authentication,
  environment, and output normalization;
- `Forge.Providers.Claude.Windows`, namespace `Forge.Providers.Claude`, owns the
  equivalent Claude Code behavior.

The projects do not reference each other. There is no shared
`Forge.Providers.Windows` project. Each adapter translates the neutral contract
to one vendor's Windows CLI and normalizes its result; workflow and routing
policy remain in the core. A future OS adds a provider-owned adapter such as
`Forge.Providers.Codex.Linux` rather than an OS switch in neutral code.

### Composition is explicit and enablement is configuration

Windows composition roots call `AddCodexProvider()` and `AddClaudeProvider()`.
Both integration assemblies ship with Forge, but registration has no install,
update, authentication, or process side effect. Forge uses no reflection,
assembly scan, dynamic loader, factory catalog, or provider plugin protocol for
the MVP.

At runtime a provider catalog indexes registrations by `ProviderId` and rejects
duplicates. User configuration adds the ordered, user-scoped
`providers.enabled` list:

- omission selects all registered built-in providers in composition order for
  backward compatibility;
- an explicit array is the exact enabled set and fallback priority;
- `[]` permits diagnostics and configuration but blocks model work;
- duplicates or an identifier with no registration invalidate configuration.

For example, `["claude_code"]` selects Claude Code only. A disabled provider is
listed as disabled without probing it and is never discovered, installed,
updated, authenticated, or executed. Its integration assembly remaining in the
Forge bundle does not install its external CLI.

A project execution profile may narrow and reorder providers but cannot enable
one outside the user list. Routing candidates are the ordered intersection of
the frozen project profile and the user-enabled set; when no project constraint
exists, the user order is used. An empty intersection blocks execution with a
stable diagnostic rather than silently selecting another provider. The resolved
candidate list is frozen into the sprint profile.

### Startup performs conditional maintenance and authentication

Only enabled providers participate in startup. Forge always performs a bounded
local executable/version probe. For a usable provider, a small per-user cache
limits the network update-availability check to once per 24 hours; a failed
check or update is retried after one hour. `forge models --refresh` bypasses the
time limit but still checks availability before invoking an updater.

Codex reads the vendor release metadata used by its own updater; Claude Code
reads the selected vendor channel metadata. Forge compares parsed versions and
runs the existing vendor-owned updater only when the remote version is newer.
A missing, corrupt, or unsupported provider is an install/repair case and does
not require an update comparison first. Update work is protected by a per-user
interprocess lock and followed by another local version probe. A release-check
failure does not block an otherwise usable installed version. An update failure
blocks only when the installed provider is no longer usable.

Normal Claude Code execution sets `DISABLE_AUTOUPDATER=1` so Forge owns cadence;
the variable is not set for an explicit update. Forge does not set
`DISABLE_UPDATES`.

After maintenance, Forge checks authentication on the final executable at every
startup: `codex login status` for Codex and
`claude auth status --json` for Claude Code. The commands run with a 15-second
deadline from a Forge-owned probe directory. Forge keeps only the normalized
state and stable diagnostic; it never persists or logs raw status output,
identity fields, authentication method, or credential material. Authentication
is never initiated automatically.

Every enabled provider must report local authentication readiness. Missing
authentication blocks model work with `provider_authentication_required`; a
probe failure uses `provider_authentication_check_failed`. These commands prove
provider-reported local readiness, not server acceptance. A later live
authentication failure is not retried as transient and excludes that route for
the attempt; routing may continue to the next configured provider.

### Reviewer lineage independence is best-effort

Review always uses a fresh attempt, clean context, the frozen review profile,
and the same mandatory scope, rubric, coverage, convergence, and deterministic
finalization rules. Forge first scans the configured priority order for an
available reviewer whose provider/model lineage differs from the implementation
lineage. If none exists, it selects the first available reviewer in normal
priority order, including the same provider and model family.

The verdict records whether provider/model independence was achieved. Reduced
lineage independence is diagnostic metadata, not a human gate and not a reason
to weaken coverage or severity rules. A single-provider user may therefore run
the complete workflow. No available review model at all still blocks review;
the best-effort rule does not make review optional.

### Surfaces expose the same normalized state

The project snapshot, CLI, and Desktop distinguish registered, enabled,
disabled, missing, current, update-available, authentication-required, and
ready states without exposing provider output or identity. `forge models` is
read-only; disabled providers are not probed. `forge models --refresh` mutates
only enabled providers. Configuration uses the existing scoped write path; no
separate enable/disable command model is introduced.

Before implementation, the versioned user-configuration, provider-health,
startup-check, snapshot, diagnostic, and execution-profile contracts must add
these fields and compatibility cases.

## Consequences

- A user can run Forge with Claude Code only, Codex only, both in explicit
  priority order, or neither for diagnostic/configuration use.
- Adding a provider changes a composition root and adds one self-contained
  adapter; it does not modify provider enums or vendor switches in the core.
- Routine startup performs cheap local and authentication probes while network
  update checks are throttled and updater execution is conditional.
- Review prefers stronger provider/model separation without making the workflow
  depend on multiple subscriptions or installations.
- The integration assemblies remain Windows-specific until another OS adapter
  is implemented; provider-neutral orchestration remains portable.
- ADR 0002 retains the vendor mechanisms, ADR 0006 retains supervision and
  convergence, and ADR 0007 retains the OS boundary; this ADR revises their
  provider composition, startup maintenance, and reviewer-lineage selection
  details where they conflict.
