# ADR 0053: Capability negotiation enforcement

- Status: Accepted
- Date: 2026-08-24
- Contract version: capabilities.json 1.10.0

## Context

Plan section 9.2 requires that "capability negotiation prevents an older Host or Desktop from
silently attempting an unsupported operation." `ControlPlaneHostedService.HandshakeAsync` already
advertises the Host's real, current capability set (`CapabilityIds.Implemented`) in every handshake
response. Nothing on the client side read it: `ForgeHostClient` received `response.Capabilities`
during the handshake and discarded it, so a client newer than the Host it talked to would still send
a request for a capability the Host does not implement. The Host's dispatch `switch` failed closed
(its `default` arm returns `ControlDiagnosticCode.Malformed`, "Unknown request kind"), so nothing
broke -- but the diagnostic was generic and gave the client no chance to degrade gracefully before
even trying.

## Decisions

### The gate lives in `RemoteForgeMutations`, not `ForgeHostClient`

`ForgeHostClient` (`Forge.Host.Client`) is deliberately a dependency-free leaf: no project reference,
so it cannot know about `CapabilityIds` (`Forge.Presentation`, part of the `Forge.Runtime` assembly,
which depends on `Forge.Host.Client` -- not the reverse). Moving the capability→id mapping into
`Forge.Host.Client` would mean duplicating every capability id as a bare string there, with nothing
to stop it drifting from `CapabilityIds` itself. Instead:

- `ForgeHostClient` gains one new fact it is qualified to know without any domain knowledge: its own
  `HostCapabilities` property, the raw string list the just-completed handshake echoed back. It is
  set on every successful handshake and reset to empty on every disconnect, so a caller can never read
  a stale set left over from a previous, possibly different, Host.
- `RemoteForgeMutations.SendAsync` -- already the single low-level funnel every typed mutation method
  (`ConfirmNodeAsync`, `ResolveGateAsync`, `MoveSprintToStageAsync`, ...) routes through before a
  `ControlRequest` is built -- is where the gate actually runs. It already sits in the one assembly
  that can see both `ForgeHostClient` (the wire client) and `CapabilityIds` (the domain model), so no
  new project reference and no inversion of the existing leaf/composition-root layering was needed.

### The `Kind` -> `CapabilityIds` table is hand-written, not loaded from `capabilities.json`

`docs/contracts/v1/capabilities.json` is a docs artifact today, not wired into any runtime code path.
Loading it at runtime to resolve which capability governs a given `ControlRequest.Kind` would need a
new file-read + JSON-parse on every client construction (or process-wide caching of it), a decision
about how to ship/locate the file next to every composition root (CLI, Desktop, and any future
client), and a fallback for a missing or corrupt file -- a materially larger and riskier change than
this fix needs, for a table with 14 entries that changes only when a new mutation kind ships. The
table (`RemoteForgeMutations.CapabilityByKind`) stays a small, hand-written
`Dictionary<string, string>` instead, built directly from `ControlProtocol`'s own `...Kind` constants
and `CapabilityIds`' own constants -- so a rename of either breaks the build, not just the mapping.

The risk a hand-written table carries -- silent drift as new capabilities and kinds are added -- is
closed by `CapabilityNegotiationMappingTests` (`tests/Forge.Tests/Acceptance`), which loads
`capabilities.json` the same way `SurfaceParityTests` already does and asserts, in both directions:

- every id the table maps to actually exists in the contract file (catches a typo or a stale id);
- every capability already in `CapabilityIds.Implemented` that has a matching `ControlProtocol` kind
  is present in the table's values, with `project.initialize` and `provider.health` named as the only
  exemption (neither has a `ControlRequest.Kind` at all: initialization happens before a Host can
  exist to dispatch anything (ADR 0005), and provider health is always answered from local state,
  never sent over this wire) -- so a future implemented capability with a real dispatch kind that
  forgets to update the table fails this test, not silently ships unenforced negotiation;
- no capability still reserved in `capabilities.json` (not yet in `CapabilityIds.Implemented`) is
  present in the table at all.

### Only capabilities already in `CapabilityIds.Implemented` are gated

`capabilities.json` documents several capabilities (`workspace.summary`, `sprint.timeline`,
`workspace.available_actions`, `workflow.stop_operation`, `workflow.assess_stage_transition`,
`sprint.move_stage`) as already implemented on Host, CLI, and/or Desktop, with their own
`ControlProtocol` kind already dispatched by every Host in this codebase -- but each is deliberately
still absent from `CapabilityIds.Implemented` for an unrelated, purely administrative reason
(ADR 0049/0050/0051: promoting the constant also requires widening `SurfaceParityTests`'s own fixed
capability dictionaries, tracked as separable cleanup). `StopCurrentOperationAsync` and
`MoveSprintToStageAsync` are already called from real CLI (`forge attempt stop`, `forge sprint
move-stage`) and Desktop (`SprintActionsViewModel`) code paths today. Gating either of them against
`CapabilityIds.Implemented` would reject a request the connected Host actually serves, purely because
that list has not caught up -- a real regression, not a safety improvement. The gate table therefore
covers exactly the 12 `CapabilityIds.Implemented` entries that have a matching `ControlProtocol` kind
(`SetConfigurationKind`, `InstallIntegrationKind`/`RemoveIntegrationKind`, `ResolveGateKind`,
`SupersedeAttemptKind`, `ConfirmNodeKind`, `RecordTestWorkKind`, `FinalizeSprintKind`,
`CreateSprintKind`/`RunSprintKind`/`ResumeSprintKind`/`CancelSprintKind`, plus
`GetProjectSnapshotKind`/`ReadControlEventsKind` for completeness even though no client code sends
either over this wire today -- both queries are always answered from local state). `PingKind` and
`RecoverStartupKind` are excluded because no `capabilities.json` id governs either.

In practice this makes the fix exactly as narrow as it needs to be: every capability this gate can
reject today is already required on both surfaces to exist at all, so the only way to actually observe
`CapabilityNotSupported` is a client newer than the Host it is talking to -- the hypothetical this ADR
exists to protect against, not a change in behavior for the common case of a Host and client that
shipped together.

### A distinct diagnostic code, collapsed like every other wire failure, with one new localized message

`ControlDiagnosticCode.CapabilityNotSupported` is a new wire-level code, returned only by the client
(never by the Host: anything the Host actually dispatches has, by definition, the capability). At the
`RemoteForgeMutations` boundary, every non-`None` wire diagnostic already collapsed to one typed-result
field via a fixed `DiagnosticCodes.HostUnavailable` literal, matching this codebase's established
"callers never need to distinguish infra failure reasons" pattern. `DiagnosticCodeFor` keeps that
collapse for every other code, but gives `CapabilityNotSupported` its own
`DiagnosticCodes.CapabilityNotSupported` value instead, since -- unlike an unreachable Host -- it is
the one outcome the codebase should show a genuinely actionable explanation for, not just a bare code.

Rendering follows each surface's existing mechanism, unchanged in shape:

- **CLI**: `Report`/`WriteDiagnostic` already write only the raw machine-readable `diagnostic_code`
  string to the dedicated `diagnostics` stream (distinct from `output`, which carries human/JSON
  content) for every diagnostic, including the existing `DiagnosticCodes.HostUnavailable` -- no
  diagnostic code gets bespoke localized prose through this path today. `capability_not_supported`
  needs no new rendering path: it already prints there like every other code, staying script-friendly.
- **Desktop**: rendering is not actually a single point -- `MainPageViewModel` has no
  configuration-write path at all (removed by ADR 0050), so the one live Desktop caller of the gated
  `configuration.manage` capability is `ProjectSettingsViewModel.SaveAsync`
  (`WorkspaceShellPage.ProjectSettings.cs`), which renders through the same generic
  `Message(text, diagnosticCode)` helper `WorkspaceShellPage.xaml.cs` and
  `WorkspaceShellPage.SprintWorkspace.cs` already use for every other save/action failure --
  `"{message} ({diagnosticCode})"` for every code, including `CapabilityNotSupported`. Only the
  `MainPageViewModel` copy of this helper (an instance method there, used by the mutations that do
  flow through it -- e.g. `ConfirmNodeAsync`, `ResolveGateAsync`) gains the one `switch` arm this
  ADR originally described: instead of the generic suffix, `CapabilityNotSupported` resolves the new
  `MessageKeys.CapabilityNotSupported` localized sentence ("This Host does not yet support this
  operation. Upgrade Forge on this project's Host to use it." / Russian translation). The project
  settings save path currently shows the raw code suffix, not that sentence -- giving it the same
  localized branch is tracked as follow-up, not required for this fix's narrow client/Host-mismatch
  scope. The message is deliberately generic (not naming the specific operation): the typed result
  carries only a diagnostic code, no free-text detail field, and adding one to every result type
  across the whole surface to parametrize one rare message would be disproportionate to what this fix
  needs.

## What stays deferred

- Promoting `workspace.summary`/`sprint.timeline`/`workspace.available_actions`/
  `workflow.stop_operation`/`workflow.assess_stage_transition`/`sprint.move_stage` to
  `CapabilityIds.Implemented` (and, once promoted, adding them to `CapabilityByKind`) -- separable
  cleanup, per ADR 0049/0050/0051; out of scope here and would only add gating for kinds that already
  work end-to-end today.
- Loading `capabilities.json` at runtime for negotiation -- deliberately rejected above in favor of a
  hand-written, drift-tested table.
- A per-operation localized message (naming exactly which capability/command is unsupported) -- the
  single generic sentence is judged sufficient for a hypothetical-today, defensive diagnostic; nothing
  currently observes this diagnostic in production.

## Consequences

- `Forge.Host.Client`: `ControlDiagnosticCode` gains `CapabilityNotSupported`; `ForgeHostClient` gains
  the `HostCapabilities` property (set on every successful handshake, cleared on every disconnect).
- `Forge.Runtime`: `RemoteForgeMutations` gains the `CapabilityByKind` gate table (`internal`, for
  direct test access), the capability check inside its own `SendAsync`, and `DiagnosticCodeFor`;
  `DiagnosticCodes` gains `CapabilityNotSupported`; `MessageKeys`/`Messages.resx`/`Messages.ru.resx`
  gain `CapabilityNotSupported`; `MainPageViewModel.Message` becomes an instance method with one new
  `switch` arm.
- `tests/Forge.Tests` gains `RemoteForgeMutationsCapabilityGateTests` (proves a gated request is
  rejected client-side without reaching the wire when the capability is absent, and proceeds normally
  when present) and `CapabilityNegotiationMappingTests` (the `capabilities.json` drift net described
  above).
- `VERSION` moves to `0.70.0` (MINOR: new client-visible behavior -- a clean rejection instead of a
  generic Host-side error -- for the narrow case of a client newer than its Host).

## References

- Plan section 9.2 ("Capability negotiation prevents an older Host or Desktop from silently
  attempting an unsupported operation.")
- `docs/contracts/v1/capabilities.json` (the contract this ADR's drift test cross-checks against)
- ADR 0005 (Host/client mutation-routing boundary `RemoteForgeMutations` implements)
- ADR 0049/0050/0051 (the reserved-capability precedent this ADR's gating scope follows)
- AGENTS.md Portability section (the leaf/dependency-direction reasoning behind putting the gate in
  `RemoteForgeMutations` rather than `ForgeHostClient`)
