# ADR 0002: Provider toolchain management

- Status: Accepted
- Date: 2026-08-04
- Contract version: 1.0.0

## Context

Stage 5 requires Forge to discover, version-check, install, update, and recheck
the official Codex CLI and Claude Code CLI on Windows, and to execute them
without shell-string concatenation. D-005 required revalidating both vendors'
current Windows installation and update mechanisms immediately before
implementation, since earlier planning predated verification.

## Research findings (2026-08-04)

- Claude Code CLI ships from `github.com/anthropics/claude-code`. Each GitHub
  release publishes `claude-win32-x64.zip`, `claude-win32-arm64.zip`,
  `SHASUMS256.txt`, and a detached `SHASUMS256.txt.sig`. The native Windows
  installer (`irm https://claude.ai/install.ps1 | iex`) places a genuine
  `claude.exe` at a fixed path (`%USERPROFILE%\.local\bin\claude.exe`, per the
  documented uninstall steps). npm installation is documented but explicitly
  deprecated in favor of the native path. Native Windows support (no WSL
  requirement) began at major version 2.
- Codex CLI ships from `github.com/openai/codex`. Each GitHub release publishes
  `codex-x86_64-pc-windows-msvc.exe`, `codex-aarch64-pc-windows-msvc.exe`, and
  a `codex-package_SHA256SUMS` manifest. The native Windows installer
  (`irm https://chatgpt.com/codex/install.ps1 | iex`) exists, but its install
  target directory is not documented; npm (`@openai/codex`) and the raw GitHub
  release binary are the only installation targets with a confirmed, fixed
  location.
- Both vendors therefore publish the same trust primitives Forge already uses
  for its own release (Stage 2): a versioned GitHub release, a checksum
  manifest, and platform-specific binary assets.

## Decision

Forge manages both provider CLIs as **Forge-owned verified installations**,
independent of any system-wide install the user may already have:

- Releases are queried from each vendor's GitHub `releases/latest` endpoint
  (which already excludes drafts and prereleases), never a mutable branch.
- The matching Windows asset is selected by exact name for the detected
  architecture (`x64`/`arm64`) and its SHA-256 is verified against the
  GitHub Releases API's own per-asset `digest` field before use — not either
  vendor's separately published checksum manifest, which is scoped
  inconsistently (Codex's `codex-package_SHA256SUMS` covers only its combined
  `.tar.gz` bundles, not the standalone `.exe` Forge installs). A missing
  digest fails closed rather than installing unverified. A `.zip` asset is
  extracted; a raw `.exe` asset is used directly.
- Verified binaries stage into an immutable
  `%LOCALAPPDATA%\Forge\providers\<provider>\versions\<version>\` directory;
  an atomically replaced `current` pointer selects the active version, and one
  previous version is retained, mirroring the Windows self-update layout in
  ADR 0001.
- Discovery and execution always invoke the Forge-owned binary directly
  (`ProcessRunner` with an argument list), never a PATH-resolved shim and never
  `cmd.exe`. This removes both PATH ambiguity (system installs, npm shims) and
  the shell-metacharacter risk of routing provider arguments — including
  future user-authored prompt text — through a command interpreter.
- Project configuration cannot override provider location, version, or
  executable path; only user-scope and Forge-internal state select the active
  provider binary.
- The `Providers` startup check performs discovery only (read the `current`
  pointer, run `--version`); it never mutates state or calls the network, so
  every startup pass (`doctor`, `status`, `next`, `config show`, ...) stays
  fast and offline-safe. `GetProviderHealth` (`forge models`) is the
  explicit, on-demand operation that reconciles: discover, and for any
  provider not `ready`, install or update, then recheck. Installing a
  well-known developer CLI into Forge's own directory is not a project or
  user-data mutation, so it follows the same unconfirmed, automatic pattern as
  Forge's own self-update rather than requiring interactive confirmation.

## Execution and output normalization

Provider adapters invoke the resolved, Forge-owned executable directly through
`IProcessRunner` (a real `.exe`, never a PATH-resolved shim or `cmd.exe`), so
prompt text passed as a single argument-list entry can never be reinterpreted
as a shell operator. Non-interactive invocation follows each vendor's
documented headless mode:

- Codex: `codex exec --json <prompt>`. Documented event `type` values are
  `thread.started`, `turn.started`, `turn.completed`, `turn.failed`, and an
  `item.*` family (exact item subtypes are not published, so classification
  stays at the `item.` prefix rather than guessing subtype names).
- Claude Code: `claude -p <prompt> --output-format stream-json`. Documented
  event `type` values are `assistant`, `tool_use`, and `result`. `assistant`
  events wrap an Anthropic Messages API object, so text is read from
  `message.content[].text`.

Every output line must parse as a JSON object; a line that does not is a
`malformed_output` failure for the whole run rather than a silently dropped
line. A non-zero exit classifies into a stable `ProviderFailureKind`
(`authentication`, `rate_limited`, `quota_exceeded`, `policy`, `transient`,
`unknown`) by a best-effort keyword match over the process's own error text,
since neither vendor publishes an exhaustive error-code table; an unmatched
error is `unknown`, never a guessed specific category. All error detail is
redacted before it leaves the adapter.

## Consequences

Forge does not depend on Node.js/npm, WinGet, Homebrew, or either vendor's
install script, and is not affected by a user's separate system-wide install
of either CLI. The tradeoff is that Forge maintains its own copy per provider
(disk cost, one retained previous version each) instead of reusing an
existing installation. Recovery mirrors self-update: a failed install/update
leaves the previous verified version in place and reports
`provider_update_failed`; sprint work stays blocked until both providers
reach `ready`.

## Open decisions

D-005 is resolved by this ADR.
