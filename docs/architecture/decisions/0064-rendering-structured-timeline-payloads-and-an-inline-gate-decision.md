# ADR 0064: Rendering structured timeline payloads, and an inline gate decision beside its own event

- Status: Accepted
- Date: 2026-08-28
- Contract version: unchanged (no wire, schema, or `capabilities.json` change)

## Context

ADRs 0058, 0059, 0060, and 0061 each landed a data half and explicitly deferred its rendering half:
the gate as a node-scoped `AvailableAction` with a `TimelineSequence` back-link, and three structured
`payload` families (`diff`, `tool_use`, `usage`). Each of those ADRs states, in its own deferrals,
that `TimelineItemView` carries `Payload` through and nothing draws it. This is the first slice that
draws any of it — `docs/plans/desktop-design-parity-review.md` finding D1, plus the additive half of
finding D2.

It is a view-layer slice only. No contract, schema, or Host behaviour changes; the entire decision
surface is what the existing data may and may not be turned into on screen.

## Decisions

### A usage card renders only reported counters, and never a computed ratio

Every `UsagePayload` field is independently nullable and ADR 0061 is explicit that `null` means "not
reported" while `0` would mean "reported as zero". `TimelineCardProjector` therefore emits one chip
per **non-null** counter and omits an unreported one entirely — no chip, no zero, no placeholder.

This is not a stylistic preference; it is the reason the card exists. The localized one-line summary
(`workflow.attempt_usage_recorded`) has to substitute `0` for an unreported counter to render a fixed
five-argument sentence, so that line already collapses the distinction. The structured payload is the
only place the true absence survives, and a card that re-collapsed it would add nothing over the
sentence it sits beneath.

`ContextWindow` renders as its own labelled value and is never a denominator. ADR 0061 established
that Codex publishes no context-window field at all and must not be given a guessed one, so a
`used / window` reading would exist for Claude attempts and not for Codex ones. Worse, no layer of
this codebase has decided *which* counters belong over that denominator — input alone? input plus
cache read? the full four-counter total the summary line already reports? Each is defensible and they
differ by orders of magnitude on a real Claude attempt (6 vs. 114,518 on the committed capture). A
ratio would present that unmade decision as a measurement. Drawing one is deferred to whatever slice
is willing to make and document it.

### A chip that restates the summary sentence leaves the accessible tree; a chip that adds information stays

`TimelineStat.RestatesSummary` is a projector-side fact, not a view-side guess: the diff card's
files/added/deleted chips and the tool-use card's calls/commands/edits chips carry exactly the three
numbers their own `workflow.attempt_*_recorded` template already substitutes, verbatim. The item's
summary label is rendered through `Describe`, so a screen reader already speaks those numbers in a
full sentence. Repeating each as its own bare stop ("files 3", "added 120") turns one coherent
sentence into four fragments that say the same thing, which is worse than silence — the same
reasoning `DecorativeGlyph` already applies to a Phosphor glyph whose meaning the adjacent text
carries.

The two chip kinds that are **not** in that sentence stay in the tree with a real
`SemanticProperties.Description`:

- every usage chip, because which counters exist is precisely what the sentence cannot express (see
  above);
- the `unrecognized` drift chip, because `unmapped_items` (ADR 0060) appears in no template at all.

Exclusion is applied to the chip `Border` **and** both of its `Label`s, not to the container alone: a
`Label`'s UIA name is its own text, so excluding only the parent would leave the two fragments as
stops anyway. `DecorativeGlyph` was generalized to a `Decorative<T>` helper for this; its `Label`
overload is unchanged in behaviour.

### The inline gate card is strictly additive, and only it may name a node

`ContextualActionHost`'s gate card is untouched — not restyled, not moved, not conditioned on the new
one. Dissolving that panel is finding A2, separate structural work; this slice only adds a second
surface.

That is not merely sequencing caution. `TimelineGateLinks.Resolve` deliberately emits **no** link for
a gate whose requesting `TimelineSequence` is not among the currently loaded timeline items. The
timeline is paged, so a decision requested long before the loaded window has no honest inline anchor,
and inventing one would attach it to an unrelated event. The panel is the correct surface for exactly
that case, which means removing the panel is a decision this slice must not force.

The two surfaces call `ResolveGateAsync` with different node arguments:

- The inline card passes `link.NodeId`, taken from the `approve_gate:<node-id>` row itself. One card
  is emitted per node-scoped row, so naming the exact node is unambiguous — which is what makes it
  correct when `SprintScheduler.AdvanceGraphAsync` has promoted two independent gates at once, the
  case ADR 0058 node-scoped the action ids for.
- The panel keeps passing `null` — "the built-in graph's canonical gate node". Not because it cannot
  determine a node id: `currentDetails.Nodes` is in scope at that call site, the same method already
  indexes nodes individually through `NodeIsReady`, and only a `HumanGate` node is ever
  `awaiting_human` (`SprintScheduler.AdvanceGraphAsync` promotes no other kind), so the first
  `awaiting_human` node **is** a gate node. The reason is that the panel renders exactly **one**
  approve/reject pair regardless of how many gates are pending. With one gate — the only case its
  current UI can express — `null` and that node id address the same decision. With two, any id it
  picked would be an arbitrary choice between them, presented as a definite target: a silent
  mis-targeting in place of today's deterministic canonical-node resolution. What has to change is
  the panel's single-target UI, not its argument, and that is finding A2's job.

Passing the single pending gate's own node id from the panel is nonetheless a reasonable, low-risk
follow-up, and it was deliberately deferred to keep this slice's diff scoped to the new inline
surface — not because it was infeasible. It is deferred rather than taken here because it changes
behaviour on a surface this slice otherwise leaves untouched (a graph whose gate is not the canonical
node would newly resolve differently), and because it is only defensible while the panel shows one
gate; doing it properly means the per-gate rendering finding A2 covers.

`ResolveGateAsync` gained a trailing `string? nodeId = null` parameter so both call sites read as
what they are.

Each inline Approve/Reject button also carries a `SemanticProperties.Description` of
`"<visible text> - <node id>"`. A `Button`'s UIA name is its own `Text`, so concurrent gates — the
scenario this surface newly makes reachable — would otherwise reach assistive technology as several
indistinguishable "Approve"/"Reject" stops. The node id is on screen as a sibling `Label`, but that
is a separate, disconnected stop; folding it into the button's own description is what identifies the
button. The panel's buttons are unchanged: its single-target UI has no such ambiguity.

### Actions are fetched before the timeline renders, without a second round-trip

`RefreshActionsAsync` both fetched and rendered. It is split into `LoadActionsAsync` (fetch into a
hoisted local) and a synchronous `RenderActions` (byte-identical rendering logic), so the refresh
order becomes header → load actions → render timeline → render actions. Exactly one action fetch per
refresh, as before; only the ordering changed.

The 15-second timeline poll does **not** re-fetch actions — it never did — so the inline card
refreshes on mutation and navigation, the same cadence the panel's card already refreshes on. That is
an accepted, documented limitation of the existing poll rather than a regression this slice
introduces, and it is stated in the code beside the card.

### The projection is two-phase, so lazy detail rows are lazy as data, not only as controls

`RenderTimelineItems` rebuilds the entire host on every poll tick, over up to
`SprintTimelineProjector.MaxItemsPerPage` items, each of which may carry up to 50 per-file or
per-call rows (ADR 0059/0060's caps). Building those eagerly would mean tens of thousands of
formatted strings and controls per tick for content that is collapsed and unread.

`TimelineCardProjector` therefore has two entry points rather than one:

- `BuildStats(item, text)` returns just the chip strip. It runs on every render of every row.
- `BuildDetails(item, text)` returns the detail rows and every elision note. It is called **only**
  from the view's first-expansion builder, alongside the lazy control construction, and never on an
  ordinary render.

A single `Build` returning both would have made the per-tick cost the full detail projection — up to
50 records of formatted strings per payload-bearing item — with only the MAUI controls deferred. The
per-item detail container is empty until its existing "Details" toggle is clicked, and both its data
and its controls are produced exactly once per rendered row thereafter. The pre-existing
correlation/causation/arguments text — previously an eager `string.Join` over every argument of every
item on every render — sits inside the same lazy builder, so the whole detail section shares one
discipline rather than two.

The collapsed steady-state cost per payload-bearing item is therefore genuinely one chip strip:
three to five small `Border`s, each holding two `Label`s, over three to five projected
`TimelineStat`s.

### Every elision note survives, not only the last one written

`TimelineCardDetails.ElidedNotes` is a list, not one nullable string. `WorkflowEventPayload` permits
a `diff` family and a `tool_use` family on the same payload, and each caps its own list
independently, so a single slot would let one family's note overwrite the other's — dropping a real
elided-items fact from the UI entirely, which is exactly what ADR 0059/0060's explicit-elision rule
exists to prevent. No producer emits both families on one payload today; the shape permits it, so the
projection and the view both handle it, and a test constructs that payload directly.

## What stays deferred

Each with the reason it is not simply unfinished work:

- **Inline diff hunk content.** Hunks are never persisted (ADR 0059). Showing them requires an
  on-demand git read at render time against the commits the payload names — a separate slice with its
  own cost and failure modes.
- **Raw command text, command output, and test output.** Never captured, and deliberately so: both
  routinely carry secrets (ADR 0006/0060).
- **An error card with a "Retry step" action on a failed tool call.** No per-tool-call failure or
  retry data model exists; `ToolCallStat` carries an outcome, not a re-runnable step. ADR 0058 names
  this same gap when explaining why `stop_current_operation` carries no timeline anchor.
- **Per-tool-call "Allow once / Always allow / Deny".** Ruled out by ADR 0058: it is a decision inside
  a live provider session, needing an interactive provider protocol Forge does not have.
- **Live streaming and token rate.** The timeline is a 15-second-polled durable record, not a stream
  (finding D3).
- **Removing `ContextualActionHost`.** Finding A2 — and see the additive-by-necessity reasoning above.
- **Diff statistics in the sprint header.** Finding C1: the same payload on a different surface, but
  it needs a cross-attempt aggregation rule (what does "changed" mean across retried and superseded
  attempts?) that this slice does not define.
- **Short local timestamps (C5), "Load more" repositioning (D5), a pinned composer (E1), and the
  `ctx X / Y` composer indicator (E3).** E3 in particular is already deferred by ADR 0061 for the
  denominator reason restated above.
- **New Phosphor glyphs.** The three payload types reuse `GitDiff`, `TerminalWindow`, and `Cpu`,
  already declared in `Theme/IconGlyphs.cs`.

## Consequences

- `Forge.Desktop.Presentation` (`TimelineCards.cs`, new): `TimelineStatTone`, `TimelineStat`,
  `TimelineDetailRow`, `TimelineCardDetails`, `TimelineCardProjector` (`BuildStats`/`BuildDetails`),
  `TimelineGateLink`, `TimelineGateLinks`. MAUI-free by construction (this project references only `Forge.Runtime`), which
  is what makes the whole projection unit-testable — ADR 0050: no MAUI control can be instantiated
  headlessly in this suite.
- `Forge.Desktop.Presentation` (`SprintTimelineViewModel.cs`): `TimelineItemView.Sequence`, carried
  through from `SprintTimelineItem.Sequence` (positional, second, mirroring the source record's own
  field order). One construction site.
- `Forge.Desktop` (`WorkspaceShellPage.SprintWorkspace.cs`): the chip strip, the lazy detail section
  (which now calls `BuildDetails` from inside its own first-expansion builder), three new
  `TimelineIconFor` arms, `InlineGateCard` with node-id-bearing button descriptions, the
  `LoadActionsAsync`/`RenderActions` split, and `ResolveGateAsync`'s optional node id.
- `Forge.Desktop` (`WorkspaceShellPage.xaml.cs`): `Decorative<T>`, with `DecorativeGlyph` delegating
  to it.
- `Forge.Runtime` (`Localization/`): 26 new keys in `MessageKeys`, `Messages.resx`, and
  `Messages.ru.resx` — the card labels, the localized `DiffChangeKinds` and `ProviderToolCallKinds`
  vocabularies, and the inline gate heading.
- `tests/Forge.Tests` (`Unit/TimelineCardProjectorTests.cs`, new): `TimelineCardProjectorTests` and
  `TimelineGateLinksTests`. No new accessibility or architecture test: those checks are source scans
  that already enumerate every `WorkspaceShellPage*` file, and this slice introduces no new control
  type (only `Border`/`Label`/`Button`, all already in their scope).
- `tests/Forge.Tests` (`Acceptance/SurfaceParityTests.cs`): one expected substring updated for
  `ResolveGateAsync`'s node-id slot. The property that assertion exists for -- that `confirmed` is the
  dialog's own answer, never a literal `true` -- is unchanged and still asserted.
- No `.xaml` change: every new control is built in code-behind, so
  `ArchitectureTests.HostsDoNotContainHardCodedLabelText` is unaffected.
- No CLI change and no contract change: this slice reads data three shipped contracts already carry.
- `VERSION` moves from `0.85.0` to `0.86.0` (MINOR: additive, no breaking change).

## References

- `docs/plans/desktop-design-parity-review.md` findings D1 and D2 (this slice), and A2, C1, C5, D3,
  D5, E1, E3 (its deferrals)
- ADR 0058 (the node-scoped gate actions and the `TimelineSequence` link this renders)
- ADR 0059 (the `payload` envelope, the diff family, and the honest-totals-plus-explicit-elision rule)
- ADR 0060 (the tool-use family, the drift counter, and why a command carries no target)
- ADR 0061 (the usage family, and why an absent counter and an absent context window are facts to
  render around rather than fill in)
- ADR 0050 (why this projection is a pure class rather than a UI-automation test)
