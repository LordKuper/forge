# ADR 0007: Cross-platform core and minimal OS adapters

- Status: Accepted
- Date: 2026-08-12
- Contract version: 1.1.0

## Context

Forge ships a Windows-only MVP, but release scope must not make reusable code
Windows-specific. The current Stage 1 composition predates this rule:
`Forge.Cli` targets Windows and references `Forge.Updater.Windows`, Windows
directory-flush interop lives in `Forge.Runtime`, and portable Desktop state is
not yet separated from its WinUI host. These are migration debt, not precedents.

Forge needs one enforceable boundary that preserves a portable Host, CLI/TUI,
workflow engine, and contracts while allowing the smallest necessary native
integration for installation, Desktop hosting, notifications, secret storage,
and process containment.

## Decisions

### Cross-platform is the default

Every project is neutral unless it is explicitly declared as an OS adapter.
Neutral projects target a portable .NET TFM and must build and run on Windows,
Linux, and macOS. They may not reference an OS-specific TFM, adapter project,
native library, P/Invoke, registry, service manager, shell command, path
convention, conditional-compilation branch, or platform-only package.

Forge uses a cross-platform BCL API when it provides the required semantics.
Portable OS/architecture detection is allowed only to report capabilities or
select a registered adapter. It cannot hide OS behavior inside a neutral project.
Vendor-specific provider and Git adapters remain neutral when their process and
data contracts work unchanged on all supported operating systems.

### OS adapters are explicit leaf modules

An OS adapter is a dedicated project with the OS in its name, an OS-specific
target when required, and a machine-readable `ForgeOsAdapter` project marker.
It implements a port defined by the neutral consumer and may depend inward on
neutral contracts. Neutral projects and one OS adapter never reference another
OS adapter.

An adapter may validate an OS-call boundary, translate a neutral request, invoke
the OS API, and normalize its result. It does not own domain rules, workflow,
policy, retries, durable state, protocols, presentation state, or reusable
algorithms. A platform-specific executable or native UI host is itself an
adapter composition root: it selects adapters and delegates immediately to a
neutral application or presentation model.

The target split is:

| Cross-platform code | Minimal OS-adapter code |
|---|---|
| Domain, application, contracts, Host, local protocol, client SDK | Native executable/UI bootstrap |
| CLI/TUI commands and Desktop presentation model | WinUI/AppKit/native UI host |
| Workflow, review, routing, persistence, Git and provider protocols | Installer activation, PATH, shortcuts, service/autostart |
| Update policy, verification, staging and rollback orchestration | OS activation primitive and restart handoff |
| Notification policy and durable attention events | OS notification delivery |
| BCL process supervision and file durability | Native containment or durability call only if BCL tests fail |

No adapter or placeholder is created for an OS capability Forge does not yet
ship. Shared behavior discovered in two adapters moves inward to neutral code;
it does not create an adapter framework.

### The boundary is mechanically enforced

Architecture checks enumerate projects and require every OS-specific TFM or
adapter reference to be inside a project marked `ForgeOsAdapter`. Neutral
projects must not reference marked projects. .NET platform-compatibility
analysis remains enabled with warnings as errors, and repository checks reject
native imports outside marked adapters.

Neutral projects and their contract tests run on Windows, Linux, and macOS. An
adapter and its tests run on the declared target OS. A skipped neutral test is
not evidence of portability. Release support may remain narrower than this build
matrix; unsupported distributions fail through capability selection, not by
embedding OS assumptions in the core.

### Existing coupling is migrated before the Stage 8 gate

- `Forge.Cli` becomes portable; Windows install/update composition moves to a
  thin Windows bootstrap adapter.
- Windows directory-flush interop in `Forge.Runtime`'s `DirectoryFlusher` moves
  behind its existing neutral durability behavior. The cross-platform BCL path
  remains the default.
- Reusable Desktop client, state, and presentation logic moves to a neutral
  project; the current WinUI executable becomes a Windows adapter.
- `Forge.Updater.Windows` remains an OS adapter but is audited so update policy
  and orchestration stay in `Forge.Updater`.
- Shared tests are separated from Windows adapter tests and join the three-OS CI
  matrix.

No new platform coupling may be added while this debt is being removed.

## Consequences

- Windows remains the only MVP distribution and Desktop host, while Host,
  CLI/TUI, workflow, contracts, and reusable presentation logic remain portable.
- OS support grows by adding thin leaf composition and native calls, not by
  forking workflows or application logic.
- Platform behavior becomes explicit and testable; adapter size and dependencies
  are visible during review.
- Forge adds no portability framework or dependency.
