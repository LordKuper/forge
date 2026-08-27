# ADR 0057: Optional sprint title

- Status: Accepted
- Date: 2026-08-27
- Contract version: project-snapshot.schema.json 1.5.0; ProjectWorkspaceSummary 1.1.0

## Context

`docs/plans/desktop-design-parity-review.md` finding B1: every Forge surface identifies a sprint by
creation sequence and state alone ("2. running"), because nothing in the sprint contract carries a
human-meaningful label. The sidebar redesign that finding proposes cannot show a name that does not
exist. This ADR adds the field end-to-end (domain, persistence, Host protocol, CLI, Desktop) so a
later slice can restyle the sidebar against real data. It deliberately does not change sidebar
rendering.

## Decisions

### One nullable `Title`, frozen at creation, on `SprintDefinition`

`SprintDefinition` is the record of everything a sprint freezes once and never changes (base commit,
workflow, configuration snapshot, frozen providers, execution profiles). A title belongs there, not
in mutable node/attempt state: it describes the sprint's whole purpose, which is fixed the moment
the sprint exists. It is nullable, with no default, and follows `DefaultBranch`'s own precedent for
"absent for a sprint frozen before this field existed."

### Normalization, redaction, then the length bound — in the orchestrator only

`SprintOrchestrator.CreateSprintAsync` owns every rule, so no surface re-implements any of them:
surrounding whitespace is trimmed, a blank result becomes `null` (no title, never an empty string --
mirroring `ProjectCatalogStore.SetAliasAsync`'s "empty/whitespace clears"), the result is passed
through `SecretRedactor` because a title is free-typed text that could carry a pasted token, and
only then is it measured against `SprintOrchestrator.MaxSprintTitleLength` (200, the same bound
`ProjectCatalogStore.MaxAliasLength` uses for the other short display name in this system).

Redaction runs **before** the length check, not after. A redaction placeholder is generally longer
than the secret it replaces, so checking the raw input instead could freeze -- and later serialize
-- a title past `project-snapshot.schema.json`'s own `maxLength: 200`. An over-length title is
refused with `sprint_title_too_long` before any event is written, the same fail-closed placement the
adjacent empty-candidates and model-policy gates already use. A blank title is never an error, so
there is no `sprint_title_required` counterpart.

### The Host tolerates an absent `create_sprint` payload

`CreateSprintRequest` was an empty record; it now carries `Title`. `ControlProtocol.Version` is
unchanged: this is an additive, optional payload field, and the protocol matches on major version
only (`ControlProtocol.IsCompatible`), so a client predating this ADR remains a compatible peer.
`DispatchCreateSprintAsync` therefore treats a null/absent payload as "no title" rather than throwing
the way its sibling dispatchers do for their own mandatory payloads. That asymmetry is deliberate and
commented at the call site so a future consistency refactor does not silently break every older
client.

### The fallback label lives in presentation, never in a contract

`SprintDisplayTitle.Resolve` (Forge.Desktop.Presentation) returns the frozen title when it is
non-blank and a localized "Sprint {N}" otherwise, mirroring `ProjectDisplayName`'s existing shape.
Nothing synthesizes a title into the journal, the project snapshot, or the workspace summary -- all
three stay honestly nullable. The fallback is the sprint's own creation sequence, never the project
root or directory name, which would render an identical label for every sprint in a project.

## What stays deferred

- **Renaming.** A title is frozen at creation and there is no rename capability, matching everything
  else `SprintDefinition` holds. Adding one later means a new mutation, a new durable event, and a
  new permission -- none of which this slice needs, and none of which can be retrofitted for free by
  making the field settable.
- **A separate `Goal`/instruction field.** The intake node takes no prompt and nothing today would
  read a goal, so shipping one now would add a contract field with zero consumer. If the composer
  work in findings D1/D2 needs one, it should arrive together with its real consumer.
- **Sidebar rendering.** Finding B1's visual half (`WorkspaceShellPage`'s sidebar builders) is a
  separate slice. This one renders the title only on the Project Overview sprint card -- a page with
  no design mockup, already styled by analogy -- so it does not collide with a pending redesign.
- **Titles on already-created sprints.** Every existing sprint loads with `Title = null` and shows
  the fallback. There is no backfill and (see above) no rename to perform one with.

## Consequences

- `Forge.Runtime` (`Domain/SprintDefinition.cs`): `SprintDefinition.Title` (nullable, positional,
  last).
- `Forge.Runtime` (`Application/FileSprintEventLog.cs`): `PersistedDefinition.Title` with no default,
  written by `SaveDefinitionAsync` and read back by `LoadDefinitionAsync`; an absent `title` key
  loads as `null` instead of failing.
- `Forge.Runtime` (`Application/SprintOrchestrator.cs`): `CreateSprintCommand.Title` (last, optional),
  `MaxSprintTitleLength`, and the `NormalizeTitle` gate.
- `Forge.Runtime` (`Application/StartupContracts.cs`): `DiagnosticCodes.SprintTitleTooLong`
  (`sprint_title_too_long`).
- `Forge.Runtime` (`Application/StatusContracts.cs`, `StatusAdvisor.cs`): `SprintStatus.Title`;
  `StatusAdvisor.ContractVersion` `1.4.0` -> `1.5.0`.
- `Forge.Runtime` (`Application/WorkspaceSummary.cs`): `SprintWorkspaceSummary.Title`;
  `ProjectWorkspaceSummary.ContractVersion` `1.0.0` -> `1.1.0`.
- `Forge.Runtime` (`Application/ForgeApplication.cs`, `RemoteForgeMutations.cs`):
  `IForgeMutations.CreateSprintAsync` gains a `string? title` parameter (no default -- every call
  site is reviewed explicitly).
- `Forge.Host.Client` (`ControlProtocol.cs`): `CreateSprintRequest(string? Title = null)`;
  `ControlProtocol.Version` unchanged.
- `Forge.Host.Runtime` (`ControlPlaneHostedService.cs`): `DispatchCreateSprintAsync` deserializes the
  payload and tolerates its absence.
- `Forge.Cli` (`CliApplication.cs`): `forge sprint create --title <text>`.
- `Forge.Cli` (`ExitCodes.cs`): `SprintTitleTooLong` joins the `Usage` arm, alongside the other
  bounded/required-input diagnostics, instead of falling through to `Internal`.
- `Forge.Desktop.Presentation` (`SprintDisplayTitle.cs` -- new; `MainPageViewModel.cs`,
  `ProjectOverviewViewModel.cs`): the title parameter threads through, and
  `ProjectOverviewSprintCard.DisplayTitle` carries the resolved label.
- `Forge.Desktop` (`WorkspaceShellPage.ProjectOverview.cs`, `WorkspaceShellPage.xaml.cs`): a
  screen-reader-named title `Entry` above "Create sprint", and the title on each sprint card header.
- `Forge.Runtime` (`Localization/`): `SprintTitleLabel` and `SprintUntitledFallback` in both
  `Messages.resx` and `Messages.ru.resx`.
- `docs/contracts/v1/README.md`: a `2 | usage | sprint_title_too_long` row in the frozen exit-code
  table, matching the `ExitCodes.For` arm above.
- `docs/contracts/v1/schemas/project-snapshot.schema.json`: `$defs.sprint.title` (nullable,
  `maxLength: 200`, not required) and `1.5.0` added to the `schema_version` enum.
- `docs/contracts/v1/capabilities.json`: `1.11.0` -> `1.12.0`; `sprint.manage`'s `cli` documents
  `--title`. It is written after the verb alternatives (`<create|run|resume|cancel> [--title <text>]`)
  rather than inside them, because `SurfaceParityTests`' documented-CLI parser splits that string on
  spaces and would stop recognizing the verbs as sibling subcommands otherwise.
- `VERSION` moves from `0.79.1` to `0.80.0` (MINOR: additive, no breaking change).

## References

- `docs/plans/desktop-design-parity-review.md` finding B1 (the gap this ADR's contract half closes)
- ADR 0036 (`DefaultBranch`, the nullable frozen-field precedent `Title` follows)
- ADR 0054 (the redaction posture applied to durable free-typed user text)
