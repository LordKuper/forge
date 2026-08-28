# ADR 0069: A sprint's elapsed-time anchor and its on-demand diff statistics

- Status: Accepted
- Date: 2026-08-28
- Contract version: `ProjectWorkspaceSummary` 1.1.0 → 1.2.0 (additive, nullable)

## Context

`docs/plans/desktop-design-parity-review.md` finding C1 wants the sprint workspace header to show
how long a sprint has been working and how much it has changed. ADR 0064 named the second half as
explicitly blocked ("it needs a cross-attempt aggregation rule... that this slice does not define"),
and the execution plan's own audit found the first half had no durable contract at all:
`SprintStatus` and `SprintWorkspaceSummary` carry no timestamp of any kind.

`docs/plans/desktop-design-parity-execution.md` Part 4 answers both — Q8 "wall-clock since the first
attempt started running" and Q9 "a fresh git diff of the integration branch against the base commit,
computed on demand" — and combines S1+S2 into one slice, since both are additive fields on the same
read model feeding the same later rendering slice (S13).

This slice adds the two facts and nothing else. No surface renders either one.

## Decisions

### Both are live projections, not frozen execution-profile values

ADR 0014 freezes what a sprint *was created with* — providers, models, effort, capability
allowlists — and re-resolving any of it mid-sprint is the invariant that ADR keeps ("running sprints
never follow later configuration changes"). Neither fact here is that kind of value. Elapsed time
and a working diff are both questions about what is true *right now*, and each answer changes while
the sprint runs. They belong beside `State`, `StagesCompleted`, and `HasActiveOperation` on
`SprintWorkspaceSummary` — recomputed on every projection — and putting either into
`SprintDefinition.ExecutionProfiles` would mean freezing a number that is wrong one second later.

### Elapsed time is derived from the journal, and adds no persisted field

`SprintJournalEntry` already exposes `CreatedAt` as `Events[0].OccurredAt`, on the stated grounds
that the anchor "is a stable, durable creation timestamp — never re-derived from filesystem
metadata." The start anchor is the same kind of fact on the same already-loaded stream:
`WorkspaceSummaryProjector` folds every sprint's full event list regardless, so
`SprintJournalEntry.FirstAttemptStartedAt` is one scan over data the caller has in hand.

A new persisted field on `SprintDefinition` was rejected for the reason ADR 0014's own deferrals
give for not building speculative infrastructure: it would duplicate a fact the journal already
records, need a tolerant read path for every sprint frozen before it existed, and be one more thing
that can disagree with the event stream. Deriving cannot drift.

**The anchor is the attempt aggregate's `created` transition, not its `running` one.** This is the
one place the implementation reads as narrower than Q8's wording, and it is deliberate. Nothing in
this codebase drives an attempt through `preparing`/`running` *while* it works: every executor calls
`SprintScheduler.StartAttemptAsync` (which appends only `created`) and then
`CompleteAttemptAsync`, which walks the entire remaining path — `preparing`, `running`, `validating`,
terminal — in one call at the end. A `running` event's `OccurredAt` is therefore written
retroactively and sits at the first attempt's *completion*. Reading it would understate elapsed time
by the whole duration of the first attempt, and report nothing at all for a sprint whose first
attempt is still running — precisely the sprint the header most needs to answer for.
`StartAttemptAsync` is what actually starts the work, so its own transition is the honest anchor for
"since the first attempt started running." A unit test pins this against both a later `running`
event and a second attempt's `created` event; if an executor ever records `running` live, that test
and `FirstAttemptStartedAt`'s remarks are the one place to revisit.

**The read model carries a timestamp, not a duration.** No clock is taken anywhere in this slice:
`SprintWorkspaceSummary` reports `FirstAttemptStartedAt` and the eventual renderer subtracts it from
its own "now". A duration baked into a read model is stale the moment it is serialized, and a
timestamp keeps the projection pure and its test free of time control.

### Diff statistics are read fresh from git, never persisted and never cached

ADR 0059 records a `DiffPayload` per attempt, and summing those is the obvious cheap answer — but it
double-counts every file two attempts both touched, and no de-duplication over per-file records can
recover the true insertion/deletion totals for a line edited twice. "The latest integrated attempt
only" answers a different question than the header asks. The sprint's integration branch is the only
artifact that actually holds the answer, and git already maintains it, so
`SprintGitIsolation.ReadIntegrationDiffStatAsync` reads `WorktreeLayout.IntegrationBranch`'s current
tip against `SprintDefinition.BaseCommit` on demand.

The tip is resolved through `GetHeadAsync` rather than passed as a branch name, because
`IWorktreeManager.DiffStatAsync` accepts only canonical full-length object ids and rejects a ref name
outright as `worktree_commit_invalid`. The integration worktree is checked out on that branch, so its
`HEAD` *is* the branch tip.

**Nothing is cached, because no caching convention would apply.** The one comparable expensive read
in this projection — provider toolchain health — is not cached either; ADR 0052/0061's own guidance
(`ProviderQuotaProjector`'s second overload) is about not paying for the *same* probe twice within
one render pass, not about holding a result across passes. Here there is nothing to share: each
sprint's diff is its own read, taken once per projection. A cross-call cache would also be actively
wrong, since the number changes on every integration and the header exists to show it moving.

The cost is bounded per sprint rather than by luck. Only non-terminal sprints reach the read at all.
A sprint that has never run has no integration worktree directory, and `GitWorktreeManager`'s own
directory-existence guard short-circuits before starting a process — so that case costs zero `git`
invocations, which is the common case for a draft or ready sprint. A sprint that has run costs three
short reads (`rev-parse`, `--numstat`, `--name-status`).

**Per-sprint boundedness is not the cost that matters, so the read is opt-in.** The first revision of
this ADR compared one sprint's read to the whole per-project call, which is the wrong baseline:
`GetWorkspaceSummaryAsync` is called once per cataloged project, in a sequential loop, by both
`SidebarViewModel.LoadAsync` and `forge workspace summary`. The real added cost of computing this
unconditionally is `projects × active sprints` serialized process spawns per refresh — and
`StartupPipeline.RunAsync` spawns no `git` at all, so it would have made a project's summary a
process-spawning call for the first time. Worse, the sidebar's rows are the lighter
`SidebarSprintItem` projection (PR #122), which carries no diff at all, so that surface paid the
whole fan-out for a value it discards.

`CreateAsync` therefore takes an `includeDiffStats` flag, mirrored on `ForgeApplication.GetWorkspaceSummaryAsync`
and on the wire as `GetWorkspaceSummaryRequest.IncludeDiffStats` (defaulted `false`, so an absent or
older payload gets the pre-ADR-0069 row). Left off, the read model reports `diff_stat` absent and no
`git` process is started — which is what every caller that fans out over a catalog on a render, or
that does not draw the number, asks for. `forge workspace summary --json` opts in, because that
contract reports the field and it is one explicit invocation rather than a refreshing view. The
opt-in is scoped to the output mode, not to the command: the same command's human-readable table
prints state, stage, progress, and active operation and never `diff_stat`, so it passes `false` and
pays nothing (PR #126 review finding 5). The Desktop sidebar and the sprint header both stay opted
out until S13 adds the control that draws the value (PR #126 review finding 2).

### Absent and zero are different answers

`DiffStat` is `null` both when the sprint has no integration worktree yet and when the git read
failed, and those two are deliberately reported identically: a header can only say "not available"
for either, and no caller has an action that differs between them. Substituting zeros would assert
that the sprint changed nothing, which is a claim, not an absence — the same distinction ADR 0061
draws between an unreported counter and a reported zero, and ADR 0064 refuses to collapse.

The read never throws and never surfaces a diagnostic code. Every failure mode — no worktree, a
worktree deleted mid-read, an unresolvable base commit, a `git` failure — collapses to `null`,
matching the "one unreadable input never fails the whole fan-out" posture `WorkspaceSummaryProjector`
already applies to an unreadable project row and ADR 0005 applies to diagnostic-bundle collection.

**That guarantee lives in the projector, not in `SprintGitIsolation`.** A result code cannot express
a `git` that never started: `Process.Start` throws `Win32Exception` when the binary is missing from
the Desktop host's `PATH`, replaced mid-session, or blocked by policy, and no `SprintGitIsolation`
method converts that into a failure code. `CreateAsync`'s own catch filter
(`IOException or UnauthorizedAccessException or InvalidDataException or FormatException`) does not
cover it either, so such an exception escaped `GetWorkspaceSummaryAsync` entirely and propagated
through `SidebarViewModel.LoadAsync`'s per-project loop — one machine without `git` took down the
whole sidebar instead of blanking one optional field. `WorkspaceSummaryProjector.ReadDiffStatAsync`
now wraps the read in the same fail-open guard `ImplementationExecutionHostedService.TryReadDiffStatAsync`
uses for the attempt-level read (PR #116 finding 1), rethrowing only a cancellation of the caller's
own token. Even for the exceptions the outer filter does cover, that guard is the correct outcome: an
optional read must null one field, not degrade the project row to `internal_error` (PR #126 review
finding 1).

### `SprintDiffStat` carries three totals, not `DiffPayload`

`DiffPayload` would have been free to reuse and would hand a later popover slice its file list. It is
not used, because `SprintWorkspaceSummary` is a *bounded* row by design — the type's own summary says
so twice — and that payload's per-file list (up to `GitWorktreeManagerDiffStatBudget.MaxFiles`
entries, per active sprint, per project, over the control-plane wire) is weight no reader of this row
needs for `3 files, +120 −8`. A surface that wants the file list (finding C3's working-diff popover,
S14) reads it for one sprint on its own. `Insertions`/`Deletions` remain totals over every changed
file including elided ones, so ADR 0059's honest-totals rule survives the narrowing.

## What stays deferred

- **Rendering either fact.** Finding C1's header stats and progress bar are slice S13. No
  `SprintStatusHeaderData` field is added here: an unrendered field on a presentation record would be
  the speculative, uncalled infrastructure ADR 0014 removed twice, and S13 adds the field together
  with the control that draws it, from `SprintWorkspaceSummary` — which is already what
  `SprintStatusHeaderProjector` reads.
- **The human-readable `forge workspace summary` line.** Unchanged; what belongs in that fixed-width
  line is a presentation decision with no finding behind it.
- **Per-file diff content in the header.** Finding C3 / slice S14, which needs the file list this
  row deliberately does not carry.
- **Summed active attempt time** (Q8 option (c)) and **per-attempt diff aggregation** (Q9 options
  (a)/(b)). Both were considered and rejected in Part 4; the reasoning above records why they stay
  rejected in code.

## Consequences

- `Forge.Runtime` (`Application/SprintJournal.cs`): `SprintJournalEntry.FirstAttemptStartedAt`, a
  pure derivation beside `CreatedAt`. No persisted field, no schema change, no read-tolerance path.
- `Forge.Runtime` (`Application/SprintGitIsolation.cs`): `ReadIntegrationDiffStatAsync`, the
  sprint-level counterpart to the existing attempt-level `ReadDiffStatAsync`, sharing its
  `Sanitize` path so every `DiffPayload` this class hands out stays path-safe and redacted.
- `Forge.Runtime` (`Application/WorkspaceSummary.cs`): new `SprintDiffStat`; two new
  `SprintWorkspaceSummary` members; `ProjectWorkspaceSummary.ContractVersion` 1.1.0 → 1.2.0;
  `WorkspaceSummaryProjector` takes `SprintGitIsolation` (already a registered singleton) and gains
  one private fail-open read. One construction site, reviewed as ADR 0057/0058 require.
- `WorkspaceSummaryProjector.CreateAsync` and `ForgeApplication.GetWorkspaceSummaryAsync` take a
  required `includeDiffStats`; `forge workspace summary` passes `true` only for `--json` and `false`
  for its human-readable table, while `SidebarViewModel.LoadAsync` and
  `SprintWorkspaceViewModel.RefreshHeaderAsync` always pass `false`. Pre-1.0 signature replacement,
  no alias kept (repository rule).
- No JSON schema change: `ProjectWorkspaceSummary` has no schema under `docs/contracts/v1/schemas/`
  — it is serialized reflectively by `StatusJson` and versioned only by its own `ContractVersion`
  constant. Both new members are additive and nullable, so an older reader is unaffected; pre-1.0,
  that additive change needs no version bump beyond the constant itself.
- Control protocol: `get_workspace_summary`'s response is the same record, two fields wider; its
  request gains one optional `include_diff_stats` field defaulting to `false`, so a client that sends
  no payload — every client today — is unaffected.
- `forge workspace summary --json` reports both fields; the command's human-readable output is
  unchanged and computes no `diff_stat`. No Desktop surface computes it in this slice.
- `tests/Forge.Tests` (`Unit/WorkspaceSummaryTests.cs`): two pure tests for the elapsed anchor (no
  attempt yet → absent; the first attempt's start, pinned against both a later retroactive `running`
  event and a second attempt) and four for the diff stat, using the existing `FakeWorktreeManager` —
  real `git.exe` `--numstat` behaviour already belongs to `GitIsolationTests`. The diff-stat tests
  cover the wiring across both states (absent before an integration worktree exists, the fake's stat
  after, asserted against the recorded worktree path / base commit / tip — with the integration head
  advanced past the base first, so the two commit assertions cannot both hold for a swapped or
  self-referential diff), the opt-out (no `DiffStatAsync` call at all), a `git` read that reports
  failure, and a `git` that cannot be launched (`Win32Exception`) — the last two both asserting the
  project row around the absent field is still fully populated. The elapsed anchor is additionally
  asserted against a real `SprintScheduler.StartAttemptAsync` (in
  `AnActiveOperationIsReportedWithoutLoadingTheSprintsTimeline`), so its derivation is pinned to the
  events production writes rather than only to synthetic ones. No test for the
  `SprintWorkspaceSummary` field pass-through itself: it is straight plumbing with no branch, and the
  tests above read it end to end anyway.
- `tests/Forge.Tests` (`Acceptance/WorkspaceCliTests.cs`): one test pinning the output-mode scope of
  the opt-in — the plain `forge workspace summary` reaches no `DiffStatAsync` while `--json` does,
  both against an integration worktree that exists.
- `VERSION` moves from `0.87.0` to `0.88.0` (MINOR: additive, no breaking change).

## References

- `docs/plans/desktop-design-parity-execution.md` Part 3 slice S1+S2 and Part 4 decisions Q8/Q9
- `docs/plans/desktop-design-parity-review.md` finding C1 (this slice's data half), C3 and S13 (its
  deferrals)
- ADR 0014 (what is frozen at sprint creation, and why neither of these is)
- ADR 0059 (the attempt-level `DiffPayload`, the honest-totals rule, and the per-file cap)
- ADR 0064 (which named the cross-attempt aggregation rule as C1's blocker)
- ADR 0061 (why an absent value and a reported zero must not be collapsed)
