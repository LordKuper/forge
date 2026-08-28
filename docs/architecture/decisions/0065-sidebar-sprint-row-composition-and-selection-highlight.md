# ADR 0065: Sidebar sprint-row composition and selection highlight

- Status: Accepted
- Date: 2026-08-28
- Contract version: unchanged (no durable contract, protocol, or schema is touched)

## Context

`docs/plans/desktop-design-parity-review.md` findings B1 and B2. The sidebar rendered one
single-line button per sprint (`"3. running"`) — no title, no progress, no status dot, no attention
badge — and gave the highlighted-row treatment (tinted background plus accent border) to
`SidebarSprintItem.HasActiveOperation` rather than to the sprint the shell is actually routed to.

B1 was inventoried as `[backend]`, which is now stale: ADR 0057 landed `SprintDefinition.Title`
end-to-end, and `SprintWorkspaceSummary`/`SprintStatus` already carry the title, the plan-progress
counts, and the attention flag. This slice is therefore pure Desktop rendering over existing read
models — no `Forge.Runtime` change, no new icon glyph, no new style or color resource, and no new
localized copy. Findings B3–B6 (per-project new-sprint action, add-project simplification,
archived-sprints collapse, the duplicated destructive button) are a separate slice.

## Decisions

### The row's one accessible node is the navigation `Button`; the dot, second line, and badge are decorative

`SidebarViewModel.ToSprintItem` already builds a full sentence for `AccessibleName`; it now leads
with the resolved `DisplayTitle` and includes the plan-progress fraction, so the row's single
focusable control speaks the title, the state, the progress, and any attention reason. Everything
the row draws around that button restates part of that sentence, so the status dot, the
state/progress line, and the attention badge are all excluded from the accessible tree through the
existing `Decorative`/`DecorativeGlyph` helpers.

The attention badge is deliberately in the *first* horn of PR #112 review round 2 finding 4's rule
(exclude a decorative element) rather than the second (give it a real description): it is **not** the
only carrier of the attention fact, because `ToSprintItem`'s own `attentionSuffix` already appends
the localized reason to the same accessible name. Describing the badge as well would announce the
attention twice for one row — the exact double-announcement that finding established the rule for.

### `HasActiveOperation` moves to the dot's fill, not to nothing

`HasActiveOperation` is strictly narrower than the state text: `ActiveOperationLookup.FindActive`
requires the sprint to be `Running` **and** a node to be executing a live, non-terminal,
non-stop-requested attempt. A sprint can read `running` with no live operation, so deleting the
signal along with the highlight it used to drive would lose real information.

It is retargeted onto the sprint row's own status dot as a **fill** difference — `●` when work is
executing, `○` when the sprint is idle at its current state — with `SidebarRowAccentColor` still
supplying the dot's colour for the state. Fill is a shape difference, not a colour one, and it now
sits beside the state it refines instead of tinting a whole row the way selection must.

The dot uses plain Unicode geometric shapes rather than Phosphor glyphs, for the reason
`SidebarStatusLine` already documents for its own bullet (MAUI falls back to a system font per
glyph, a safer bet for one character than a custom icon font) and because `IconGlyphs` ships only
the outline `Circle`: a Phosphor pair cannot express the fill difference without adding a glyph.

**What stays deferred:** the active-operation state is still not *spoken*. Naming it would need new
localized copy, and every message key and `.resx` in this system lives in `Forge.Runtime`, which
this Desktop-only slice does not touch. This is not a regression — `HasActiveOperation` was never in
any accessible name before this change either — but it is a real gap, and it should be closed by the
slice that next has reason to add sidebar copy.

### The accessible name leads with the sprint, and keeps the ordinal to disambiguate

`"{SprintIdLabel} {CreationSequence}"` no longer *prefixes* the name: the resolved title leads,
matching the precedent ADR 0057 already set on the Project Overview sprint card. `SprintIdLabel` is
dropped entirely — it resolves to the CLI's own `"Sprint id (empty: active sprint):"` prompt, which
never read as a label in a spoken row name.

The ordinal itself is kept, as a trailing disambiguator rather than a leading label, because a frozen
title is free text that ADR 0057 only trims, redacts and length-bounds — never makes unique. Two
sprints titled `"Fix login"` would otherwise carry byte-identical names, which is finding B1's own
defect relocated from untitled sprints onto same-titled ones. `SprintDisplayTitle.ResolveAccessible`
therefore appends it only on the **titled** path — `"Fix login (Sprint 2)"` — and never on the
untitled one, whose resolved title already *is* the ordinal and would otherwise speak
`"Sprint 2 (Sprint 2)"`. That branch is the same `IsNullOrWhiteSpace(title)` test `Resolve` makes,
not a comparison against the rendered fallback text, and it reuses the existing
`SprintUntitledFallback` copy for both roles rather than duplicating it under a second key.

History rows need this most: their name carries no progress fraction or attention suffix to vary, so
the title and ordinal are nearly all they have.

### History rows get the title and the highlight, and nothing else

A terminal sprint has no attention flag, no active operation, and no stage counts —
`SidebarHistoryItem` has no fields for any of them, by construction. History rows therefore take
finding B1's title treatment and finding B2's selection highlight, and keep their muted styling, but
render no dot, badge, or progress line.

## Consequences

- `Forge.Desktop.Presentation` (`SidebarViewModel.cs`): `SidebarSprintItem.DisplayTitle` and
  `SidebarHistoryItem.DisplayTitle` (positional, no default — each record has exactly one
  construction site, so every one is reviewed explicitly); `ToSprintItem`/`ToHistoryItem` resolve
  them through `SprintDisplayTitle.Resolve`, and their accessible names through
  `SprintDisplayTitle.ResolveAccessible`; `ToSprintItem`'s accessible name gains the progress
  fraction through the existing `SprintStatusHeaderProgressLabel` copy.
- `Forge.Desktop` (`WorkspaceShellPage.xaml.cs`): `BuildSprintRow`, `BuildHistoryRow`, and the shared
  `SidebarSelectableRow` container replace the two inline row loops; `ActiveOperationDot` /
  `IdleOperationDot`.
- The sprint row's second line overrides `MonoLabelStyle`'s `ColorNeutral600` ink with
  `ColorNeutral500`. `ColorNeutral600` measures ≈4.25:1 on the rail and ≈3.31:1 on the
  `ColorAccent900` a selected row now paints — the mono-on-tint combination is new to this slice,
  since a tinted row previously had no second line. `ColorNeutral500` clears the 4.5:1 body-text
  floor on both grounds (≈6.3:1 and ≈4.9:1), so one existing token covers selected and unselected
  alike. This matters more than usual because the line is `Decorative`-excluded and the progress
  fraction appears nowhere else in the rail.
- The sprint row's `TrackSidebarFocus` key (`sprint:{id}`) is unchanged, so focus restoration across
  a sidebar rebuild behaves exactly as before. History rows remain unregistered, as they already were.
- `VERSION` moves from `0.86.0` to `0.87.0` (MINOR: additive, no breaking change).

## References

- `docs/plans/desktop-design-parity-review.md` findings B1 and B2
- ADR 0057 (the sprint title this slice renders, and the `DisplayTitle`-not-ordinal precedent)
- PR #112 review round 2 finding 4 (the decorative-versus-described rule the badge is judged against)
- PR #112 review round 3 finding 4 (the 4.5:1 body-text floor and the `ColorNeutral500` precedent)
