# ADR 0031: Attempt worktree commit primitive

- Status: Accepted
- Date: 2026-08-20
- Contract version: 1.0.0

## Context

ADR 0030 shipped the second node executor (`planning`) and named the third
(`implementation`) as needing "a git-commit primitive this repository does
not have yet." That gap is total: `IWorktreeManager` (ADR 0004) has
`ExistsAsync`, `CreateAsync`, `IsDirtyAsync`, `ResetHardAsync`,
`GetHeadAsync`, `IntegrateFastForwardAsync`, `RebaseOntoAsync`,
`RemoveAsync`, `DeleteBranchAsync` — nothing that stages and commits
working-tree content. `SprintGitIsolation`'s own class doc has said since
ADR 0004: *"invoke a provider, commit its edits ... is Stage 11's job"* —
two separate responsibilities, with committing explicitly Forge's own
code's job, never something a provider process is trusted to do via its
own shell access inside the worktree.

Every existing test that needs an attempt branch with a real commit past
its base (`GitIsolationTests`'s `Integrate*`/`Rebase*` cases) fakes one
through `GitTestRepository.CommitFileAsync` — a test-only fixture helper —
precisely because nothing in production could produce one.

`planning` (ADR 0030) could legitimately defer this: its whole job is
reasoning, not editing, so its prompt forbids file changes and its
worktree is always discarded unconditionally, needing no commit at all.
`implementation`'s whole job *is* producing a committed diff — deferring
the primitive further would force its own executor slice to either invent
an "isolate but never persist" workaround with no analogue anywhere else
in this codebase, or block entirely. This item builds the primitive alone,
ahead of that executor, matching this stage's own established rhythm:
`SprintScheduler` shipped ahead of Stage 6's executor, `SprintGitIsolation`
ahead of Stage 7's, `RoutingLedger` ahead of Stage 8's, `AttemptSupervisor`
ahead of Stage 11's own.

## Decisions

### `IWorktreeManager.CommitAllAsync` and `SprintGitIsolation.CommitAttemptAsync`

`Task<GitOperationResult> CommitAllAsync(string projectRoot, string path, string message, CancellationToken cancellationToken)`
(`src/Forge.Runtime/Application/Abstractions.cs`), implemented by
`GitWorktreeManager` (`src/Forge.Runtime/Infrastructure/GitWorktreeManager.cs`):
`git add -A` then `git commit --no-verify -m <message>` in `path`, fails
closed (`worktree_commit_failed`, new `DiagnosticCodes` entry) on either
step's non-zero exit, returns the new `HEAD` on success via the class's own
existing `GetHeadAsync`. `SprintGitIsolation.CommitAttemptAsync` is a thin
path-resolution wrapper (`WorktreeLayout.AttemptPath` then delegate),
matching `CreateAttemptWorktreeAsync`'s own shape exactly — no new policy
beyond resolving which worktree.

### Every commit is authored and committed as Forge itself, never the ambient repository identity

`CommitAllAsync` passes `GIT_AUTHOR_NAME`/`GIT_AUTHOR_EMAIL`/
`GIT_COMMITTER_NAME`/`GIT_COMMITTER_EMAIL` (`"Forge"` /
`"forge@localhost"`, a fixed, unconfigurable MVP identity — same "one fixed
MVP policy, no per-project configuration yet" precedent
`ExecutionProfilePolicy`'s own sandbox/permission constants already use) as
environment variables merged onto the inherited environment
(`ProcessRequest.ReplaceEnvironment` defaults to `false`), not a full
replacement — the commit still needs the ordinary inherited environment
(`PATH`, credential helpers) the same way every other `git` invocation in
this class already gets it; only the identity is overridden. Two concrete
reasons this is not merely stylistic: it makes the primitive work
identically in a project whose repository never configured `user.name`/
`user.email` at all (`git commit` would otherwise refuse outright), and it
keeps a Forge-driven commit from being silently misattributed as the
human developer's own authorship, which the ambient `user.name`/
`user.email` would otherwise imply.

### Committing a clean worktree fails closed; it is never this method's job to decide that's a no-op

`CommitAllAsync` does not call `IsDirtyAsync` itself and does not special-case
"nothing to commit" into a different outcome — it fails the same way any
other `git commit` failure does (`worktree_commit_failed`). Whether an
unmodified attempt worktree is meaningful is caller policy, not this
primitive's: a role whose whole job is producing an edit (implementation)
should treat "the provider changed nothing" as its own diagnosed failure
by checking `IsDirtyAsync` before ever reaching this method, not have it
silently absorbed here. `SprintGitIsolation.CommitAttemptAsync`'s own doc
states this explicitly so the next executor slice does not have to
re-derive it.

## Consequences

- New `IWorktreeManager.CommitAllAsync` and `SprintGitIsolation.CommitAttemptAsync`,
  wired nowhere yet — zero production callers, matching every other
  primitive-ahead-of-its-executor precedent in this stage.
- New `DiagnosticCodes.WorktreeCommitFailed` (`worktree_commit_failed`).
- `tests/Forge.Tests/Support/TestEnvironment.cs`'s `FakeWorktreeManager`
  gains a matching in-memory `CommitAllAsync` (records `(Path, Message)`
  pairs, advances a fake `HEAD`, configurable failure) so the next
  executor's own orchestration tests do not need real `git.exe` — the same
  "prove this service's own logic, not its already-tested dependency"
  boundary ADR 0030 already drew for worktree creation.
- `GitIsolationTests` gains three real-`git.exe` cases: committing stages
  both a tracked edit and an untracked new file and is authored as Forge
  regardless of the repository's own configured identity; committing a
  clean worktree fails closed and changes nothing; and a commit made
  through this primitive integrates cleanly via the existing
  `IntegrateAsync` fast-forward path end to end — the same scenario
  `IntegrateFastForwardsTheAttemptsCommitIntoIntegrationAndDiscardsTheAttemptWorktree`
  already covered, now with the real production primitive in place of
  that test's own fixture-level commit.
- The `implementation` node executor itself — prompt assembly inviting
  edits, reading planning's real `Handoff` (still no admitted path through
  `ContextManifest.Layers.Handoffs`, which stays a permanent MVP stub;
  the executor must call `SprintScheduler.GetHandoffsAsync` directly, the
  same "bypass the not-yet-built abstraction" pattern planning already
  used for `.forge/` documents), the dirty-check-then-commit-then-integrate
  sequence, and `RebaseAttemptAsync` recovery on a stale base — remains
  the next slice, explicitly not attempted here.

## References

- ADR 0004 (git isolation and circuit breakers — `SprintGitIsolation`'s own
  "invoke a provider, commit its edits" framing)
- ADR 0016 (provider stdin/environment/bounded-streaming protocol —
  `ProcessRequest.ReplaceEnvironment`'s merge-vs-replace contract this
  item reuses for a non-provider process for the first time)
- ADR 0030 (planning node execution — the sibling executor whose own
  "no commit" scoping this item's context section explains is not
  available to `implementation`)
