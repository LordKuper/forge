# Desktop design parity review

**Status:** Review findings
**Date:** 2026-08-27
**Scope:** `src/Forge.Desktop`, `src/Forge.Desktop.Presentation`, and the contracts they read

## 1. Source of truth

Claude Design project `527e56d8-df37-4cd0-bca8-16f94dd14b21`:

- `Forge Desktop.dc.html` — the interactive mockup (three pages: sprint workspace, project
  settings, Forge settings)
- `_ds/nocturne-.../styles.css` — Nocturne tokens and component classes
- `_ds/nocturne-.../_ds_bundle.js`, `support.js` — canvas runtime, no product content

Compared against the shell as of `v0.79.1` (Nocturne visual pass, PR #112).

## 2. Verdict

Tokens, typography, icon family, and status colors match the design system. Structure,
information density, and a large part of the functionality do not: the mockup describes a
chat-like agent workspace, the current build is a form-and-button shell rendered in Nocturne
colors.

Findings are `P1` (blocks design parity or the product behavior the design implies), `P2`,
`P3` (cosmetic). `[backend]` marks work that needs a contract or Host capability first.

## 3. Shell layout

| # | Sev | Finding |
| - | --- | ------- |
| A1 | P1 | The mockup has no global page-title bar; every page owns its header (title, scope caption, "Back to sprint"). The shell adds a permanent `PageStatusHeader` row (`WorkspaceShellPage.xaml`) and has no per-page header or back control. Remove the global row, move title and caption into each page. |
| A2 | P1 | `ContextualActionHost` (a bottom "Actions" panel holding a stack of buttons) does not exist in the design. Its actions live inline in the timeline (permission card, "Retry step") and in the status header. Dissolve the panel into those two places. |
| A3 | P2 | The design pins the sidebar footer (Settings row, provider status); the shell puts the whole sidebar in one `ScrollView`, so both scroll away. |
| A4 | P2 | Keyboard hints in the design (`⌘N` add project, `⌘,` settings) have no accelerators behind them. Add `Ctrl+N` / `Ctrl+,` and render the hints. |
| A5 | P3 | The collapsed rail is an implementation-only feature with a `"<<"`/`">>"` text toggle. Needs a design decision and a Phosphor glyph. |

## 4. Sidebar

| # | Sev | Finding |
| - | --- | ------- |
| B1 | P1 | Sprint row: the design shows a status dot, the sprint title, a second line (`running · step 3/7`), and an attention badge. The shell renders a single-line button `"3. running"` — no title, no progress, no badge, no dot. `[backend]` `SprintWorkspaceSummary` carries no title and sprints are created without one (`CreateSprintAsync` takes only a root); a sprint title/goal field is a prerequisite. |
| B2 | P1 | Row highlight follows `HasActiveOperation` instead of the selected route. The design highlights the selected sprint; selection currently only tints text. |
| B3 | P2 | The per-project "new sprint" action is missing; sprint creation exists only on Project overview. |
| B4 | P2 | "Add project" renders a manual path `Entry` plus a button; the design is one button opening the folder picker. The picker is already the empty-input fallback, so drop the text field (also matches redesign plan 4.1, "no manually entered roots"). |
| B5 | P2 | "Archived sprints" is a count row that opens history in the design; the shell renders a label plus the whole (capped) history list inline, always expanded. |
| B6 | P2 | The per-project "Remove project" button in the sidebar is not in the design — removal belongs only to the Project settings danger zone. Duplicated destructive action. |
| B7 | P2 | Provider footer: the design uses two compact provider chips with hover popovers (weekly limit, 5h session, degraded state); the shell stacks five text lines. `[backend]` the quota bars cannot be built today — `ProviderQuotaProjector` only ever yields `Unknown` (ADR 0052). Either restyle to chips plus an honest "no limit data" popover, or land a provider quota signal first. |

## 5. Sprint workspace status header

| # | Sev | Finding |
| - | --- | ------- |
| C1 | P1 | Missing stat columns: worktree/branch, elapsed, diff (file count, +/−). Branch data exists (`GitContextReader`, `SprintGitIsolation`) and elapsed is derivable; `[backend]` diff statistics have no contract. |
| C2 | P1 | Model is a read-only label; the design makes it a per-sprint picker (`auto` plus the reachable models). Today model choice is a comma-separated "Allowed models" text field in project settings. |
| C3 | P2 | None of the header popovers exist: pull requests, workflow stage graph, working diff, sprint plan checklist. The stage graph is buildable from the workflow contract; `[backend]` the sprint plan has no backing data and no GitHub integration exists anywhere in `src`. |
| C4 | P2 | Plan progress renders as `3/7` text only; the design adds the segmented tick bar. |
| C5 | P3 | Timestamps render as ISO `"O"`; the design uses a short local form (`26.08 12:04`). Applies to the header and every timeline row. |
| C6 | P3 | The header states stage/progress/findings twice (stat strip plus a spoken summary line) and adds a "Details" button where the design uses hover popovers. Keep the accessibility line, hide it visually. |

## 6. Timeline (largest gap)

| # | Sev | Finding |
| - | --- | ------- |
| D1 | P1 | The design renders a user bubble, agent prose, per-tool cards (read/edit/command) with durations, inline diff hunks with +/− coloring, test output with exit code, an error card with "Retry step", a permission card with Allow once / Always allow / Deny, and a live status line with step and token rate. The shell renders every event as one monospace line `* <ISO> [type/actor] <message>` plus Details/Copy. `[backend]` `WorkflowEvent` has nine types and no tool-call, diff, test, or permission payload — the contract extension blocks all of this UI work. |
| D2 | P1 | No inline decision affordance: permission and gate approval live in the bottom action panel rather than in the event that requested them. |
| D3 | P2 | No streaming indicator; the timeline refreshes on a 15s poll. The design implies live append with a spinner and token rate. |
| D4 | P2 | Event type, actor, and correlation ids sit in the primary line; the design keeps them behind the expandable detail. |
| D5 | P3 | "Load more" sits at the bottom, but older items load upward — the control belongs at the top. |
| D6 | P3 | Unread state is a `"* "` text prefix instead of a visual treatment. |

## 7. Composer

| # | Sev | Finding |
| - | --- | ------- |
| E1 | P1 | The composer is a single-line `Entry` inside the scrolled content; the design is a pinned multi-line footer card with the Stop button inside it. Stop currently lives in the separate action panel. |
| E2 | P2 | The "Poll raw events" ghost button sits next to the composer and is not in the design. Move it to details/diagnostics. |
| E3 | P3 | The "attach" chip and the `ctx 41k / 200k` counter have no backing capability (no upload, no token-usage projection). `[backend]` project `TokenBudget` exists but consumption is not reported. |

## 8. Project settings

| # | Sev | Finding |
| - | --- | ------- |
| F1 | P1 | No section nav rail (Repository / Instructions / Danger zone), no page header, no scope caption ("project scope · overrides user settings"), no back button. |
| F2 | P1 | The "Open AGENTS.md" instructions action is missing entirely. |
| F3 | P2 | The repository block exposes the project id, which the design hides, alongside root and alias. |
| F4 | P2 | The danger-zone card has no title and no consequence text ("Detaches the project and deletes its sprint history. The repository on disk is untouched.") — only a bare red button. |
| F5 | P2 | Capabilities the design does not cover at all: languages, token budget, allowed models, relink, recover, integration generate/install/remove, diagnostic bundle. Needs a design decision (for example an Advanced/Integration section) rather than silent divergence. |
| F6 | P2 | "Allowed models" as a comma-separated text field contradicts the design's structured model rows. |

## 9. Forge settings (nearly disjoint from the design)

| # | Sev | Finding |
| - | --- | ------- |
| G1 | P1 | The design's sections do not exist: **Models & providers** (priority-ordered draggable list with provider label, context size, `default` badge, per-model effort control, "Add model"), **Approval mode** (Ask on write / Auto / Autonomous), **Theme** (Dark / Light / Follow system). |
| G2 | P1 | Sections the shell has and the design does not: Language (three pickers), Safety toggle, provider enable checkboxes, Notifications, provenance labels, Save/Discard. The design implies immediate apply with no Save button — decide the contract for both settings pages. |
| G3 | P2 | The provider list has no ordering or priority concept. `[backend]` provider priority and per-model effort need configuration schema support. |
| G4 | P2 | The theme switch requires a light palette; Nocturne defines dark tokens only. Request the light ramp from design before implementing. |
| G5 | P2 | Same missing header, scope caption, back button, and section rail as F1. |

## 10. Project overview

| # | Sev | Finding |
| - | --- | ------- |
| H1 | P2 | No artboard covers this page; it is styled by analogy. Request a design, or fold the page into sidebar navigation. |

## 11. Visual system

| # | Sev | Finding |
| - | --- | ------- |
| I1 | P3 | `App.xaml` mirrors part of Nocturne only: the radius scale is not tokenized (literal values on setters), the spacing scale is truncated, `accent-2` and most ramp steps are absent. Acceptable as a deliberate "declare only what is read" policy, but the radius tokens still need the live-run verification the file defers. |
| I2 | P3 | The design animates a pulsing "running" dot, a spinner, and an indeterminate bar; the shell has no animation. |
| I3 | P3 | Hover states: the design tints project/sprint rows and icon buttons; the shell only inherits the button style's `PointerOver`. |
| I4 | P3 | The icon set ports 16 glyphs; the findings above need roughly 14 more (branch, pull request, timer, paperclip, trash, arrow-left, check, spinner, drag handle, file inspect, cursor, shield, archive variants). |

## 12. Sequencing

Contract work blocks the highest-value UI findings and should be scheduled first:

1. Sprint title/goal in the sprint contract — blocks B1 and the design's whole sidebar.
2. Structured timeline events (tool call, diff, test, permission) — blocks D1 and D2, the single
   largest divergence.
3. Diff statistics, worktree/branch, and elapsed in the sprint header contract — blocks C1.
4. Provider quota signal (B7), provider priority and effort configuration (G3), token-usage
   reporting (E3).

Everything else is view-layer work that can start immediately. Findings F5, G2, G4, H1, and A5
need a design decision before implementation, not code.
