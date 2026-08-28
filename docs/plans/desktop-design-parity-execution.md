# Desktop design parity — execution plan

**Date:** 2026-08-28 · **Base:** `main` @ `fe95ca7` (v0.87.0) · **Source plan:** `docs/plans/desktop-design-parity-review.md`

## Part 1: Current status by finding

**Totals: 43 findings — 2 already done, 3 partially done, 38 not started** (4 of those materially unblocked by #114–#121).

Merged since the source plan: #114 (ADR 0057 sprint title), #115 (ADR 0058 gate as linked action), #116 (ADR 0059 diff payload envelope), #117 (ADR 0060 tool-call capture), #118 (ADR 0061 usage capture), #119/#120 (ADR 0062/0063 model+effort routing), #121 (ADR 0064 payload rendering), #122 (ADR 0065 sidebar rows).

### A. Shell layout
| # | Status | Evidence |
|---|---|---|
| A1 | Not started | Global `PageStatusHeader` still declared `src/Forge.Desktop/WorkspaceShellPage.xaml:17`, set per route at `WorkspaceShellPage.xaml.cs:1025-1032`. No page owns a header, scope caption, or back control anywhere. |
| A2 | Not started | `ContextualActionHost` `WorkspaceShellPage.xaml:24`; `RenderActions()` `WorkspaceShellPage.SprintWorkspace.cs:816-1230` still builds the whole button stack. ADR 0064 "What stays deferred" explicitly leaves it. |
| A3 | Not started | Whole sidebar in one `ScrollView` `WorkspaceShellPage.xaml:11-13`; status row is the last child of `SidebarHost` (`xaml.cs:310-337`), so it scrolls. |
| A4 | Not started | No `KeyboardAccelerator` anywhere in `src/Forge.Desktop`. |
| A5 | Not started — **decision** | `BuildSidebarToggleButton` `xaml.cs:378-387` still `Text = collapsed ? ">>" : "<<"`. |

### B. Sidebar
| # | Status | Evidence |
|---|---|---|
| B1 | **Already done** | PR #122 / ADR 0065. `BuildSprintRow` `xaml.cs:821-917`, `BuildHistoryRow` `:932-972`, `SprintDisplayTitle.ResolveRowTitle`. |
| B2 | **Already done** | `SidebarSelectableRow` `xaml.cs:981-989` keys off `workspace.Route.SprintId`. |
| B3 | Not started | `BuildProjectRow` `xaml.cs:592-777` has caret/name/count/gear/remove only; creation lives at `WorkspaceShellPage.ProjectOverview.cs:99-109`. |
| B4 | Not started | `BuildAddProjectRow` `xaml.cs:493-530` still renders `pathEntry` + button. |
| B5 | Not started | `xaml.cs:689-739`: label row plus the full capped history list, always expanded. |
| B6 | Not started | `removeButton` `xaml.cs:742-775`, duplicating `ProjectSettings.cs:151-160`. |
| B7 | Not started — **decision** | Quota is still `Unknown`-only: ADR 0052 unchanged and re-confirmed as deferred in ADR 0061. Sidebar stacks five `SidebarStatusLine`s `xaml.cs:310-337`. |

### C. Sprint workspace status header
| # | Status | Evidence |
|---|---|---|
| C1 | Not started; partly unblocked | Stat strip is stage/progress/findings only `SprintWorkspace.cs:323-332`. **Branch needs no contract** — `WorktreeLayout.IntegrationBranch(sprintId)` is pure (`SprintGitIsolation.cs:14-18`). **Elapsed still has no contract**: `SprintStatus` (`StatusContracts.cs:57-63`) and `SprintWorkspaceSummary` (`WorkspaceSummary.cs:14-25`) carry no created/started timestamp. **Diff data now exists per attempt** (ADR 0059) but ADR 0064 names the missing piece: a cross-attempt aggregation rule. |
| C2 | Not started (backend groundwork only) | #119/#120 only *route* the already-frozen model/effort. No per-sprint choice exists: `ExecutionProfilePolicy.cs:17-23` freezes one model per provider and says "Revisit once real per-project, per-phase model selection exists"; `ILlmProvider` exposes `DefaultModel` only, no enumeration (`ProviderContracts.cs:224-257`). Header renders read-only `ActiveProviderModelText` `SprintWorkspace.cs:334-349`. |
| C3 | Not started | No popovers. Stage graph is buildable from `currentDetails.Nodes`. **No GitHub pull-request integration exists**: the only GitHub client in `src` is `Forge.Updater/GitHubReleaseApi.cs` (the self-updater's releases-endpoint reader, consumed by `ReleaseLookup.cs`), which knows nothing about sprints or pull requests. Sprint plan has no backing data. |
| C4 | Not started | `SprintWorkspace.cs:326-328` renders `n/m` text. |
| C5 | Not started | ISO `"O"` at `SprintWorkspace.cs:367`, `:372`, `:622`. |
| C6 | Not started | Stat strip `:323-332` + spoken summary `:356-364` + Details button `:383-395` all coexist. |

### D. Timeline
| # | Status | Evidence |
|---|---|---|
| D1 | **Partially done** (PR #121 / ADR 0064) | **Done:** payload chips via `TimelineCardProjector.BuildStats` (`SprintWorkspace.cs:632-642`, `StatChip` `:434-474`), lazy detail rows `:655-692`, per-type glyphs `:409-419`, operator bubble / agent sparkle / system card `:721-764`. Also done, contrary to the source plan's assumption: **per-tool-call duration and exit code already render** — `ToolCallStat` carries `DurationMilliseconds`, `ExitCode` and `Succeeded` (`WorkflowEvents.cs:71-76`) and `TimelineCards.cs:207-271` emits one detail row per call with all three plus a pass/fail tone. **Remains:** primary line is still one mono string `* <ISO> [type/actor] <msg>` `:618-626`; per-call rows sit inside the collapsed Details expander rather than a card of their own (presentation only, no data missing); no error card + "Retry step" (no per-tool-call failure/retry model); **no live status line/token rate — declined by Q14(a)**: the status half is D3's live-update mechanism and Q14 declined the streaming option (c) that would carry it, while the rate half has no data source at all, since ADR 0061 latches usage from the single terminal result event at attempt completion and reports nothing in flight (ADR 0064 already lists "Live streaming and token rate" among its deferrals, against D3). S21 ships D3's spinner and "last updated" line in its place; **no permission card — ruled out by ADR 0058** (needs an interactive provider protocol Forge does not have); **no inline diff hunks and no captured command/test output — see Q26**: ADR 0059 persists diff *statistics* and never hunks, and ADR 0060 records "never the content it did it with — no command text, no command output, no file content", so both are redaction-policy positions rather than unimplemented features. |
| D2 | **Partially done** | `InlineGateCard` `SprintWorkspace.cs:508-595` + `TimelineGateLinks`. Remains: the duplicate panel card `:983-1010` (= A2), and gate is the only inline decision kind. |
| D3 | Not started | 15s poll `SprintWorkspace.cs:31`, `:1634-1649`. No `ActivityIndicator`/`ProgressBar`/animation anywhere in `src/Forge.Desktop`. |
| D4 | Not started | Type/actor/unread all in the primary line `:622`. |
| D5 | Not started | `loadMoreButton` appended at `:1504`, below `timelineItemsHost`. |
| D6 | Not started | `"* "` prefix `:622`. |

### E. Composer
| # | Status | Evidence |
|---|---|---|
| E1 | **Partially done** | Now a bordered `CardStyle` composer card `SprintWorkspace.cs:1511-1525`. Remains: single-line `Entry` (`:146`), still inside the scrolled `ContentHost` (not pinned to a Grid row), and Stop still lives in the action panel `:880-910`. |
| E2 | Not started | `pollRawEvents` `:1533-1543`, immediately after the composer. |
| E3 | Not started; denominator partly exists | ADR 0061 landed `usage.context_window` for Claude only; Codex publishes none. ADR 0064 refuses to draw a ratio until someone decides the numerator. No attachment capability. `context.token_budget` is an assembly budget, not live consumption. |

### F. Project settings — all not started
Evidence throughout: `WorkspaceShellPage.ProjectSettings.cs`. F1 no rail/header/caption/back. F2 no AGENTS.md open action anywhere in Desktop (only integration generate/install/remove, `ForgeApplication.cs:70-77`). F3 project id shown `:34-39`. F4 bare red button in a `DangerCardStyle` Border, no title or consequence text `:150-160`. F5 **decision** — languages/token budget/allowed models `:57-74`, relink `:115-120`, recover `:122-131`, integration `:133,163-193`, diagnostic bundle `:135-143` all sit as a flat list. F6 comma-separated `Entry` `:70-73`, split on save `:88-89`.

### G. Forge settings — all not started
Evidence throughout: `WorkspaceShellPage.ForgeSettings.cs`. G1: no config keys exist for approval mode, theme, provider priority, or per-model effort (`docs/contracts/v1/configuration.json` key list). G2 **decision**: Save/Discard at `ForgeSettings.cs:79-98` and `ProjectSettings.cs:76-111`. G3: needs configuration schema support. G4 **decision**: `App.xaml` declares dark tokens only. G5: `SectionDivider`'s own comment `ForgeSettings.cs:128-133` records the rail as deliberately not reproduced.

### H. Project overview
H1 — Not started, **decision**. Page exists and is functional (`WorkspaceShellPage.ProjectOverview.cs`), styled by analogy.

### I. Visual system
I1 not started — `App.xaml` has literal `CornerRadius="8"` on setters (e.g. `:144`), `Space1`–`Space4` only, no `Radius*` keys, no `accent-2`. I2 not started — no animation primitives at all. I3 not started — only `PrimaryButtonStyle`'s `VisualStateManager` `App.xaml:148-160`; sidebar rows are plain `Border`s. I4 not started, scope reduced — `Theme/IconGlyphs.cs` now ships 16 glyphs; ADR 0064 deliberately reused `GitDiff`/`TerminalWindow`/`Cpu` rather than adding.

## Part 2: Decisions needed

**Q1 (A5) — collapsed sidebar rail toggle.** The `<<`/`>>` text toggle needs a real icon and a decision on whether the rail stays. Options: (a) keep collapse, use Phosphor `SidebarSimple` (one glyph, rotates or swaps); (b) keep collapse, use `CaretLeft`/`CaretRight` pair; (c) drop the collapsed rail entirely and make the sidebar fixed-width. → **Recommend (a)**: one glyph, matches the mockup's icon language, and the feature is already persisted across restart so removing it would be a regression.

**Q2 (A1) — what "Back" does.** The design's settings pages carry "Back to sprint". Forge navigates from the sidebar and has no history stack. Options: (a) no back control; the sidebar is the only navigation; (b) back = return to the last non-settings route (the catalog already persists `LastSelectedSprintId`); (c) back = always Project Overview for the current project. → **Recommend (b)**: matches the design's intent and reuses persistence that already exists.

**Q3 (A2) — where the ~9 non-gate actions go when the bottom panel is dissolved.** Run/resume/cancel/stop/supersede/confirm/test-work/finalize/move-to-stage. Options: (a) lifecycle actions (run/resume/cancel/finalize/move-to-stage) into the sprint header as a compact action row, decision actions (gate/confirm/test-work/supersede) inline in the timeline, Stop inside the composer card; (b) keep a slim panel for lifecycle only, move just decisions inline; (c) put everything behind one "Actions" popover in the header. → **Recommend (a)**: it is what the mockup actually shows and it kills the panel outright, which is the finding.

**Q4 (B5) — what "Archived sprints (12)" opens.** Options: (a) expand/collapse in place, collapsed by default; (b) navigate to a history section on Project Overview; (c) a popover list. → **Recommend (a)**: cheapest, keeps every sprint one click away, and collapse state can reuse the existing per-project persistence.

**Q5 (B7) — provider chips without quota data.** The plan's own either/or. Options: (a) restyle to two compact chips whose popover honestly says "no limit data available from this provider"; (b) first build a real quota signal (neither vendor CLI exposes one — ADR 0052 verified this, so this means inferring from failures, which plan 6.5 forbids); (c) drop the quota line from the footer entirely and keep only health/auth/model-availability. → **Recommend (a)**: honest, unblocks the visual work now, and leaves the door open if a vendor ever publishes a signal.

**Q6 (D1) — the permission card.** ADR 0058 ruled "Allow once / Always allow / Deny" out: it is a decision *inside* a live provider session and Forge has no interactive provider protocol. Options: (a) drop the permission card from the parity scope and record it as a known design/product divergence; (b) fund an interactive provider protocol (large, multi-slice, changes ADR 0006/0016); (c) render a read-only card showing the *frozen* permission policy (`ExecutionProfilePolicy.PermissionPolicy = "never"`) so the mockup's slot is filled with a true fact. → **Recommend (a)**, optionally plus (c) as a one-line header stat later.

**Q7 (D1) — the error card and "Retry step".** No per-tool-call failure or retry data model exists; `ToolCallStat` carries an outcome, not a re-runnable step. Options: (a) drop "Retry step"; render a failure card that surfaces the existing attempt-level failure and offers the existing supersede action; (b) build a per-step retry capability (new contract, new mutation); (c) drop both card and action. → **Recommend (a)**: the user's real recovery lever today is supersede, so surface that instead of inventing one.

**Q8 (C1) — what "elapsed" measures.** Nothing durable records when a sprint started. Options: (a) wall-clock since sprint creation; (b) wall-clock since the first attempt started running; (c) summed active attempt time (excludes idle/paused/awaiting-human). → **Recommend (b)**: "how long has this been working" is the question the mockup asks, and (a) makes a sprint parked overnight look like a 14-hour run.

**Q9 (C1) — what the header's diff numbers mean across attempts.** ADR 0064 named this as C1's blocker. Options: (a) sum every recorded `AttemptDiffRecorded` (double-counts a file edited by two attempts); (b) the latest integrated attempt only; (c) a fresh git diff of the sprint's integration branch against its base commit, computed on demand. → **Recommend (c)**: it is the only reading that answers "what has this sprint changed", the branch and base commit are both already known (`SprintGitIsolation`, `SprintStatus.BaseSha`), and `IWorktreeManager.DiffStatAsync` already exists.

**Q10 (C2) — model picker semantics.** Profiles are frozen at creation (ADR 0014) and three phases share one model per provider. Options: (a) the picker appears at *sprint creation* only, and the header shows the frozen model read-only with an "auto" indicator; (b) make the model mutable mid-sprint (breaks the frozen-profile invariant and the reproducibility guarantee); (c) picker at creation, plus a "change model" that only affects *future* attempts, recorded as a durable event. → **Recommend (a)**: it is a one-slice change and preserves ADR 0014.

**Q11 (C2/F6/G1) — where the list of selectable models comes from.** Options: (a) new `ILlmProvider.ListModelsAsync` per adapter (`codex debug models` works today, verified in ADR 0063; Claude would ship its alias set); (b) a hand-maintained static list in config; (c) free-text with validation against `models.allowed_models` only. → **Recommend (a)**: it is the only option that does not rot at the next vendor release, and half of it already runs.

**Q12 (C3) — which header popovers to build.** Options: (a) build only the two that have data — workflow stage graph and working diff — and drop pull-requests and the sprint-plan checklist from scope; (b) build stage graph + diff now, add a sprint-plan field to the sprint contract later; (c) build all four, which means a GitHub pull-request integration and a new plan contract. → **Recommend (a)**. Note for anyone revisiting (c): no pull-request integration exists, but `Forge.Updater/GitHubReleaseApi.cs` is a working GitHub REST client (releases endpoint, ETag and User-Agent handling, `IReleaseApi`), so the HTTP plumbing and an API precedent are already in the repository and the cost is lower than "from nothing".

**Q13 (C5) — timestamp format.** Options: (a) fixed `dd.MM HH:mm` in every locale (matches the mockup literally); (b) locale-aware short date+time via the current UI culture; (c) relative ("4m ago") in the timeline, absolute in the header. → **Recommend (b)**: Forge already localizes every surface, and a hardcoded day-first format is wrong for en-US users.

**Q14 (D3) — live updates.** Options: (a) keep the 15s poll, add a spinner and a "last updated" line so the staleness is honest; (b) shorten the poll to 2–3s while the sprint is running; (c) build a real streaming/push channel from Host to Desktop (new protocol work). → **Recommend (a) now, (b) as a cheap follow-up**; (c) only if the product wants genuine token-rate display. **This also decides D1's live status line/token rate, which the mockup draws on the timeline:** (a) declines it. The status half rides on D3's mechanism, so only (c) could carry it; the rate half has no data source under any of the three options, because ADR 0061 latches usage from the single terminal result event at attempt completion — nothing reports tokens while an attempt runs, so there is no rate to compute. ADR 0064 already deferred "Live streaming and token rate" for the same reason; recording it here makes the decline explicit rather than implied.

**Q15 (E1) — composer send key.** Options: (a) Enter sends, Shift+Enter newline; (b) Ctrl+Enter sends, Enter newline; (c) button only. → **Recommend (a)**: it is what every chat-like surface the mockup imitates does.

**Q16 (E3) — the `ctx 41k / 200k` counter.** Claude reports a context window; Codex reports none, and no layer has decided which counters sit over the denominator. Options: (a) drop the counter until a decision exists; (b) show the last attempt's *total* tokens with no denominator ("last attempt: 114.5k tokens"); (c) show `input+cache_read / context_window` for Claude and hide the whole chip for Codex. → **Recommend (b)**: honest, works for both providers, needs no new rule.

**Q17 (E3) — the "attach" chip.** Options: (a) drop it (no upload capability exists anywhere); (b) build file attachment for sprint messages (new contract + storage + redaction). → **Recommend (a)**.

**Q18 (F5) — the capabilities the design does not cover.** Languages, token budget, allowed models, relink, recover, integration generate/install/remove, diagnostic bundle. Options: (a) an "Advanced" section in the project-settings rail holding relink/recover/integration/diagnostics, with languages/token-budget/allowed-models promoted into named sections of their own; (b) a separate "Advanced" *page* off the settings rail; (c) leave them as a flat trailing list under the designed sections. → **Recommend (a)**: keeps everything one click deep and gives the rail real content.

**Q19 (E2/F5) — where "Poll raw events" goes.** Options: (a) into the project-settings Advanced section beside the diagnostic bundle; (b) behind the sprint header's Details expander; (c) remove it (the sprint timeline covers the common case). → **Recommend (a)**: it is a project-scoped diagnostic, so it belongs with the other diagnostics.

**Q20 (G2) — Save/Discard vs immediate apply.** Options: (a) immediate apply on both settings pages, with an inline per-row result/undo (matches the design); (b) immediate apply on Forge settings, keep Save/Discard on project settings (project writes touch `manifest.yaml` and are capability-gated); (c) keep Save/Discard everywhere. → **Recommend (b)**: the design only draws the Forge-settings page, and a failed capability-gated project write genuinely needs an explicit commit point.

**Q21 (G2) — the sections the shell has and the design does not.** Language pickers, Safety toggle, provider enable checkboxes, Notifications, provenance labels. Options: (a) keep all of them, adding them as extra sections in the new rail (Language, Safety & notifications, Providers) alongside the design's three; (b) hide provenance labels behind a per-row info affordance and keep the rest; (c) drop anything the design does not show. → **Recommend (a) + (b)'s provenance treatment**: (c) would delete shipped, reachable capability, which plan 12.1 forbids.

**Q22 (G1) — what "Approval mode" actually controls.** The design shows Ask on write / Auto / Autonomous, but Forge's execution profiles hardcode `PermissionPolicy = "never"` and the human gate is mandatory. Options: (a) map it to the existing `interaction.confirm_destructive` plus a new "auto-approve the human gate" setting; (b) ship it as read-only, showing the frozen policy; (c) drop it until a real permission model exists. → **Recommend (a)**, with an explicit note that the mandatory gate disclaimer (already localized) applies.

**Status from PR #123/#124 (both open, unmerged as of this document's base).** PR #124 implements S4 and proposes ADR 0067; it adds `interaction.auto_approve_gate` as schema, validation and resolution, and **nothing will read it**. That ADR's investigation found no skip, bypass or auto-approve path for the human gate anywhere on `main`: `SprintScheduler.AdvanceGraphAsync` promotes every `HumanGate` node unconditionally, `WorkflowStateMachines` declares `AwaitingHuman -> [Running, Failed, Cancelled]` with no `Succeeded` or `Skipped` edge, and `ResolveHumanGateAsync` has only approve/reject branches. Once #124 merges the key is therefore a placeholder. S18 renders the control; **S18b** owns the enforcement, and until S18b ships the Autonomous choice must be presented as not yet in effect rather than as a working setting.

**Q23 (G3) — scope of provider priority and per-model effort.** Options: (a) user scope only (`providers.priority`, `models.effort`); (b) project scope only; (c) user default with project override. → **Recommend (a)**: `providers.enabled` is already user-scoped, and (c) doubles the settings UI for no known need.

**Q24 (G4) — the light theme.** `App.xaml` declares dark tokens only; Nocturne ships no light ramp. Options: (a) request the light ramp from design and ship Dark/Light/Follow-system once it arrives; (b) ship a Dark-only "theme" section that is disabled with an explanatory caption; (c) drop the theme section from scope. → **Recommend (a)**, and sequence it last so it never blocks anything else.

**Q25 (H1) — Project Overview.** Options: (a) request an artboard and keep the page; (b) fold it into the sidebar — sprint creation becomes B3's per-project "+", provider health moves to the sidebar footer, suggested actions move into the sprint header — and delete the page; (c) keep the page, restyled by analogy against the settings-page pattern from A1/F1. → **Recommend (c)**: (b) is a lot of demolition for a page that currently carries init/recover/suggested-actions with nowhere else to live, and (a) blocks on external design turnaround.

**Q26 (D1) — inline diff hunks and captured command/test output.** The mockup's timeline shows a diff hunk inside an edit card and a test card carrying its console output. Neither datum exists, and neither is merely unimplemented: ADR 0059 persists diff *statistics* and deliberately never hunks, and ADR 0060 scopes tool-call capture to "what the provider did, never the content it did it with — no command text, no command output, no file content", precisely so provider output never reaches durable storage un-redacted. Exit codes are already captured and rendered (see D1); only the *content* is absent. Options: (a) drop hunks and command output from parity scope and record the redaction policy as the reason, the way Q6 records ADR 0058; (b) extend the payload contract to persist bounded hunks and truncated command output, which means a redaction pass over provider-authored content, new storage budgets, and an amendment to ADR 0059 and ADR 0060 — its own Wave-0 slice, not a rendering change; (c) render hunks and output live from the running attempt without persisting them, which contradicts ADR 0006's replay-from-durable-events model and shows nothing for a finished attempt. → **Recommend (a)**: the two ADRs took this position on purpose, (b) reopens a redaction decision for a cosmetic gain, and (c) makes the card appear and disappear depending on when it is looked at. With (a), D1 closes once S11's primary-line restructure and S16's failure card ship.

## Part 3: Execution slices

Each slice is one branch / one PR / one ADR, matching how B1+B2 shipped as ADR 0065. Slice ids are stable identifiers, not positions: every dependency below is stated from both ends, so a slice's `Depends on` and the corresponding `Blocks` must always agree.

**Wave 0 — contract & backend (unblocks the UI waves)**
- **S1+S2 · Sprint header: elapsed time and diff statistics.** One slice, not two: both are additive fields on the same read model feeding S13, mirroring why B1+B2 were combined. Adds a durable sprint start timestamp, surfaces the derivable integration-branch/worktree path through `SprintWorkspaceSummary`/`SprintStatusHeaderData`, and exposes an on-demand `DiffStatAsync` of the integration branch vs `BaseSha`. *Blocked on Q8, Q9.* Blocks S13.
- **S3 · Provider model enumeration and per-sprint model selection.** `ILlmProvider.ListModelsAsync`, `CreateSprintCommand.RequestedModel`, `ModelPolicyGate` validation of the chosen value. *Blocked on Q10, Q11.* Blocks S15.
- **S4 · Settings configuration schema.** New keys for approval mode, theme, provider priority, per-model effort; `configuration.json` + `user-config.schema.json` + resolver. *Blocked on Q22, Q23, Q24(shape only).* Blocks S18, S18b.
- **S5 · Provider quota posture.** Per Q5(a), no new signal: formalize "no limit data" as a first-class rendering state. *Blocked on Q5.* Blocks S10.
- **S6 · Attempt token rollup for the composer.** Per Q16(b), expose the latest attempt's total on the workspace summary. *Blocked on Q16.* Blocks S23.

**Wave 1 — structural view work**
- **S7 · Page chrome: remove the global header, give every page its own.** A1, F1, G5, C6's heading dedup. *Blocked on Q2.* Blocks S8, S12, S17, S18, S19.
- **S8 · Dissolve `ContextualActionHost`.** A2, D2's remaining half, Stop relocation. *Blocked on Q3.* Depends on S7. Blocks S12, S16.
- **S9 · Sidebar batch 2.** B3, B4, B5, B6. *Blocked on Q4.* Independent of S7; blocks nothing.
- **S10 · Sidebar footer: pinned, chipped.** A3, B7 rendering. *Depends on S5.* Blocks nothing.

**Wave 2 — timeline and composer**
- **S11 · Timeline presentation pass.** D4, D5, D6, C5. *Blocked on Q13.* Independent. Blocks S16.
- **S12 · Pinned multi-line composer.** E1 remainder, E2 relocation. *Blocked on Q15, Q19.* Depends on S7, S8. Blocks S23.
- **S13 · Sprint header stats and progress bar.** C1 rendering, C4. *Depends on S1+S2.* Blocks S14.
- **S14 · Header popovers.** C3 — stage graph + working diff only. *Blocked on Q12.* Depends on S13.
- **S15 · Model picker UI.** C2, F6. Not a pure rendering slice: PR #123 (open) implements S3 as the application layer only, so once it merges S15 also owns the transport that #123 defers into it — a new control-protocol query kind returning the enumerated model list, and a `RequestedModel` field on `CreateSprintRequest` carrying the chosen value (matching `CreateSprintCommand.RequestedModel`). Size it as transport plus view. *Blocked on Q10, Q11.* Depends on S3.
- **S16 · Timeline failure card.** D1's error-card half per Q7(a), and D1's last remaining presentation gap: promoting the existing per-tool-call detail rows (kind, target, duration, exit code, outcome — already projected by `TimelineCards.cs`) out of the Details expander into a card of their own. No new data. With Q26(a) and S11's primary-line restructure, this closes D1 **apart from the live status line/token rate, which Q14(a) declines with D3** — S21 ships D3's spinner and "last updated" line instead, and no slice builds a token rate. *Blocked on Q7, Q26.* Depends on S8, S11.

**Wave 3 — settings pages**
- **S17 · Project settings redesign.** F2, F3, F4, F5. *Blocked on Q18, Q19.* Depends on S7.
- **S18 · Forge settings redesign.** G1, G2, G3 UI, section rail. Presents the four keys S4 adds (PR #124, open). **`interaction.auto_approve_gate` lands inert** (per #124's proposed ADR 0067): S18 may render the control because the key exists and validates, but must not present the Autonomous choice as taking effect — the gate-skip path does not exist and S18 does not build it. `shell.theme`'s `light`/`system` values are equally inert until S24. *Blocked on Q20, Q21, Q22.* Depends on S4, S7. Blocks S24.
- **S18b · Human-gate skip enforcement.** **[Added 2026-08-28 during this document's own PR review, after the Q1–Q25 decision session; the user has not seen or approved this slice.]** Makes `interaction.auto_approve_gate` mean something: a real, audited bypass of a mandatory safety gate, so it is its own slice with its own review and never bundled into S18's UI work. Requires an `AwaitingHuman` exit edge in `WorkflowStateMachines`, a predicate in `SprintScheduler.AdvanceGraphAsync`, and a durable record of who or what approved. **ADR 0067's trap must be handled explicitly:** `StageTransitionAssessor.NodeSucceededWithLiveEvidence` returns `true` for `NodeState.Skipped`, which is unreachable today, so making the gate skippable would *silently* satisfy the `HumanApproved` stage prerequisite as a side effect — the single load-bearing assumption behind the gate's current guarantee. Also revisit ADR 0014 and `capabilities.json`'s `workflow.finalize` note ("stays human-only with no config-driven confirmation bypass"). *Depends on S4.* Not scheduled; run it only if the product commits to a real autonomous mode.
- **S19 · Project overview restyle.** H1. *Blocked on Q25.* Depends on S7.

**Wave 4 — visual system and polish**
- **S20 · Token sheet completion.** I1 radius/spacing tokenization with the live-run verification `App.xaml` defers, plus I4's glyph additions. Carries no blocking question. Blocks S21 (needs `Spinner`), S22, S24.
- **S21 · Motion and hover.** I2, I3, and D3's spinner + "last updated" line. *Blocked on Q14.* Depends on S20.
- **S22 · Keyboard accelerators and the rail glyph.** A4, A5. *Blocked on Q1.* Depends on S20.
- **S23 · Composer context counter.** E3 per Q16(b); the attach chip is dropped per Q17. *Depends on S6, S12.*
- **S24 · Light theme.** G4. *Blocked on Q24 and on an external design deliverable.* Depends on S18, S20. Schedule last.

**Critical path.** The longest chain is Q2 → S7 → S8 (Q3) → S12 (Q15, Q19) → S23 (which also needs S6), so Q2 and Q3 gate the most downstream work: S7 alone unblocks S8, S12, S17, S18 and S19. The second chain is Q8/Q9 → S1+S2 → S13 → S14 (Q12). Every Wave-0 slice can start in parallel once Q5, Q8, Q9, Q10, Q11, Q16, Q22, Q23 and Q24 (shape only) are answered. S20 carries no blocking question and can start immediately; S9 and S11 need only Q4 and Q13 respectively. Q1 gates S22, which sits behind S20, so answering Q1 early makes nothing startable sooner.

## Part 4: Decisions (recorded 2026-08-28)

The user accepted every recommended default in Q1–Q25 as-is. Two things in this document post-date that session and carry **no** user approval: **Q26**, raised during this document's own PR review and still open — its Part 2 recommendation is the working assumption but is not binding — and **slice S18b** (Part 3), invented in the same review pass and left unscheduled. Binding answers for implementers:

| Q | Decision |
|---|---|
| Q1 (A5) | Phosphor `SidebarSimple` glyph, keep collapse. |
| Q2 (A1) | Back = return to the last non-settings route (`LastSelectedSprintId`). |
| Q3 (A2) | Lifecycle actions → sprint header compact row; decision actions → inline in timeline; Stop → composer card. |
| Q4 (B5) | Expand/collapse in place, collapsed by default. |
| Q5 (B7) | Restyle to two compact chips; popover states "no limit data available" honestly. |
| Q6 (D1 permission card) | Drop from scope; record as known design/product divergence (ADR 0058 already covers the reasoning). |
| Q7 (D1 error/retry) | Failure card surfaces the existing attempt-level failure + existing supersede action; no new retry capability. |
| Q8 (C1 elapsed) | Wall-clock since the first attempt started running. |
| Q9 (C1 diff stats) | Fresh git diff of the integration branch vs. base commit, computed on demand (`IWorktreeManager.DiffStatAsync`). |
| Q10 (C2 model semantics) | Picker at sprint creation only; header shows the frozen model read-only. |
| Q11 (C2 model source) | New `ILlmProvider.ListModelsAsync` per adapter. |
| Q12 (C3 popovers) | Build only workflow stage graph + working diff; drop pull-requests and sprint-plan checklist from scope. |
| Q13 (C5 timestamps) | Locale-aware short date+time via current UI culture. |
| Q14 (D3 live updates) | Keep 15s poll, add spinner + "last updated" line. Also declines D1's live status line/token rate: (c) was the only streaming option, and ADR 0061 reports usage only at attempt completion, so no rate exists to display. |
| Q15 (E1 send key) | Enter sends, Shift+Enter inserts a newline. |
| Q16 (E3 context counter) | Show last attempt's total tokens, no denominator. |
| Q17 (E3 attach chip) | Drop; no upload capability exists. |
| Q18 (F5 uncovered capabilities) | "Advanced" section in the project-settings rail (relink/recover/integration/diagnostics); languages/token-budget/allowed-models get their own named sections. |
| Q19 (E2/F5 raw events) | Move into the project-settings Advanced section. |
| Q20 (G2 save semantics) | Immediate apply on Forge settings; keep explicit Save/Discard on project settings. |
| Q21 (G2 extra sections) | Keep Language/Safety/provider-enable/Notifications as extra rail sections; hide provenance labels behind a per-row info affordance. |
| Q22 (G1 approval mode) | Map to existing `interaction.confirm_destructive` + a new auto-approve-gate setting; keep the mandatory-gate disclaimer. **Materially narrowed after acceptance:** PR #124's investigation (proposed ADR 0067) found the human gate has no existing skip path, so S4 ships the key inert — S18 renders the control without claiming it takes effect, and the accepted decision's practical effect is deferred to **S18b**, which is unscheduled and which the user has not reviewed. |
| Q23 (G3 priority/effort scope) | User scope only. |
| Q24 (G4 light theme) | Request the light ramp from design; schedule last (S24); until then no light theme ships. |
| Q25 (H1 project overview) | Keep the page; restyle by analogy against the A1/F1 settings-page pattern. |
| Q26 (D1 hunks/output) | **Open.** Working assumption: drop inline diff hunks and captured command/test output from parity scope, per ADR 0059 and ADR 0060's redaction positions. S16 cannot close D1 until this is confirmed. |

Execution proceeds through the waves in Part 3, one branch/PR/ADR per slice, full AGENTS.md review loop per PR (≤3 full-scope + critical-only thereafter), sequenced to respect the dependency graph.
