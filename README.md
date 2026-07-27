# Forge

Forge is a local harness for durable, isolated AI-assisted software delivery.

The MVP targets Windows and exposes equivalent CLI/TUI and .NET MAUI Desktop
surfaces over a shared application contract. Stage 0 defines the contracts before
runtime implementation:

- [Accepted architecture decisions](docs/architecture/decisions/0001-stage-0-foundation.md)
- [Versioned contracts](docs/contracts/v1/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [Complete original research and system design (Russian source)](ai-agentic-software-development-workflow-ru.md)
- [Implementation plan](implementation-plan.md)

Validate the Stage 0 gate with:

```powershell
pwsh ./tests/contracts/Stage0.Contracts.Tests.ps1
```
