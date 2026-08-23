# Forge

Forge is a local harness for durable, isolated AI-assisted software delivery.

The MVP targets Windows and exposes equivalent CLI and .NET MAUI Desktop
surfaces over a shared application contract. Both hosts run the same ordered
startup sequence and dispatch the same versioned commands, queries, and status
snapshot. Self-update, project initialization, scoped configuration,
deterministic recommendations, the provider toolchain, and the durable
implementation-critical workflow engine (planning, implementation, review,
confirmation, test-work, and finalization, with human gates, stage transitions,
and a stop-current-operation control) are all implemented.

The Desktop surface is a two-panel workspace: a sidebar listing known projects
and their active sprints with a global provider/quota status row, a project
overview, Forge/project settings pages, and a sprint workspace with a sticky
status header, a chronological timeline, and typed contextual actions (run,
resume, cancel, stop the active operation, move to another workflow stage).
It renders the same versioned queries and mutations the CLI uses, never its own
copy of workflow policy.

- [Accepted architecture decisions](docs/architecture/decisions/0001-stage-0-foundation.md)
- [Versioned contracts](docs/contracts/v1/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [AI-assisted software delivery research](docs/architecture/ai-agentic-software-development-workflow.md)
- [Implementation plan](docs/plans/implementation-plan.md)

## Install

On Windows 10 version 2004 (build 19041) or later, using an x64 or Arm64
machine and PowerShell 5.1 or later, run:

```powershell
irm https://raw.githubusercontent.com/LordKuper/forge/main/install.ps1 | iex
```

The script requires no administrator privileges. It downloads the latest stable
release from GitHub, verifies the bundle against the release's published size
and SHA-256 checksum, then runs Forge's existing per-user installer. The script
and release are not code-signed; HTTPS and the GitHub repository are the trust
boundary for this initial distribution method.
Rerun the same command to update or repair an existing per-user installation.

## Commands

Every command runs the same ordered startup sequence. Unresolved checks keep
sprint work fail-closed; a failed check leaves recovery as the only safe action.

| Command | Purpose |
|---|---|
| `forge doctor [--startup] [--recover --yes] [--bundle]` | Show the startup summary, the ordered checks with `--startup`, quarantine unreadable configuration with `--recover`, or emit a redacted diagnostic bundle with `--bundle`. |
| `forge eval [--project-root <path>]` | Evaluate the updater, provider, bootstrap, and workflow subsystems plus the project model-policy gate; JSON, non-zero exit on a failed check. |
| `forge init --project-root <absolute-path> [--yes]` | Display the absolute root and initialize `.forge/` after confirmation. |
| `forge status [--detail summary\|full] [--sprint <id>] [--json]` | Show the project status snapshot; `--json` emits the versioned machine contract. |
| `forge tree [--sprint <id>] [--json]` | Show the sprint hierarchy, nesting each attempt under its owning node. |
| `forge sprint inspect <id> [--json]` | Show one sprint's full node/attempt/finding/routing detail. |
| `forge next [--json]` | Show the deterministic recommended actions. |
| `forge events [--after <cursor>] [--follow] [--json]` | Read incremental workflow events. |
| `forge models [--json] [--refresh]` | Show provider toolchain health. |
| `forge models quota [--json]` | Show provider/model account quota status (`unknown` unless a provider exposes a verified signal — see ADR 0052). |
| `forge config <show\|user\|project>` | Read scoped configuration with provenance, or write one key. |
| `forge project <add\|list\|remove\|relink\|alias\|select>` | Manage the local, user-scoped project catalog; never modifies a project's own repository or `.forge/` directory. |
| `forge workspace <summary\|actions> [--json]` | Show the bounded sidebar/status-header summary across every cataloged project, or the Host's own contextual-action list. |
| `forge sprint <create\|run\|resume\|cancel\|inspect\|timeline\|assess-stage\|move-stage>` | Manage one sprint's lifecycle and workflow stage; `assess-stage`/`move-stage` implement the read-then-commit stage-transition protocol (ADR 0045/0046/0048). |
| `forge attempt <supersede\|stop> <id> --sprint <id>` | Supersede a non-terminal attempt with a replacement, or stop the sprint's exact active operation without cancelling the sprint (ADR 0044/0047). |
| `forge gate \| forge confirm \| forge test-work \| forge finalize` | The human-only workflow gates: approve/reject a review gate, record implementation confirmation, record a test-work decision, and finalize a sprint. |
| `forge integration skill <generate\|install\|remove>` | Preview, write, or remove each enabled provider's native integration file. |

The Desktop workspace consumes the same versioned queries and mutations this
table lists; it never re-derives workflow policy locally (see
`docs/architecture/decisions/0043-workspace-read-model-projections.md` and the
subsequent Slice 4-7 ADRs for the read models and presentation architecture).

`--project-root` accepts only an absolute directory and is never resolved
upward. Values passed to `forge config` follow the declared type of the key, so
boolean and numeric keys keep their type and string keys keep the raw text.
Machine output goes to standard output, diagnostics go to standard error, and
exit codes follow [the contract table](docs/contracts/v1/README.md).

## Prerequisites

- .NET SDK 10.0.303, pinned by `global.json`.
- The .NET MAUI Windows workload:

```powershell
dotnet workload install maui-windows --skip-manifest-update
```

- Visual Studio builds require the **MSVC x64/x86 build tools** component
  (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`) in the same Visual
  Studio instance. The IDE does not reuse this component from a separate Build
  Tools installation.

## Build and test

Restore uses committed NuGet lock files. Run both validation scripts before
creating a pull request, `lint.ps1` first so that `test-stage1.ps1` then
builds and tests the already-formatted code:

```powershell
pwsh ./.github/scripts/lint.ps1
pwsh ./.github/scripts/test-stage1.ps1
```

`lint.ps1` applies .NET formatting and checks dependencies for known
vulnerabilities; locally it fixes formatting in place, while CI
(`$env:CI = 'true'`) verifies that formatting made no changes and fails
instead. Run `dotnet format Forge.slnx` directly to fix formatting without
the rest of `lint.ps1`. `test-stage1.ps1` builds and tests the solution.

Validate only the Stage 0 contract gate with:

```powershell
pwsh ./tests/Forge.Tests/Contracts/Stage0.Contracts.Tests.ps1
```
