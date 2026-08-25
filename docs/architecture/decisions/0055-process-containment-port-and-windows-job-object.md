# ADR 0055: Process containment port and Windows Job Object adapter

- Status: Accepted
- Date: 2026-08-25
- Contract version: 1.0.0

## Context

Plan section 12.4's remaining crash-recovery gap: an abrupt kill of the Forge Host process itself
(`taskkill /F`, a crash, an OS out-of-memory kill) gives `ProcessRunner.RunAsync`'s own
cancellation-triggered `process.Kill(entireProcessTree: true)` cleanup no chance to run at all, since
that cleanup is cooperative code inside the very process that just died. A spawned provider process
(and whatever descendants it has spawned) could therefore survive as a live orphan.

ADR 0017 already proved `Process.Kill(entireProcessTree: true)` correctly tears down a multi-generation
process tree on Windows, Linux, and macOS, and on that basis concluded "no native OS-adapter
containment call is added under ADR 0007." That conclusion is correct for what ADR 0017 actually
tested -- a *cooperative* teardown, where the Host process is still alive and running the `Kill` call
itself (cancellation, a deadline, or an ordinary parent exit) -- and remains true today; nothing here
changes it. It does not, and was never claimed to, cover the Host process dying *before* any of its
own code runs. `Process.Kill(entireProcessTree: true)` only tears down a tree from inside a living
parent; it offers no guarantee about what happens to that tree if the parent is the one that dies
first. Closing that narrower, distinct gap is what this ADR adds.

## Decisions

### `IProcessContainment`: a port, not a `ProcessRunner` special case

New `Forge.Application.IProcessContainment` (`Abstractions.cs`): one method, `IDisposable
Attach(Process process)`, called immediately after `Process.Start` for every process
`Forge.Infrastructure.ProcessRunner.RunAsync` spawns, on every platform. The returned handle is
disposed once the process is known to have exited, on every `RunAsync` exit path (normal completion
and the cancellation branch alike) -- mirroring the two-path release discipline ADR 0017's own
`ProcessRunner` cleanup already required. The port promises only "the strongest containment the
installed adapter can offer, applied consistently to every spawned process," not identical behavior
across platforms -- matching ADR 0007's minimal-OS-adapter shape: neutral `Forge.Runtime` owns the
port and the call site; only the concrete mechanism is OS-specific.

`Forge.Infrastructure.NullProcessContainment` (a no-op `Attach` returning an empty `IDisposable`) is
the default every composition root's `AddForgeInfrastructure` registers, so every existing
`new ProcessRunner()` call site -- test code with no DI container -- keeps compiling with the same
"no containment" behavior it always had.

### Windows: a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`

`Forge.Runtime.Windows.WindowsJobObjectProcessContainment` assigns each spawned child to its own
Windows Job Object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. A process created by a job
member is itself automatically a member of the same job (unless it explicitly opts out), so this
reaches descendants too, not just the directly-spawned child. The job handle `Attach` returns is the
trigger: when the Host process is torn down for any reason -- including an abrupt `taskkill /F` or a
crash -- the OS unconditionally closes every handle that process held, which fires the kill. No
cooperation from the dying process is required at that moment; this is what makes it a real guarantee
for exactly the scenario ADR 0017 left open, rather than a best-effort one.

`WindowsRuntimeServices.AddForgeRuntimeWindowsProcessContainment` overrides the cross-platform
`NullProcessContainment` default with this adapter; `Forge.Cli.Windows`, `Forge.Desktop`, and
`Forge.Host.Windows` all call it from their composition roots, so every real Forge process embedding
`ProcessRunner` gets real containment, matching how `WindowsNotificationServices` already overrides
`INotificationService`'s own default.

Attach fails open, never fails the spawn: some environments restrict job-object nesting or breakaway
(a Host already confined to a restrictive job, as some CI/sandbox hosts do). A `Win32Exception`,
`InvalidOperationException`, or `ObjectDisposedException` from the attach path degrades to "no
containment for this one process" and is logged once (not per spawn, since the underlying condition
is permanent for the process's lifetime) rather than silently swallowed -- the prior version of this
adapter caught only `Win32Exception` and logged nothing, verified (via `ProcessContainmentFailOpenTests`)
to let a real fail-open case through as an entirely silent, indistinguishable-from-success loss of the
CHANGELOG-documented guarantee.

Residual, honestly-named gap: `Attach` can only run after `Process.Start` has already returned control
to the caller, so a child that spawns and exits its own grandchild inside that (realistically
sub-millisecond) window could leave the grandchild outside the job before assignment lands. Closing
this fully would need `CREATE_SUSPENDED` plus an explicit `ResumeThread`, which `System.Diagnostics.Process`
does not expose without bypassing its stdio-redirection plumbing entirely -- out of scope here.

Proven with a real, abrupt kill of a genuinely separate spawning process (`ProcessContainmentCrashTests`,
using the `Forge.ProcessContainmentProbe` helper project in its `harness` mode), not merely a
same-process fake: no in-process xunit test can observe this property about itself, since the test
host is the very process that would need to die.

### Linux/macOS: investigated, no adapter added

A POSIX process group (`setpgid`) was prototyped and removed. `setpgid` can only change a child's
group before it execs, but .NET's Unix process-launch implementation synchronously waits, inside the
native `Process.Start` call itself, for the spawned child to reach `execve` before returning control
to managed code -- so by the time any caller-side code could run, the child has, by construction,
already exec'd, and POSIX specifies `setpgid` fails with `EACCES` in exactly that case (confirmed
against .NET's own runtime source and the POSIX spec). Reaching the child before `execve` would
require bypassing `Process.Start` with a bespoke fork/exec/`posix_spawn` wrapper, out of scope for
this change -- and even that would not close the gap on its own: a POSIX process group carries no
OS-level kill-on-parent-death semantic by itself, only a separate reaper process explicitly calling
`kill(-pgid)` would, and this codebase has no such reaper.

Linux/macOS therefore keep running with `NullProcessContainment` permanently, the same as before this
change: a genuine, honestly-documented behavior gap, not a regression. In practice this gap is
Windows-only in impact today -- the only two provider adapters this codebase has (Codex, Claude) are
both Windows-only (ADR 0008), so Linux/macOS never run a real long-lived provider child at all.

### Reconciling with ADR 0017

ADR 0017 is not superseded: its own claim -- that `Process.Kill(entireProcessTree: true)` suffices for
*cooperative* teardown (cancellation, either deadline, or ordinary parent exit, all while the Host
process is alive to run the call) -- still holds and needed no new adapter, exactly as that ADR
concluded. This ADR closes the separate, narrower gap ADR 0017's own scope explicitly excluded: an
*uncooperative* death of the Host process itself, where no code in the dying process runs at all.
Where ADR 0017 said "no native OS-adapter containment call is added," read that as scoped to the
cooperative-teardown question it actually tested, not as a permanent decision against ever adding one
for a different failure mode.

## Consequences

- On Windows, an abrupt Host crash or kill no longer orphans a spawned provider process (or its
  descendants) -- closing plan 12.4's last crash-recovery sub-gap for `AnAbruptHostProcessKillLeavesNoOrphanedProviderProcess`
  (`tests/Forge.Tests/Integration/ProcessRunnerTests.cs`) and the deeper inheritance proof in
  `ProcessContainmentCrashTests` (`tests/Forge.Tests/WindowsRuntime/`).
- Linux/macOS remain uncontained against this specific failure mode; revisit only if a real POSIX
  provider adapter is ever added, since today the gap has no live impact.
- A Host already confined to a restrictive job (nested sandboxes, some CI runners) degrades to no
  containment for the processes it spawns, now observably (logged once) rather than silently.
- `Forge.ProcessContainmentProbe` (`tests/`) is a genuinely separate, Windows-only helper process
  (targets `net10.0-windows10.0.19041.0`, referencing `Forge.Runtime.Windows` directly) rather than a
  portable leaf project like its `Forge.PipeIsolationProbe`/`Forge.MutexIsolationProbe` siblings; it is
  excluded from `.github/scripts/test-portable.ps1`'s neutral leaf-project list for that reason.

## References

- ADR 0006 (supervised execution -- cancellation terminates the owned process tree)
- ADR 0007 (cross-platform core and minimal OS adapters)
- ADR 0017 (attempt deadlines and process-tree teardown -- the cooperative-teardown proof this ADR
  narrows, not supersedes)
