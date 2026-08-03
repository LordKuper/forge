# Forge

Forge is a local harness for durable, isolated AI-assisted software delivery.

The MVP targets Windows and exposes equivalent CLI/TUI and .NET MAUI Desktop
surfaces over a shared application contract. The Stage 1 skeleton contains the
shared domain, application, presentation, localization, configuration, updater,
provider, infrastructure, and bootstrap projects plus both hosts.

- [Accepted architecture decisions](docs/architecture/decisions/0001-stage-0-foundation.md)
- [Versioned contracts](docs/contracts/v1/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [Complete original research and system design (Russian source)](ai-agentic-software-development-workflow-ru.md)
- [Implementation plan](implementation-plan.md)

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

Restore uses committed NuGet lock files. The validation script formats, builds,
tests, and checks dependencies for known vulnerabilities:

```powershell
pwsh ./.github/scripts/test-stage1.ps1
```

Validate only the Stage 0 contract gate with:

```powershell
pwsh ./tests/contracts/Stage0.Contracts.Tests.ps1
```
