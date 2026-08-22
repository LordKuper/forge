# ADR 0043: Workspace read-model projections

- Status: Accepted
- Date: 2026-08-23
- Contract version: capabilities.json 1.6.0

## Context

`docs/plans/desktop-workspace-redesign.md` section 6 replaces Desktop's
single scrolling form with a project-oriented workspace. Its sidebar,
project overview, and sprint workspace need five read projections that do
not exist today: a user-scoped project catalog, a bounded workspace summary
for the sidebar/status header, a versioned sprint timeline, a general
contextual-action projection (so Desktop stops re-deriving workflow policy
locally), and provider/account quota distinct from provider health. Section
11's Slice 1 scopes this ADR to recording the decision and reserving the
protocol surface; the actual read models, storage, and Host wiring land in
Slice 4 (catalog, summary, actions, timeline) and Slice 7 (quota).

## Decisions

### Five distinct projections, not one wider snapshot

`project.snapshot` (ADR 0005) stays the authoritative full-sprint read
model; it is not widened to also serve the sidebar. Section 6.2 is explicit
that "sidebar refresh is bounded and slower than selected-sprint refresh,"
which a shared payload cannot express — a sidebar poll that pulls every
project's full snapshot to render five summary fields does not bound
independently of selected-sprint traffic. Five separate contracts also let
each evolve on its own minor-version schedule (`docs/contracts/v1/README.md`'s
"additive optional changes require a minor contract version" applies per
schema, not to one shared shape):

- `ProjectCatalog` (section 6.1) — user-scoped, outside project state
  entirely (stable id when initialized, normalized root, optional alias,
  last-opened, last route). Local to this Desktop installation; adding or
  removing an entry never touches the project's own `.forge/` directory.
- `WorkspaceSummary` (section 6.2) — one lightweight query per known
  project: availability, active sprint summaries, attention reasons,
  current stage, progress, active operation, provider health.
- `SprintTimelinePage` (section 6.3) — cursor-paged, ordered
  `SprintTimelineItem`s projected from the existing append-only workflow
  journal, redacted before persistence and again before rendering (ADR
  0039's redaction chokepoint governs both passes; no new redaction rule).
- `AvailableAction` (section 6.4) — the Host's own action list with safety
  class, confirmation requirement, typed inputs, blockers, idempotency key,
  and stale behavior. Desktop renders it; it never computes it.
- `ProviderQuotaSnapshot` (section 6.5) — verified account/model quota only.
  Distinct from `provider.health` (toolchain readiness) and from a sprint's
  retry budget; unverified quota renders as unknown, never inferred.

### Reserved, not implemented, capability ids

`capabilities.json` (1.5.0 → 1.6.0) gains `workspace.summary`,
`sprint.timeline`, `workspace.available_actions`, and `provider.quota_status`
(the project-catalog projection is local-only and has no Host protocol
surface, so it gets no capability id). Each carries a plausible `cli`/
`desktop`/`permission`/`acceptance` shape — the Stage 0 gate requires every
capability entry to have them — but none is added to
`Forge.Presentation.CapabilityIds.Implemented`. This mirrors `quality.evaluate`'s
own reservation (ADR 0042): the Host's handshake advertises only
`CapabilityIds.Implemented`, so an older Desktop built against today's Host
cannot be silently invited into a query the Host does not yet serve (plan
section 9.2). `SurfaceParityTests`, `ControlPlaneTests`, and the CLI/Desktop
parity checks all key off `CapabilityIds.Implemented`, not the full JSON
list, so this reservation changes no existing test's expectations.

### No domain or storage code in this slice

No `ProjectCatalog`/`WorkspaceSummary`/`SprintTimelinePage`/`AvailableAction`/
`ProviderQuotaSnapshot` type, storage port, or Host handler exists yet.
Building them now, ahead of Slice 4/7's own acceptance cases, would freeze
a shape before the sidebar and action-renderer work that actually
constrains it exists — the same reasoning ADR 0014 used to defer the node
executor rather than freezing `ExecutionProfile` consumption code early.

## What stays deferred

- Project catalog persistence and relinking (Slice 4).
- Workspace summary, available-action, and timeline read models,
  storage, and Host query handlers (Slice 4).
- Provider quota adapters, gated on "only for providers that expose
  verified quota data" (Slice 7).
- Desktop sidebar/project-overview/sprint-workspace consumption of any of
  the above (Slices 5-6).

## Consequences

- `capabilities.json` documents four new reserved capability ids with no
  behavior behind them yet; `docs/contracts/v1/README.md`'s existing
  "unstable until 1.0.0" language already covers a future shape change to
  any of them.
- Later slices implement against a named contract surface instead of
  inventing capability ids and CLI/Desktop shapes under review pressure.
- No production code changed in `Forge.Runtime`, `Forge.Host.Runtime`, or
  `Forge.Desktop*` by this ADR.

## References

- ADR 0005 (Host as sole `.forge/` writer; `project.snapshot`/`control.events`)
- ADR 0039 (redaction chokepoint reused by the timeline projection)
- ADR 0042 (the CLI-only/reserved-capability precedent this ADR mirrors)
