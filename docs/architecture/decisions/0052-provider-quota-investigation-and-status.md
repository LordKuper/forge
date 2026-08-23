# ADR 0052: Provider quota investigation and `provider.quota_status`

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.10.0

## Context

`docs/plans/desktop-workspace-redesign.md` section 6.5 reserved `provider.quota_status`, distinct
from `provider.health` (ADR 0008's toolchain install/authentication readiness) and from a sprint's
own retry budget. ADR 0043 reserved the capability id without behavior; ADR 0049 deferred the actual
read model to Slice 7 with an explicit condition: "Add `ProviderQuotaSnapshot` only for providers
that expose verified quota data... Unknown quota is rendered as unknown, never inferred." This ADR
records that investigation's outcome and the implementation that follows from it.

## Decisions

### Neither provider integration in this codebase exposes a verified account/model quota signal

The investigation examined every surface Forge's two provider adapters
(`Forge.Providers.Claude.Windows`, `Forge.Providers.Codex.Windows`) already shell out to or parse:

- **Claude Code.** `ClaudeLlmProvider.CheckAuthenticationAsync` already documents that `claude auth
  status --json`'s "exact response schema is not published" and parses it defensively through a
  priority list of guessed boolean field names, falling back to `CheckFailed` for any unrecognized
  shape. `RunAsync`'s `--output-format stream-json` event stream (`type: "system"|"assistant"|"user"|"result"`)
  carries message content and a terminal result marker; no observed event type carries a
  remaining-quota, usage-limit, or reset-time field, and no such field is part of any published
  contract this adapter parses against.
- **Codex.** `CodexLlmProvider.CheckAuthenticationAsync` uses `codex login status`, documented to be
  "scriptable by exit code alone" -- no output body is even read. `RunAsync`'s `codex exec --json`
  event stream (`thread.*`, `turn.*`, `item.*`) is classified by `Classify` into `Result`/`ToolUse`/
  `Unknown` only; no event type carries quota data either, and the adapter's own remarks note item
  subtypes are "documented only in prose."
- **The one existing quota-shaped signal is inferred, not verified, and is not reused here.**
  `ProviderExecution.ClassifyFailure` best-effort keyword-matches a failed run's stderr text
  (`"quota"`, `"usage limit"`, `"billing"`) to produce `ProviderFailureKind.QuotaExceeded` --
  reachable in `ProviderDiagnosticCodes.QuotaExceeded`. This classifies a failure after the fact from
  unstructured text; it carries no remaining amount, unit, or reset time, and reusing it to
  synthesize a `ProviderQuotaSnapshot` value would be exactly the "inferred" quota plan 6.5 forbids.

Conclusion: `provider.quota_status` ships end-to-end (Host, CLI, Desktop) today, but every
`ProviderQuotaSnapshot` it produces reports `ProviderQuotaAvailability.Unknown` with no remaining
amount, unit, or reset time. This is the plan's own explicitly anticipated, legitimate outcome, not
an incomplete implementation -- fabricating a `Ready`/`Limited` reading to look more complete would
violate plan 6.5 directly.

### `ProviderQuotaSnapshot`/`ProviderQuotaProjector` mirror `ProviderHealthEntry`/`ProviderHealthProjector` exactly

`Forge.Runtime/Providers/ProviderQuota.cs` adds `ProviderQuotaAvailability` (`Unknown`, `Ready`,
`Limited`, `Unavailable`, `Stale` -- plan 11 item 2's five states), `ProviderQuotaSnapshot`
(provider id, model, availability, remaining amount, unit, reset time, observation time, diagnostic
code -- plan 6.5's exact field list), and `ProviderQuotaProjector.Project`, which walks the same
enabled-then-disabled provider union `ProviderHealthProjector.Project` already walks over
`ProviderToolchainStatus`/`ProviderCatalog`, purely and without any new probe (`ForgeApplication.GetProviderQuotaStatusAsync`
reuses the already-cheap `providerToolchain.CheckAsync`). Every entry currently resolves through one
`Unverified` helper, which is the only production code path today -- the four other enum members
exist so the projector's contract, the CLI row, and the Desktop status row are structurally complete
for every state the plan requires, not because any of them is reachable yet. A future adapter that
gains a real, verified quota API would extend `ProviderQuotaProjector` (or add a provider-specific
quota port, mirroring `IProviderReleaseSource`'s per-vendor-adapter shape) without changing this
contract's shape.

### `provider.quota_status` stays reserved, matching ADR 0049/0050/0051's own precedent

`capabilities.json` moves `provider.quota_status`'s `note` from "Reserved, not implemented on either
surface" to "Implemented on Host, CLI, and Desktop" (1.9.0 -> 1.10.0) -- `forge models quota
[--json]` and `SidebarViewModel`'s status row both ship in this slice. It is deliberately *not*
added to `CapabilityIds.Implemented`: ADR 0049 kept `workspace.summary`/`sprint.timeline`/
`workspace.available_actions` reserved even after shipping real CLI-and/or-Desktop surfaces, and ADR
0050/0051 repeated the same choice for `workflow.stop_operation`/`workflow.assess_stage_transition`/
`sprint.move_stage`, each time for the same reason: promoting the constant also requires widening
`SurfaceParityTests.DesktopControls`'s fixed dictionary and every other test keyed off
`CapabilityIds.Implemented`, which is real but separable cleanup that does not gate this
capability's own functional correctness. `SidebarViewModel` calls
`ForgeApplication.GetProviderQuotaStatusAsync` the same local, in-process way `GetWorkspaceSummaryAsync`/
`GetAvailableActionsAsync` are already called (ADR 0050's own reasoning: this is not
`ControlProtocol`-negotiated remote-Host traffic, so the reservation never blocked Desktop
consumption). A dedicated `SurfaceParityTests.ProviderQuotaStatusDocumentedCliOptionsMatchTheirActualRequiredness`
test closes the same CLI-option-requiredness gap ADR 0047/0048/0049 already closed for their own
reserved-but-shipped capabilities.

### Sidebar aggregation reports the single worst-case reading across every provider

Plan 4.1's bottom status row shows one quota line, not one per provider. `ProviderQuotaAggregation.Worst`
ranks `Unavailable > Limited > Stale > Unknown > Ready` and reports the most severe state present (or
`Unknown` for an empty provider list), so a degraded provider's quota can never be hidden behind an
otherwise-healthy or merely-unknown majority. `SurfaceFormatting.QuotaStatusSummary` resolves that
worst state to one of five localized (text, accessible-name) pairs -- satisfying plan 12.6's "text +
accessible name for every one, from the start" for all five states structurally, even though only the
`Unknown` pair is reachable in production today. `WorkspaceShellPage.xaml.cs`'s sidebar quota `Label`
previously carried no `SemanticProperties.SetDescription` call at all (a genuine plan 12.6 gap this
slice closes alongside the query wiring, not a pre-existing, deliberately-deferred item).

### Audit: no existing surface presents sprint retry budget as account quota

Plan 6.5's explicit anti-requirement ("sprint retry budget is never presented as account quota") was
audited across every surface that renders either concept. `SurfaceFormatting.SprintDetailLines`
renders `RoutingLabel retry_remaining={details.Routing.RetryRemaining}` under a `RoutingLabel`
heading distinct from any quota text; the CLI's `forge sprint inspect`/`forge status --detail full`
and the Desktop sprint workspace both render it through that one shared method, so neither surface
has its own competing label. No code path assigns `RoutingStatus.RetryRemaining` (or any routing/
retry-ledger value) to a `ProviderQuotaSnapshot` field, and `ProviderQuotaSnapshot` itself has no
retry/routing-shaped field to receive one. No change was needed to satisfy this requirement; it holds
by construction and this ADR records the audit rather than a fix.

## What stays deferred

- A real quota adapter for any provider: this requires a provider vendor to publish a structured,
  scriptable quota/usage API or CLI output, which neither does today. If one does in the future, it
  extends `ProviderQuotaProjector`/adds a provider-specific port without changing
  `ProviderQuotaSnapshot`'s shape.
- Promoting `provider.quota_status` (and the still-reserved `workspace.summary`/`sprint.timeline`/
  `workspace.available_actions`/`workflow.stop_operation`/`workflow.assess_stage_transition`/
  `sprint.move_stage`) to `CapabilityIds.Implemented` -- separable cleanup, per ADR 0049/0050/0051.
- Per-provider quota rows in the Desktop sidebar (only the aggregate worst-case is shown, matching
  plan 4.1's one-line status row); `forge models quota` already exposes the full per-provider detail
  for anyone who needs it.

## Consequences

- `Forge.Runtime` gains `Providers/ProviderQuota.cs` (`ProviderQuotaAvailability`,
  `ProviderQuotaSnapshot`, `ProviderQuotaStatus`, `ProviderQuotaProjector`, `ProviderQuotaAggregation`)
  and `ProviderDiagnosticCodes.QuotaUnknown`; `ForgeApplication` gains `GetProviderQuotaStatusAsync`;
  `StatusJson` gains a `ProviderQuotaStatus` overload; `SurfaceFormatting` gains `ProviderQuotaRow`/
  `QuotaStatusSummary`.
- `Forge.Host.Client`/`Forge.Host.Runtime` gain the `get_provider_quota_status` `ControlProtocol` kind,
  request record, and dispatch case, mirroring `get_workspace_summary`.
- `Forge.Cli` gains `forge models quota [--json]`.
- `Forge.Desktop.Presentation`'s `SidebarStatusRow` gains `QuotaAccessibleText`; `SidebarViewModel`
  calls the new query and both formatting helpers. `Forge.Desktop`'s sidebar quota label gains a
  `SemanticProperties.SetDescription` call it previously lacked.
- `Forge.Localization` gains eleven new Slice-7 message keys (English and Russian):
  `ModelsQuotaDescription`, `ModelsQuotaTitle`, `QuotaStatusUnknownAccessible`, and a (text,
  accessible) pair each for `Ready`/`Limited`/`Depleted`/`Stale`.
- `capabilities.json` moves from 1.9.0 to 1.10.0 (`provider.quota_status`'s `note` updated; still not
  in `CapabilityIds.Implemented`).
- `tests/Forge.Tests` gains `ProviderQuotaProjectorTests`, `ProviderQuotaAggregationTests`, two
  `ModelsQuotaCommand*` CLI acceptance tests, `SurfaceParityTests.ProviderQuotaStatusDocumentedCliOptionsMatchTheirActualRequiredness`,
  and a `SidebarViewModelTests` case proving the quota row resolves to the truthful "unknown" text
  and accessible name.
- `VERSION` moves to `0.69.0` (MINOR: new capability surface, no breaking contract change; this
  redesign has been additive throughout).

## References

- Plan sections 6.5, 11 (Slice 7), 12.6
- ADR 0008 (`provider.health`, `ILlmProvider`, the vendor-adapter boundary this investigation reused)
- ADR 0043/0049 (the five-projection reservation and CLI-first/reserved-capability precedent this ADR
  follows for `provider.quota_status`)
- ADR 0050 (local, in-process consumption reasoning `SidebarViewModel` reuses for this query)
- AGENTS.md Portability section (the OS-adapter boundary this investigation's own adapter reading
  respected -- no neutral code change was needed to read the two existing Windows adapters)
