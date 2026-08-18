# ADR 0024: Best-effort local notifications

- Status: Accepted
- Date: 2026-08-18
- Contract version: 1.0.0

## Context

Stage 11's plan text (`docs/plans/implementation-plan.md` P11.67-P11.72):
"Add best-effort local notifications for `awaiting_human`, `blocked`,
`failed`, and `completed`, deduplicated from journal event ids and
redacted." ADR 0005 already committed to the shape this must take:
"Desktop/OS notifications project durable `awaiting_human`, `blocked`,
`failed`, and `completed` events. They are best-effort, user-configurable,
redacted, and deduplicated by event id. A notification is never the
authoritative record and a delivery failure never changes workflow state.
Network notification channels and custom scripts remain outside the MVP."

Most of the projection half of this item was already built and tested
under an earlier stage: `NotificationProjector` (`src/Forge.Runtime
/Application/NotificationProjector.cs`) already maps `ControlEventRecord`s
onto the four `NotificationKind` values, with its own doc comment noting
"actual delivery (toast, tray, sound) is a platform-owned concern for a
later stage to add." This ADR is exactly that later stage — the delivery
half, plus the config gate and dedup mechanism the projection layer
presumes exist.

Stage 11 P11.56-P11.66 remains its own separate, still-open item (ADRs
0019-0023) with substantial scope left (navigation shell, `sprint.manage`
Desktop controls, `forge sprint rebase`, ICU localization, accessibility).
This ADR picks P11.67-P11.72 instead — a distinct, well-specified plan
item with concrete, already-partially-built acceptance criteria, not
blocked on any of P11.56-P11.66's own open work.

## Decisions

### Dedup reuses `ControlEventsCursor`, not a new watermark concept

`forge events --cursor` already has exactly the durable, gap-safe resume
mechanism "deduplicated by event id" needs: `ControlEventsReader.ReadAsync`
advances a per-sprint sequence watermark only through a contiguous run, so
an event is delivered to a cursor holder exactly once, in order, even
across process restarts. `NotificationDeliveryHostedService` persists its
own opaque cursor token (`NotificationDeliveryCursorStore`, a plain
base64-string file at `.forge/notifications/cursor.json`) and simply reads
forward from it every tick — no separate "delivered event id set" is
invented, since the cursor already provides the same guarantee
`NotificationProjection.EventId`'s own doc comment names ("a caller that
already delivered this id skips it").

`NotificationDeliveryCursorStore` deliberately does not use
`AtomicConfigurationFile`'s full crash-durability machinery (fsync'd
directory flush, `.previous` fallback, internal to `Forge.Configuration`
with no path for a `Forge.Host.Runtime` caller to reach it anyway) — a
temp-file-then-rename is enough. ADR 0005 already frames the whole feature
as best-effort: losing a cursor write to a genuine crash costs, at worst,
one already-seen sweep's worth of re-delivered notifications on restart,
an accepted, bounded cost.

A cursor that fails to decode (`ReadControlEvents`'s own `ControlCursorStale`
diagnostic) is not retried forever with the same broken token — this
service's own cursor should never actually become unreadable except a rare
partial-write race, and retrying the same broken token indefinitely would
permanently wedge that project's notifications. It resets and resumes
cleanly from now, skipping only that one tick's delivery — never replaying
the project's full history as "new." Getting this right needs one more step
than it first appears: `ControlEventsPage.Empty`'s own fresh-anchor token
has *empty* watermarks, and an empty watermark does not itself mean "skip
everything already delivered" — it means "nothing has been seen yet,"
which is the opposite of "resume from now." Persisting that token as-is
(the first version of this ADR's own mistake, found in round 1 review — see
below) makes every historical event look unseen again, replaying the
project's entire history as new notifications on the very next tick.
`CatchUpToNowAsync` is the actual fix: it reads forward from a fresh
anchor, bounded to `MaxCatchUpReads` reads, discarding every event without
delivering it, until a read makes no further progress (the returned cursor
equals the one just used) — i.e., the journal's current tip — and only
that caught-up cursor is what gets persisted. Round 2 review found the
first version of this loop instead stopped once a page returned fewer than
`ControlEventsReader.MaxEventsPerRead` *deliverable* events, which
undercounts whenever `ReadControlEvents` withholds an event behind a gap
(see "Round 2 review" below) — fixed to the cursor-equality condition
described here.

### `notifications.enabled` gates delivery, but the cursor advances regardless

A new `User`-scope configuration key, default `true`, added to
`ConfigurationRegistry`/`ConfigurationSchemaCodec`/`user-config.schema
.json` (bumping `UserContractVersion` to `1.2.0`) the same way
`interaction.confirm_destructive` was — satisfying ADR 0005's
"user-configurable." It is optional and nullable in the schema DTO
(`UserNotifications?`, not required), matching `providers.enabled`'s own
shape rather than `interaction`'s: an on-disk document written before this
key existed must still validate on read with it entirely absent, the exact
backward-compatibility property the schema's own "an older document...
still validates... and is silently upgraded the next time it is saved"
comment already promises for `providers`.

While disabled, `NotificationDeliveryHostedService` still reads and
advances its cursor every tick — it only skips the `INotificationService
.NotifyAsync` calls. Without this, re-enabling the key later would deliver
every notification-worthy event accumulated while disabled as a single
burst, which defeats the purpose of muting in the first place.

### The delivery port lives in `Forge.Runtime`; the OS call lives in an adapter

`INotificationService.NotifyAsync(title, body, cancellationToken)` is a
new neutral port in `Forge.Runtime`, matching ADR 0007's table exactly:
"Notification policy and durable attention events" (the projector, the
cursor, the config gate, the redaction, all of which live in
`NotificationDeliveryHostedService`) is cross-platform; "OS notification
delivery" is the one adapter-owned piece. `AddForgeCore` registers a
`NullNotificationService` default (`TryAddSingleton`, mirroring
`IPlatformPreflight`'s own `UnsupportedPlatformPreflight` default) that
silently discards — "no OS adapter installed" is not itself a delivery
failure worth logging on every tick.

`NotificationDeliveryHostedService` lives in `Forge.Host.Runtime`, not
`Forge.Desktop` — ADR 0005/0006 already frame notifications as a Host-plane
concern reading the same durable state Desktop and CLI both already share,
not something owned by the MAUI app specifically. It runs whether or not
Desktop is open, matching `ResumeSchedulerHostedService`'s own precedent
exactly: registered as a singleton (not `AddHostedService`),
`ControlPlaneHostedService` starts it only after winning the project lease
and stops it before releasing that lease, so a Host that loses the lease
race never ticks against durable state it does not own.

### `WindowsNotificationService` uses `NotifyIcon.ShowBalloonTip`, not the Windows App SDK

The modern, Microsoft-first-party way to send a local Windows notification
is `Microsoft.Windows.AppNotifications` (`AppNotificationManager
.Default.Register()`/`AppNotificationBuilder`) — the legacy
`Microsoft.Toolkit.Uwp.Notifications`/`CommunityToolkit.WinUI.Notifications`
package this superseded was archived shortly before this ADR was written.
Both are built for a primary UI application with its own message pump and
`OnStartup`/`Main` registration ceremony (WPF/WinForms examples throughout
Microsoft's own documentation); Forge Host is a lightweight, headless
background process with no such loop.

`System.Windows.Forms.NotifyIcon.ShowBalloonTip` is chosen instead:
Windows 10/11 render a balloon-tip call from the shell as a standard
Action Center toast automatically, so it needs neither a message pump nor
an unpackaged-app COM/AUMID registration step, and needs no new NuGet
dependency at all — `System.Windows.Forms` is a framework reference
(`UseWindowsForms=true` in `Forge.Runtime.Windows.csproj`) already part of
the Windows Desktop shared framework this project's TFM pulls in. The
accepted trade-offs, named honestly rather than silently assumed away:

- **No click-activation or argument routing back into Forge.** Neither is
  named as in scope by ADR 0005's own wording ("best-effort" local
  notifications) or by this item's plan text.
- **A small, persistent tray icon while a project's Host process runs.**
  `ShowBalloonTip` is a no-op while `NotifyIcon.Visible` is `false`, and
  toggling visibility off immediately after each call risks racing the
  shell's own asynchronous balloon rendering — so the icon stays visible
  for the service's own lifetime rather than flickering per notification.
- **Delivery correctness cannot be verified by this repository's own test
  suite or by an independent review agent.** No interactive desktop
  session exists in CI (or, realistically, in most automated review
  environments) to confirm a balloon tip actually renders. Every other
  piece of this feature — the cursor, the projector, the config gate, the
  redaction, the per-notification failure isolation — is fully covered by
  `NotificationDeliveryHostedServiceTests` against a fake
  `INotificationService`; `WindowsNotificationService` itself is not, and
  that gap is named here rather than left implicit.

### The first sweep waits one interval rather than ticking immediately

`ResumeSchedulerHostedService.ExecuteAsync` ticks immediately on start,
then waits — a real correctness choice for that service, since a node left
stuck with a satisfied dependency should be promoted as soon as possible
after the Host that can fix it comes up.
`NotificationDeliveryHostedService` has no equivalent urgency: a
best-effort notification arriving one `PollInterval` late on Host startup
is a non-issue, so `ExecuteAsync` waits for the timer's first tick before
ever calling `TickAsync` — the opposite order.

This was not a stylistic preference: repeated full-suite local runs during
this ADR's own validation showed an intermittent failure (roughly 2 of 5
runs) in unrelated `ControlPlaneTests` round-trip tests that never
reproduced when run in isolation or before this feature existed — the
signature of resource contention across many concurrently-started test
`ControlPlaneHost` instances, not a logic defect. Every real
`ControlPlaneHost.StartAsync` call in the test suite now also starts a
`NotificationDeliveryHostedService`, and the immediate-tick design meant
every one of those Host starts synchronously performed an extra
`ControlEventsReader.ReadAsync` call plus cursor file I/O at the exact
moment the test suite already has the most concurrent Host-startup load.
Deferring the first tick removes that synchronous burst from Host startup
entirely; four repeated full-suite runs after this change were clean,
against two flaky runs (out of five) before it. The underlying
`ControlPlaneTests` timing sensitivity under heavy parallel load is a
pre-existing property of those tests' own real-named-pipe design, not
something this ADR claims to have fully solved — only to have stopped
measurably contributing to.

### The notification body is redacted even though nothing in it is secret today

The composed body is `"{NotificationSprintLabel} {sprintId:D}"` — a fixed
label plus a GUID, never plausibly matching `SecretRedactor`'s private-key/
authorization/credential-URI patterns. `SecretRedactor.Redact` is still
called on it, satisfying ADR 0005's "redacted" requirement literally and
defensively: if a future change ever composes a richer body (a finding
summary, an instruction excerpt), it is protected automatically rather
than requiring someone to remember to add redaction later.

### Title text is localized through the same resolved `language.ui` every surface reads

`ReadNotificationSettingsAsync` reads both `notifications.enabled` and
`language.ui` from one `ForgeApplication.GetUserConfigurationAsync` call —
the same resolved, default-applied `ConfigurationView` every other surface
already uses to read a user setting. The first version of this ADR instead
called `ForgeApplication.GetStartupStatusAsync` just for its `Language.Ui`
field, which round 1 review found ran the Host's entire startup pipeline
(platform, provider, and project-root checks included) on every tick with
something to deliver, purely to reach one already-resolved value one line
away in `GetUserConfigurationAsync`'s own result. Fixed by reading it from
there directly instead — no separate call, and no full startup pipeline
run just for a language tag. Five new message keys (four titles, one
`NotificationSprintLabel`) are added to `MessageKeys`/`Messages.resx`/
`Messages.ru.resx`.

### Round 1 review: one high-severity defect, four further fixes

Independent review found five issues, all fixed:

1. **(High) The stale-cursor recovery replayed the project's full
   history**, described above under "the cursor advances regardless" —
   the single most serious finding in this ADR's own history. Reproduced
   with a temporary probe (deliver once, corrupt the cursor, restart,
   confirm the same event redelivers) before the fix, then confirmed fixed
   after it; a permanent regression test
   (`AStaleCursorRecoversWithoutReplayingAnAlreadyDeliveredEvent`) proves
   it the same way — verified to fail against the pre-fix code and pass
   against the fix.
2. **`docs/contracts/v1/configuration.json` was never updated** for the
   new `notifications.enabled` key, and its own `contract_version` stayed
   at `1.1.0` — an omission nothing in the test suite caught. Fixed by
   adding the key and bumping to `1.2.0` (matching `UserContractVersion`),
   and by closing the enforcement gap itself: a new
   `ConfigurationRegistryMatchesTheContractsKeyList` test now proves, in
   both directions, that every key `ConfigurationRegistry` registers is
   documented here and every key documented here is registered — with
   matching scope, session-override, inheritance, and default value — so
   this specific drift cannot recur silently.
3. **`NotificationDeliveryCursorStore.SaveAsync` leaked its own `.tmp`
   file** on a write or rename failure — one per failed tick, forever,
   under a persistent failure. Fixed with a try/catch that deletes the
   temp file before rethrowing the original exception unchanged.
4. **Reading settings via `GetStartupStatusAsync` sat outside this
   service's own tick-level failure isolation** — an escaping exception
   there would have faulted `ExecuteTask` silently and permanently, since
   this service is a plain singleton `BackgroundService`, not registered
   via `AddHostedService`, so nothing observes that fault the way the
   generic host would. Fixed by folding settings-reading into the same
   try/catch that already wraps the cursor load and events read (see the
   language-resolution fix above, which also removed the call entirely).
5. **Two of the six original tests proved less than their names claimed.**
   The delivery-failure-isolation test used one event and asserted only
   `File.Exists` on the cursor path — neither proves per-item isolation
   (a single event can't) nor that the cursor genuinely advanced (a file
   existing says nothing about its content). Fixed by rewriting it with
   two sprints, one whose delivery is made to fail and one that succeeds,
   asserting the failing one is skipped, the succeeding one still
   delivers, and the cursor's own decoded watermark advances past *both*.
   The original stale-cursor test (superseded by finding 1's own
   regression test, which proves the stronger, actually-meaningful
   property) was removed rather than kept alongside a strictly weaker
   duplicate.

### Round 2 review: round 1's own settings-isolation fix reintroduced round 1's own bug, plus five further findings

Independent review found six issues. The first is a direct echo of round
1's finding 4: fixing that finding folded settings-reading into `TickAsync`'s
try/catch, but the stale-cursor branch's own `CatchUpToNowAsync` call —
added in the *same* round 1 commit — was left outside it. All six fixed:

1. **(High) `CatchUpToNowAsync` ran outside `TickAsync`'s own failure
   isolation** — up to `MaxCatchUpReads` (1,000) journal reads with no
   exception handling at all, reached only when the cursor is already
   corrupt, so a single transient `IOException` there would have
   permanently faulted `ExecuteTask` with nothing logged and nothing else
   observing the fault (this is a plain singleton `BackgroundService`, not
   `AddHostedService`). Fixed by restructuring `TickAsync` into one
   try/catch wrapping the cursor load, the events read, stale-cursor
   recovery (catch-up included), settings resolution, and delivery —
   matching `ResumeSchedulerHostedService`'s own whole-tick-wrapped shape
   exactly, the same standard round 1's fix should already have applied
   everywhere.
2. **`CatchUpToNowAsync`'s own stopping condition was wrong.** It compared
   `page.Events.Count` against `ControlEventsReader.MaxEventsPerRead`, but
   `Events` is only the *deliverable* subset of a read — `ReadControlEvents`
   itself withholds events stranded behind a gap (see `ControlEvents.cs`'s
   own "deliver exactly once, once its predecessor closes the gap"
   comment). A full 500-event page with some withheld could report fewer
   deliverable events than the bound, ending the catch-up loop early with
   history still unread — silently reintroducing a smaller version of
   finding 1's own replay bug. Fixed by terminating on cursor-value
   stability instead (`page.Cursor == cursor`, i.e. no further progress
   was possible on this read), which correctly reflects
   `ReadControlEvents`'s own advancement guarantee regardless of the
   deliverable/pending distinction.
3. **An unreadable configuration was treated as "enabled."** `ConfigurationView.DiagnosticCode`
   was never checked; an unreadable document resolves to an empty
   `Values` list, and the `!= JsonValueKind.False` check on a missing key
   defaults to `true` — silently overriding a genuine
   `notifications.enabled=false` the user had already set during a
   transient config-read glitch, the one case ADR 0005's
   "user-configurable" promise cannot tolerate getting wrong. Fixed:
   `ReadNotificationSettingsAsync` now fails closed (`Enabled: false`)
   whenever `DiagnosticCode != DiagnosticCodes.None`.
4. **The `.tmp`-cleanup fix from round 1 could itself throw and replace
   the original exception** it was written to preserve — `File.Delete`
   failing (the file was never created, or is now locked) inside the
   cleanup `catch` block would propagate instead of the real failure.
   Fixed with a nested try/catch around the delete alone, so `throw;`
   always rethrows what the outer `catch` actually caught.
5. **The round-1 contract-consistency test omitted `sensitive`** — the one
   field with an actual security consequence if it ever silently
   diverged. Fixed by adding it to the per-key comparison.
6. **The "disabled" test's cursor-advance assertion still proved less than
   it claimed**: `watermark >= 0` is true for any watermark, including a
   stale one nowhere near "caught up." Fixed by computing the ground-truth
   watermark independently — reading this sprint's own full event history
   directly via `ControlEventsReader`, bypassing the service under test —
   and asserting the disabled tick's persisted watermark equals it
   exactly, not merely that some non-negative number exists.

### Round 3 review: the same "does the try/catch actually cover everything reachable from `TickAsync`" defect class, a third time

Independent review found five issues, all fixed:

1. **(High) `InvalidDataException` still escaped `TickAsync`'s catch
   filter** — the third instance in this PR's own history of the same
   underlying defect class round 2 finding 1 fixed: a code path reachable
   from `TickAsync` not actually covered by its try/catch.
   `FileSprintEventLog.LoadValidatedEventsAsync` throws
   `InvalidDataException` for a corrupt journal, reached via
   `ControlEventsReader.ReadAsync` the same way `ResumeSchedulerHostedService`
   already reaches it — that service's own catch filter already includes
   this type, and this service's doc comment already claimed parity with
   it, but the filter itself omitted it. Fixed by adding
   `InvalidDataException` to the filter; a regression test
   (`ACorruptJournalDoesNotPermanentlyFaultTheService`) appends invalid
   JSON to a sprint's own event log and asserts `ExecuteTask` stays
   unfaulted.
2. **The fail-closed config-read path (round 2's fix) logged nothing**,
   making a genuine transient failure indistinguishable from "the user
   disabled notifications" from the logs alone — undiagnosable in
   practice. Fixed by adding a `LogSettingsUnreadable` warning log to that
   branch, naming the diagnostic code; a new
   `AnUnreadableConfigurationFailsClosedAndStillAdvancesTheCursor` test
   proves the end-to-end behavior (no delivery, cursor still advances to
   the same watermark a healthy read would reach).
3. **`ADeliveryFailureIsIsolatedAndTheCursorStillAdvancesPastBothEvents`
   still used `watermark >= 0`** — the exact pattern round 2 finding 6
   rejected in the sibling "disabled" test, left unfixed here. Fixed by
   extracting the ground-truth comparison into a shared
   `ReadGroundTruthWatermarkAsync` helper and using it for both sprints in
   this test, matching the "disabled" test's own rigor.
4. **`NotificationDeliveryCursorStore.SaveAsync`'s cleanup path — rewritten
   by both prior review rounds — had zero tests**, against AGENTS.md's
   regression-test rule. Fixed with a new
   `NotificationDeliveryCursorStoreTests.SaveAsyncCleansUpItsTempFileWhenTheFinalMoveFailsAndPropagatesTheOriginalException`
   unit test: pre-creating a directory at the destination cursor-file path
   forces the final `File.Move` to fail against a genuinely-written temp
   file (not merely "nothing to clean up"), asserting both that the
   original exception propagates and that no `.tmp` file remains. On this
   Windows environment, that failure surfaces as
   `UnauthorizedAccessException`, not `IOException` as originally assumed
   when the fix was written — both are already in the hosted service's own
   catch filter, so the delivery-side behavior was never wrong, only the
   test's assumed exception type. (Round 4 review found this exact-type
   assertion was itself not portable — see "Round 4 review" below.)
5. **`MaxCatchUpReads`'s doc comment and `LogCatchUpBoundReached`'s log
   message both inaccurately described unresolved events as later being
   "caught up on."** They are not: once the bound is hit, the persisted
   cursor reflects only what catch-up actually advanced through, and any
   remaining events are delivered by a later tick as ordinary *new*
   notifications — from that tick's perspective there is no "catch-up"
   happening at all. Fixed by rewording both to state this precisely.

### Round 4 review (critical-only): round 3's own new test was not portable

Round 3 completed AGENTS.md's cap of three full-scope rounds; round 4 is
critical-findings-only. One critical issue found and fixed:

1. **(Critical) The round-3 cursor-cleanup test broke the portable CI job on
   Linux and macOS.** `SaveAsyncCleansUpItsTempFileWhenTheFinalMoveFailsAndPropagatesTheOriginalException`
   asserted an exact `UnauthorizedAccessException` type, confirmed only by
   running it locally on Windows. Moving a file onto an existing directory
   raises `IOException` (`EISDIR`) on Linux/macOS instead — confirmed from
   the actual CI failure log for the round-3 commit, not inferred:
   `Validate portable core (ubuntu-24.04)` failed with
   `System.IO.IOException : Is a directory`, and the `macos-14` job was
   cancelled by fail-fast. This violated AGENTS.md's "build and test neutral
   code on Windows, Linux, and macOS" and "pass all checks before review"
   rules — a genuine gap this repository's own local (Windows-only)
   validation could never have caught, exactly the class of check an
   independent review pass exists to add. Fixed by asserting the thrown
   exception is one of the two types the hosted service's own catch filter
   already accepts (`IOException or UnauthorizedAccessException`) instead
   of one exact type: the platform difference is real and expected, and the
   test's actual job — proving both are handled without leaking a `.tmp`
   file — holds on every OS either way.

The same round re-verified, from scratch, that every code path reachable
from `TickAsync` is covered by its try/catch (the recurring round-1→2→3
defect class) and found no fourth instance; it also re-checked test
soundness, secret/PII exposure, and contract-version consistency across the
full feature diff, all clean.

### Round 5 review (critical-only): a delivery-signal-only wait raced the cursor save

Round 5 confirmed round 4's fix directly against CI (the exact
`ubuntu-24.04`/`macos-14` jobs that had failed now passed on the round-4
commit), then found one further critical issue — the same underlying shape
as round 4's own finding: a test that passes reliably on a dev box but is
not reliable in CI. Fixed:

1. **(Critical) `ADeliveryFailureIsIsolatedAndTheCursorStillAdvancesPastBothEvents`
   raced `TickAsync`'s own cursor save.** `WaitForDeliveryAsync` returns as
   soon as the fake records a delivery — a point strictly *inside*
   `TickAsync`'s delivery loop, before it reaches `SaveCursorAsync`. The
   test's own `finally` then calls `StopAsync` immediately, cancelling the
   stopping token; if that cancellation lands inside
   `NotificationDeliveryCursorStore.SaveAsync`'s `File.WriteAllTextAsync`,
   the resulting `OperationCanceledException` propagates out of
   `TickAsync` (a cancellation the tick's own catch filter intentionally
   rethrows rather than swallows), `ExecuteAsync` ends canceled rather than
   faulted, `service.ExecuteTask!.Exception` stays null (so the existing
   assertion doesn't catch it), and `cursor.json` is never written — the
   test's own `ReadCursorAsync` call then throws `FileNotFoundException`.
   Reproduced directly on the round-4 commit, both in a single plain full
   suite run and repeatedly under contention, not merely theorized. Fixed
   with a new `WaitForCursorAsync` helper that polls the persisted cursor
   file itself until both sprints' watermarks are actually present, used in
   place of relying on the delivery signal alone before this test calls
   `StopAsync` — this closes the race regardless of platform or scheduling,
   rather than merely widening the existing fixed-delay tolerance the way
   the sibling tests happen to (safely, but only by chance) avoid it.

### Round 6 review (critical-only): round 5's own new poll helper raced the same writer it was polling

Round 6 confirmed CI stayed green on the round-5 commit, reran the affected
test file 27 consecutive times locally with zero failures, then found one
further critical issue — a sixth instance of the "passes locally, not
reliably under CI-shaped load" pattern, this time in the very helper round
5 added to fix the fifth instance. Fixed:

1. **(Critical) `WaitForCursorAsync` is the only cursor read in this file
   that runs WHILE the service is still ticking** — every other read
   happens after `StopAsync` (which awaits `ExecuteTask`), so has no
   concurrent writer. `TickAsync` calls `SaveCursorAsync` on every tick
   regardless of whether anything happened, so `NotificationDeliveryCursorStore.SaveAsync`'s
   own `File.Move(overwrite: true)` can be replacing `cursor.json` at the
   exact moment this helper's unguarded `File.ReadAllTextAsync` reads it —
   on Windows the replacing rename briefly opens the destination for
   delete, and a read landing in that window throws `IOException`, failing
   the test outright before its retry loop gets a chance. Reproduced
   empirically at this file's actual 50 ms poll cadence: 0.075% per read
   under low contention, 2.9% under CI-shaped heavy contention — load-
   dependent, exactly why it would surface on a busy runner and not on a
   quiet dev box. Fixed by wrapping the read in the identical
   `catch (Exception error) when (error is IOException or UnauthorizedAccessException)`
   tolerance `NotificationDeliveryCursorStore.LoadAsync` itself already
   uses for this exact file — a transient mid-rename read simply retries
   on the next poll, matching production's own established tolerance for
   reading a file this service writes to concurrently.

### Round 7 review (critical-only): the fixed polling/delay budgets themselves were load-sensitive

Round 7 confirmed CI stayed green on the round-6 commit (one transient,
unrelated `ControlPlaneTests` handshake-timing failure on that same run
re-ran clean, outside this PR's diff), then found three further critical
issues — a seventh, eighth, and ninth instance of the "passes locally,
races under load" pattern, all sharing one root cause: fixed time budgets
(a 2-second, 40-attempt poll; two 300ms delays) that CI-shaped contention
(6.5x-20x local slowdown, reproduced directly) can exceed before even one
real tick lands. All fixed:

1. **(Critical) `WaitForDeliveryAsync` and `WaitForCursorAsync`'s shared
   2-second budget could expire under load with no tick having landed yet**,
   producing an `Assert.Fail`/`Assert.NotEmpty` failure indistinguishable
   from a genuine "never delivered" defect. Fixed by replacing the fixed
   attempt count on both with a shared 10-second wall-clock deadline
   (`Stopwatch`-measured), which cannot spin out early on repeated
   transient `IOException`s either -- the wait condition latches (cursor
   watermarks only accumulate forward), so once satisfied it stays
   satisfied.
2. **(Critical) `DisablingNotificationsSkipsDeliveryButStillAdvancesTheCursor`
   used a fixed `Task.Delay(300ms)` immediately followed by a cursor-file
   read**, which could throw before the first tick had a chance to create
   the file at all. Fixed by replacing the delay with `WaitForCursorAsync`,
   polling until the cursor genuinely reaches the ground-truth watermark
   (proving a real tick ran) before asserting nothing was delivered.
3. **(Critical) `AnUnreadableConfigurationFailsClosedAndStillAdvancesTheCursor`
   had the identical fixed-delay-then-read shape** as finding 2, reproduced
   failing with `DirectoryNotFoundException`. Fixed the same way.

### Round 8 review (critical-only): even the widened 10-second deadline wasn't always enough

Round 8 confirmed CI stayed green on the round-7 commit, then reproduced
load at far greater scale than any prior round — 352 samples of the
affected test file across six contention levels on a 24-core box, up to 48
parallel `dotnet test` processes plus 240 CPU-burning threads — and found
one further critical issue, a fifth consecutive instance of the same
underlying "fixed time budget vs. CI-shaped contention" pattern:

1. **(Critical) `WaitForDeliveryAsync`/`WaitForCursorAsync`'s round-7
   10-second deadline still wasn't always enough under extreme thread-pool
   oversubscription.** 9 of 352 samples failed, all in
   `ADeliveryFailureIsIsolatedAndTheCursorStillAdvancesPastBothEvents` (the
   one test whose first tick must process two sprints' worth of work rather
   than one). Critically, failure was **not monotonic in slowdown** — one
   run failed at 26 seconds total elapsed while another in the same wave
   passed at 65 seconds, against a 1-second unloaded baseline — a
   thread-pool scheduling coin flip once oversubscribed, not a margin that
   scales predictably with load, so no fixed deadline is ever provably
   enough in principle. Product code was not implicated: `NotificationDeliveryHostedService`
   behaved correctly in all 352 samples. Fixed by widening the shared
   deadline to 30 seconds — the cost is zero on the happy path, since both
   helpers already return as soon as their condition is met, and the wider
   budget only matters when a runner is genuinely this starved.

## Consequences

- `Forge.Application.INotificationService` (port) and
  `NullNotificationService` (default) added; `AddForgeCore` registers the
  default via `TryAddSingleton`.
- `NotificationDeliveryCursorStore` (static, `Forge.Application`) persists
  an opaque `ControlEventsCursor` token per project at `.forge/notifications
  /cursor.json`.
- `NotificationDeliveryHostedService` (`Forge.Host.Runtime`) sweeps on a
  30-second `PeriodicTimer`, gated by `notifications.enabled`, wired into
  `ControlPlaneHostedService`'s existing lease-scoped start/stop lifecycle
  alongside `ResumeSchedulerHostedService`.
- `notifications.enabled` (User scope, default `true`) added to
  `ConfigurationRegistry`; `UserConfiguration.Notifications` (optional,
  nullable) added to `ConfigurationSchemaCodec`; `user-config.schema.json`
  gains an optional `notifications` object and a `1.2.0` `schema_version`
  entry (`UserContractVersion` bumped to match). `docs/contracts/v1
  /configuration.json` gains the matching key entry and its own
  `contract_version` bump to `1.2.0`, with a new test
  (`ConfigurationRegistryMatchesTheContractsKeyList`) now enforcing the two
  never drift apart again.
- `Forge.Runtime.Windows` gains `WindowsNotificationService`
  (`INotificationService` via `NotifyIcon.ShowBalloonTip`) and
  `AddForgeRuntimeWindowsNotifications` (an `IServiceCollection` extension
  overriding the cross-platform default, mirroring `AddForgeWindowsUpdater`'s
  own shape); `Forge.Host.Windows/Program.cs` calls it alongside the
  existing updater/provider registrations. `UseWindowsForms=true` is the
  only new build-time addition — no new NuGet package.
- Five new message keys (`NotificationAwaitingHumanTitle`,
  `NotificationBlockedTitle`, `NotificationFailedTitle`,
  `NotificationCompletedTitle`, `NotificationSprintLabel`), en/ru.
- `NotificationDeliveryHostedServiceTests` (6 tests): exactly-once delivery
  across ticks, redacted title/body composition, config-gated skip with
  the cursor's own decoded watermark still advancing (and no backlog burst
  on re-enable), delivery-failure isolation across two events proven by
  both surviving delivery and both watermarks advancing, a no-op tick over
  an empty project, and stale-cursor recovery proven against a genuinely
  already-delivered event (not merely "the file changed"). `ControlPlaneTests`'s
  own in-test Host builder (`ControlPlaneHost.StartAsync`) gained the same
  `NotificationDeliveryOptions`/`NotificationDeliveryHostedService`
  registrations `ForgeHostApplication.RunAsync` now has, matching the
  parallel registration `ResumeSchedulerHostedService` already required —
  missing this broke every test that starts a real in-process Host via
  dependency injection, caught immediately by the full suite run.
- `ContractTests.ConfigurationRegistryMatchesTheContractsKeyList` (1 test,
  round 1 review): closes the "documented but never enforced" gap the
  new `notifications.enabled` key exposed in `docs/contracts/v1
  /configuration.json`.

## Deliberately deferred

- **Real verification that `WindowsNotificationService` actually renders a
  toast.** Named above — no interactive desktop exists in this
  repository's CI or realistic review environment to confirm it.
- **Click-through activation.** No argument routing back into Forge; a
  clicked notification does nothing beyond dismissing itself.
- **Desktop-side notification history or an in-app "attention" view.**
  Unrelated to this item; the still-open Stage 11 P11.56-P11.66 navigation
  shell (ADR 0021/0022/0023's own deferred list) is the more natural home
  for a future such view.
- **Network notification channels or custom scripts.** ADR 0005 already
  named both outside the MVP.
- **A real technical guarantee that a duplicate notification is
  impossible.** The cursor-write durability trade-off above accepts a
  rare, bounded re-delivery window on crash — matching "best-effort," not
  "exactly once."

## References

- ADR 0005 (`docs/architecture/decisions/0005-local-host-and-control-plane.md`)
  — "Notifications are local attention projections," the requirement this
  ADR implements.
- ADR 0007 (`docs/architecture/decisions/0007-cross-platform-core-and-minimal-os-adapters.md`)
  — the neutral/adapter split this ADR's `INotificationService` port
  follows.
- `src/Forge.Runtime/Application/NotificationProjector.cs` — the
  already-built, already-tested projection half this ADR delivers.
- `src/Forge.Host.Runtime/ResumeSchedulerHostedService.cs` — the lifecycle
  and per-item failure-isolation pattern `NotificationDeliveryHostedService`
  mirrors directly.
