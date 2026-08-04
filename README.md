# Forge

Forge is a local harness for durable, isolated AI-assisted software delivery.

The MVP targets Windows and exposes equivalent CLI/TUI and .NET MAUI Desktop
surfaces over a shared application contract. The Stage 1 skeleton contains the
shared domain, application, presentation, localization, configuration, updater,
provider, infrastructure, and bootstrap projects plus both hosts.

- [Accepted architecture decisions](docs/architecture/decisions/0001-stage-0-foundation.md)
- [Versioned contracts](docs/contracts/v1/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [AI-assisted software delivery research](docs/architecture/ai-agentic-software-development-workflow.md)
- [Implementation plan](docs/plans/implementation-plan.md)

## Commands

Every command runs the same ordered startup sequence. Unresolved checks keep
sprint work fail-closed; a failed check leaves recovery as the only safe action.

| Command | Purpose |
|---|---|
| `forge doctor [--startup] [--recover --yes]` | Show the startup summary, the ordered checks with `--startup`, or quarantine unreadable configuration with `--recover`. |
| `forge init --project-root <absolute-path> [--yes]` | Display the absolute root and initialize `.forge/` after confirmation. |
| `forge status [--json]` | Show the project status snapshot; `--json` emits the versioned machine contract. |
| `forge next [--json]` | Show the deterministic recommended actions. |
| `forge config <show\|user\|project>` | Read scoped configuration with provenance, or write one key. |

`--project-root` accepts only an absolute directory and is never resolved
upward. Values passed to `forge config` follow the declared type of the key, so
boolean and numeric keys keep their type and string keys keep the raw text.
Machine output goes to standard output, diagnostics go to standard error, and
exit codes follow [the contract table](docs/contracts/v1/README.md).

## Prerequisites

- .NET SDK 10.0.302, pinned by `global.json`.
- The .NET MAUI Windows workload:

```powershell
dotnet workload install maui-windows --skip-manifest-update
```

- Visual Studio builds require the **MSVC x64/x86 build tools** component
  (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`) in the same Visual
  Studio instance. The IDE does not reuse this component from a separate Build
  Tools installation.

## Build and test

Restore uses committed NuGet lock files. Run the validation script before
creating a pull request; locally it applies .NET formatting, then builds,
tests, and checks dependencies for known vulnerabilities. CI verifies that the
formatting made no changes and blocks warnings and errors:

```powershell
pwsh ./.github/scripts/test-stage1.ps1
```

Validate only the Stage 0 contract gate with:

```powershell
pwsh ./tests/Forge.Tests/Contracts/Stage0.Contracts.Tests.ps1
```
