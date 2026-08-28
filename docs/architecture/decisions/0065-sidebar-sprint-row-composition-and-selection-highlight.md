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
focusable control speaks the title, the state, the progress, and any attention reason. The
state/progress line and the attention badge restate part of that sentence, so both are excluded from
the accessible tree through the existing `Decorative`/`DecorativeGlyph` helpers.

The status dot is excluded too, but *not* as a restatement: its colour restates the state, while its
**fill** carries `HasActiveOperation`, which no accessible name in this rail states (see the deferred
gap recorded below). Describing the dot as a redundant echo would therefore be wrong on the one fact
it uniquely carries, so `BuildSprintRow`'s own remarks name the two reasons separately.

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

### The row label names the sprint, and carries the ordinal to disambiguate

`"{SprintIdLabel} {CreationSequence}"` no longer labels the row: `SprintIdLabel` is dropped entirely
— it resolves to the CLI's own `"Sprint id (empty: active sprint):"` prompt, which never read as a
label in a spoken row name — and the frozen title becomes the substance of the label, matching the
precedent ADR 0057 already set on the Project Overview sprint card.

The ordinal itself is kept, as a parenthesized disambiguator rather than a bare label, because a
frozen title is free text that ADR 0057 only trims, redacts and length-bounds to 200 characters —
never makes unique. Two sprints titled `"Fix login"` would otherwise carry byte-identical labels,
which is finding B1's own defect relocated from untitled sprints onto same-titled ones.
`SprintDisplayTitle.ResolveRowTitle` therefore adds it only on the **titled** path —
`"(Sprint 2) Fix login"` — and never on the untitled one, whose resolved title already *is* the
ordinal and would otherwise read `"(Sprint 2) Sprint 2"`. That branch is the same
`IsNullOrWhiteSpace(title)` test `Resolve` makes, not a comparison against the rendered fallback
text, and it reuses the existing `SprintUntitledFallback` copy for both roles rather than
duplicating it under a second key.

**One string is both drawn and spoken.** `ResolveRowTitle` backs `DisplayTitle` itself, not a
separate accessible-only variant layered over it, so a visible label and a spoken name cannot
disagree about which sprint a row is. Deriving both from one function makes that divergence
unrepresentable.

The ordinal is unconditional on the titled path rather than applied only when a title actually
collides. Collision-conditional labelling would make this a set-aware operation instead of a pure
per-sprint one; it would relabel a row whenever an unrelated sprint was created or archived; and its
uniqueness scope is ambiguous across the separately rendered active and history lists, which sit
adjacent in the rail. It would also have to be mirrored in the spoken name to keep the two in
agreement, undoing the property above. A stable, local, always-present ordinal costs one short
parenthetical.

#### The ordinal leads the label because only trailing truncation exists on Windows

Both row buttons use `LineBreakMode.TailTruncation`, and the ordinal is placed at the **front** of
the string so the rail can only ever trim the title's tail, never the disambiguator. Front-anchoring
is what makes the ordinal survivable; the truncation mode is not doing that work and cannot.

Placing the ordinal last and asking for `LineBreakMode.MiddleTruncation` does **not** work here.
`Forge.Desktop` targets `net10.0-windows10.0.19041.0` only, so WinUI is the sole renderer, and
`Button.MapLineBreakMode` → `ButtonExtensions.UpdateLineBreakMode` →
`TextBlockExtensions.SetLineBreakMode` maps both `HeadTruncation` and `MiddleTruncation` onto
`TextTrimming.WordEllipsis`. WinUI implements no head or middle form at all: `WordEllipsis` trims
from the END at a word boundary. A middle mode is therefore a *coarser* tail mode — it drops a whole
trailing word where `TailTruncation` (`CharacterEllipsis`) drops characters — and a trailing ordinal
is lost either way. With a 200-character title bound and a rail roughly 210px wide for text, that is
the common case, not an edge one.

The spoken name inherits the same order rather than keeping a title-first phrasing of its own.
`ToSprintItem`/`ToHistoryItem` build a comma-separated sentence already led by the project name, so
`"(Sprint 2) Fix login"` reads as one more item in that list; the small awkwardness of hearing the
ordinal first is worth less than the guarantee that exactly one string is drawn and announced, which
is the property the section above rests on.

History rows need all of this most: they carry no progress fraction or attention suffix to vary, so
the title and ordinal are nearly all they have.

### History rows get the title, the state word, and the highlight

A terminal sprint has no attention flag, no active operation, and no stage counts —
`SidebarHistoryItem` has no fields for any of them, by construction. History rows therefore take
finding B1's title treatment and finding B2's selection highlight, and keep their muted styling, but
render no dot, badge, or progress fraction.

They do keep a second line carrying the **state word alone**. The pre-redesign row read
`"3. cancelled"`, so the state was always legible as text; rendering the title alone left
`SidebarRowAccentColor`'s green-versus-neutral tint as the only thing separating a completed sprint
from a cancelled one — and only once the row was selected, since an unselected row paints every
state the same `ColorNeutral600`. That is status by colour alone, which plan 12.6 forbids. Restoring
the word also gives the archived list the same two-line rhythm as the active list above it.

## Consequences

- `Forge.Desktop.Presentation` (`SprintDisplayTitle.cs`): `ResolveRowTitle` is new, alongside the
  unchanged `Resolve`, which still serves `ProjectOverviewViewModel`. No existing contract is
  replaced.
- `Forge.Desktop.Presentation` (`SidebarViewModel.cs`): `SidebarSprintItem.DisplayTitle` and
  `SidebarHistoryItem.DisplayTitle` (positional, no default — each record has exactly one
  construction site, so every one is reviewed explicitly); `ToSprintItem`/`ToHistoryItem` resolve
  them through `SprintDisplayTitle.ResolveRowTitle` and interpolate that same string into the
  accessible name, which therefore has no separately resolved title of its own; `ToSprintItem`'s
  accessible name gains the progress fraction through the existing
  `SprintStatusHeaderProgressLabel` copy.
- `Forge.Desktop` (`WorkspaceShellPage.xaml.cs`): `BuildSprintRow`, `BuildHistoryRow`, and the shared
  `SidebarSelectableRow` container replace the two inline row loops; `ActiveOperationDot` /
  `IdleOperationDot`; both row buttons use `LineBreakMode.TailTruncation`.
- Both second lines override `MonoLabelStyle`'s `ColorNeutral600` ink with `ColorNeutral500`.
  `ColorNeutral600` measures ≈4.25:1 on the rail and ≈3.31:1 on the `ColorAccent900` a selected row
  now paints — the mono-on-tint combination is new to this slice, since a tinted row previously had
  no second line. `ColorNeutral500` clears the 4.5:1 body-text floor on both grounds (≈6.3:1 and
  ≈4.9:1), so one existing token covers selected and unselected alike. This matters more than usual
  because the lines are `Decorative`-excluded and are the only rail-visible carriers of the progress
  fraction and the archived state word. On a history row that ink reads one step brighter than the
  deliberately muted `ColorNeutral600` title above it: the title carries the settled weight at button
  size, while a 10.5pt mono line gets no large-text contrast allowance.
- The sprint row's `TrackSidebarFocus` key (`sprint:{id}`) is unchanged, so focus restoration across
  a sidebar rebuild behaves exactly as before. History rows remain unregistered, as they already were.
- `VERSION` moves from `0.86.0` to `0.87.0` (MINOR: additive, no breaking change).

## References

- `docs/plans/desktop-design-parity-review.md` findings B1 and B2
- ADR 0057 (the sprint title this slice renders, and the `DisplayTitle`-not-ordinal precedent)
- PR #112 review round 2 finding 4 (the decorative-versus-described rule the badge is judged against)
- PR #112 review round 3 finding 4 (the 4.5:1 body-text floor and the `ColorNeutral500` precedent)
