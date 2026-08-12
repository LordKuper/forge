# ADR 0002: Provider toolchain management

- Status: Accepted (revised 2026-08-12)
- Date: 2026-08-04
- Contract version: 1.1.0

## Context

Stage 5 requires Forge to discover, version-check, install, update, and recheck
the official Codex CLI and Claude Code CLI on Windows, and to execute them
without shell-string concatenation. D-005 required revalidating both vendors'
current Windows installation and update mechanisms immediately before
implementation, since earlier planning predated verification.

Forge follows each vendor's supported installation and execution contract rather
than maintaining a parallel provider distribution channel.

## Research findings (2026-08-04)

- Claude Code CLI's recommended Windows install is the native installer:
  PowerShell `irm https://claude.ai/install.ps1 | iex`. Reading the script
  (`downloads.claude.ai/claude-code-releases`) confirms it downloads a
  version- and platform-matched `claude.exe`, verifies it against a
  `manifest.json` SHA-256 checksum, then runs `<binary> install` to finish
  setup at a fixed path: `%USERPROFILE%\.local\bin\claude.exe`. The script
  takes no interactive input. The documented manual update command is
  `claude update`. Native Windows support (no WSL requirement) began at major
  version 2.
- Codex CLI's recommended Windows install is the native installer: PowerShell
  `irm https://chatgpt.com/codex/install.ps1 | iex`. Reading the script
  confirms the resolved, stable command path is
  `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` (a junction the
  installer keeps pointed at its current release under
  `%USERPROFILE%\.codex\packages\standalone\`). The script prompts
  interactively only for edge cases (existing conflicting installs); setting
  `CODEX_NON_INTERACTIVE=1` makes every prompt answer "no" instead of
  blocking. No separate update subcommand is documented; the script itself
  compares the installed version and is safe to rerun.
- Claude Code's non-interactive JSON output (`claude -p ... --output-format
  json|stream-json`) requires `--verbose` whenever `stream-json` is used, or
  the CLI rejects the flag combination. Documented top-level `stream-json`
  event types are `system`, `stream_event`, `assistant`, `user`, and `result`
  — there is no top-level `tool_use` type; tool calls appear as `tool_use`
  content blocks nested inside an `assistant` message's `content` array.
- Codex's non-interactive JSON output (`codex exec --json`) documents top-level
  event types `thread.started`, `turn.started`, `turn.completed`,
  `turn.failed`, and an `item.*` family; exact `item.*` subtype strings are
  not published.

ADR 0006 refines provider execution for unattended workflow attempts. It does
not change discovery, installation, update, or version verification.

## Decision

Forge runs each vendor's own recommended Windows install/update mechanism and
never re-implements release verification itself:

- Discovery reads the fixed, vendor-documented install path directly
  (`%USERPROFILE%\.local\bin\claude.exe` for Claude Code,
  `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` for Codex) and runs
  `--version`, bounded by a 15-second timeout so a hung probe cannot block
  every startup pass. It never touches the network.
- Installing a missing provider runs the fully-qualified in-box Windows
  PowerShell (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`,
  never a bare `powershell.exe` — a bare name is resolved through
  `CreateProcess`'s search order, which checks the calling image's own
  directory and the current directory before `System32`) with
  `-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "<vendor
  install script URL>"` — a fixed, Forge-controlled literal, never built from
  variable/untrusted input. Updating an already-installed Claude Code runs
  `claude.exe update` directly (the documented lighter path); updating Codex
  reruns the same install script, which the vendor designed to be idempotent.
  Both are bounded by a 10-minute timeout.
- Forge trusts the vendor's own download and checksum verification (Claude's
  script verifies against Anthropic's `manifest.json`; Codex's script
  verifies against its own release digests) the same way any user running the
  vendor-recommended command would. Forge does not additionally re-verify the
  installed binary.
- Discovery and execution always invoke the resolved absolute executable path
  directly through `IProcessRunner`'s argument list — never a PATH-resolved
  shim and never `cmd.exe`. Stage 11 execution sends the prompt through
  redirected standard input, never an argument, environment variable, or log,
  as required by ADR 0006. This also removes command-line length and option
  injection as prompt concerns.
- Project configuration cannot override provider location or version; only
  the fixed vendor-documented path is consulted.
- The `Providers` startup check performs discovery only; it never mutates
  state or calls the network, so every startup pass (`doctor`, `status`,
  `next`, `config show`, ...) stays fast and offline-safe. `GetProviderHealth`
  (`forge models`) is read-only for the same reason, matching its declared
  `query`/`read` capability contract (`docs/contracts/v1/capabilities.json`).
  Installing or updating is the separate, explicit `forge models --refresh`
  action (`RefreshProviderHealthAsync`) — the same query-plus-explicit-mutation
  shape `forge doctor --recover` already uses. Installing a well-known
  developer CLI is not a project or user-data mutation, so `--refresh` does
  not require confirmation, matching Forge's own self-update.

## Execution and output normalization

Non-interactive invocation follows each vendor's headless mode and reads the
prompt from redirected standard input:

- Codex: `codex exec --json` with no positional prompt. Classification uses only the
  documented top-level prefixes: `turn.*` maps to `Result`, `item.*` maps to
  `ToolUse`, everything else (including `thread.*`) is `Unknown`. Text
  extraction is intentionally a no-op for Codex: `item.*` subtype field names
  are not published, so guessing them risks silently wrong data.
- Claude Code: `claude --print --output-format stream-json --verbose`.
  `assistant` maps to `Message` and `result` maps to `Result`; `system` and
  `user` map to `Unknown`. Text is read from the `text`-typed blocks in
  `message.content[]`, skipping any `tool_use` blocks in the same array.

Stdout and stderr are read concurrently with the bounded incremental parser,
minimal child environment, deadlines, and process-tree cleanup defined in ADR
0006. Every output line must parse as a JSON object; a line that does not is a
`malformed_output` failure for the whole run rather than a silently dropped line.
A non-zero exit classifies into a stable `ProviderFailureKind`
(`authentication`, `rate_limited`, `quota_exceeded`, `policy`, `transient`,
`unknown`) by a best-effort keyword match over the process's own error text,
since neither vendor publishes an exhaustive error-code table; an unmatched
error is `unknown`, never a guessed specific category. All error detail is
redacted before it leaves the adapter.

## Consequences

Forge depends on each vendor's install-script infrastructure being reachable
and stable, and on that script continuing to place the binary at today's
documented path — a vendor path change silently reintroduces `missing` until
Forge is updated to match. In exchange, Forge tracks whatever the vendor
considers current without maintaining its own release-verification and
version-retention logic, and stays aligned with the update cadence and trust
model each vendor already recommends to every other user of their CLI.
Recovery is vendor-owned: a failed install/update leaves whatever the vendor
script left behind and reports `provider_update_failed`; sprint work stays
blocked until both providers reach `ready`.

## Open decisions

D-005 is resolved by this ADR.
