# ADR 0005: Local host and observable control plane

- Status: Accepted
- Date: 2026-08-12
- Contract version: 1.1.0

## Context

Forge must run long-lived provider attempts while CLI/TUI and Desktop remain
equivalent, independently restartable clients. The Stage 6/7 stores currently
coordinate writers only inside one process. Running both surfaces as independent
application hosts would therefore permit competing mutations of the same sprint.
It would also tie provider lifetime to whichever UI launched it and force each
surface to reconstruct live status independently.

The control plane needs one mutation owner, durable read-back, incremental events,
attention-oriented presentation, explicit human decisions, supervised provider
work, notifications, agent-facing discovery, safe diagnostics, and development
isolation. It does not need a terminal emulator, a general UI automation protocol,
or another workflow state store.

## Decisions

### A cross-platform headless Forge Host owns mutable runtime state

The Forge Host runtime and control plane are platform-neutral; a distributable
Host executable is a thin OS composition root over that runtime (ADR 0007,
ADR 0008). `Forge.Host.Runtime` is the per-user, headless owner of workflow
execution, provider processes, worktrees, routing state, and every mutation of
`.forge/`. Its domain, application, protocol, framing, connection lifecycle,
projections, event cursors, and client SDK contain no Windows dependency and no
concrete provider identifier. `Forge.Host.Windows` is the shipped composition
root: it installs the Windows runtime adapter and registers the concrete
provider adapters (`Forge.Providers.Codex.Windows`,
`Forge.Providers.Claude.Windows`), then calls into the neutral runtime.
`Forge.Host.TestHost` is a minimal neutral composition root with no real
provider registered, used only so the process/lease/protocol acceptance suite
runs on Windows, Linux, and macOS; provider end-to-end behavior stays
Windows-only against `Forge.Host.Windows`. CLI/TUI and Desktop are clients.
Closing, restarting, crashing, or updating a client does not stop an active
attempt; reconnecting clients rebuild their views from durable state. A client
may execute the minimal bootstrap, host-start, update recovery, and self-test
paths needed before a host is available, but it never runs workflow business
logic locally once connected.

The host acquires a cross-process project lease keyed by the manifest's stable
project id before mutation. `IProjectLease` has one platform-neutral BCL
implementation: a `System.Threading.Mutex` whose short, hashed name uses
`NamedWaitHandleOptions` with `CurrentUserOnly = true`, and
`CurrentSessionOnly` decided once per process from a dedicated,
always-uncontended capability probe (a name built from a fresh GUID every
time, so it can never collide with a real lease and never observes real
contention): if this account can construct a `Global\`-namespaced object at
all, every real lease uses `CurrentSessionOnly = false` (the OS-wide `Global\`
namespace, covering every session of the user); otherwise every real lease
falls back to `CurrentSessionOnly = true` (session-scoped). The real lease
name itself never makes this decision — an earlier version tried the real
name directly and fell back on `UnauthorizedAccessException`, which is
ambiguous between "this account cannot create `Global\` objects" (safe to
fall back) and "a different user already holds this exact lease and denied
me" (must not silently create an independent session-scoped object instead).
Creating a `Global\` named object on Windows requires `SeCreateGlobalPrivilege`,
which a standard non-admin user does not hold by default — a same-user
isolation CI check caught this concretely. `CurrentUserOnly` restricts the
object's security descriptor to the creating user rather than creating a
separate object per user, so a different local user's attempt to open the
identical name is denied, not silently redirected to an independent object.
Residual risk: because the probe runs per process, two processes of the
*same* user running at different elevation levels can resolve to different
namespaces (one `Global\`, one session-scoped) and so do not exclude each
other — accepted against the alternative of every non-admin account failing
outright. Diagnostic lease metadata lives in the shared per-user Forge state directory;
the mutex is authoritative and becomes abandoned on process death. A successor
treats
`AbandonedMutexException` as ownership plus a mandatory durable-state recovery
signal, not as clean state. Release, development, test, CLI, and Desktop
instances use the same lease namespace, so distinct instance data roots cannot
become concurrent writers of one `.forge/` tree. A second host may read but
returns `project_in_use` for mutation; it never steals a live lease.

### The local protocol is small, versioned, and user-scoped

Clients use one platform-neutral `System.IO.Pipes` transport implemented with
`NamedPipeServerStream` and `NamedPipeClientStream` in asynchronous byte mode.
The server and client require `PipeOptions.CurrentUserOnly`; the .NET runtime uses
Windows named pipes on Windows and Unix-domain sockets on Linux/macOS while
enforcing same-user peers. Forge supplies only a short, hashed pipe name and does
not manage native endpoints or socket paths. `ILocalControlTransport` remains a
protocol/test boundary with one production BCL implementation, not an OS adapter
family. No loopback TCP fallback is allowed. The wire format is a four-byte
little-endian length followed by one UTF-8 JSON request or response.
The first request is a handshake containing protocol version, client version,
instance id, and supported capabilities; an incompatible major version is
rejected before any project access. Messages have explicit size limits, bounded
read/write/overall deadlines, one response per request, stable diagnostics, and
correlation ids. Existing application commands, queries, immutable DTOs,
permission checks, expected state versions, confirmations, and idempotency keys
remain the semantic contract. Local IPC is only a transport and cannot bypass
them.

No gRPC, Protobuf, streaming socket, subscriber registry, or second command model
is introduced. Clients implement follow mode through bounded short polling. A
compatible old host may finish active attempts after an update; activation points
new clients at the new version, and the host drains/restarts when idle. An
incompatible update must wait for idle or require explicit cancellation and never
terminates work silently.

### Read-back is one stable project snapshot

`GetProjectSnapshot(detail, sprint_id?)` projects authoritative state as
`project -> sprint -> node -> attempt`, with findings, gates, artifacts, routing
health, retry budget, phase profile, last activity, active deadline,
`resume_not_before`, startup/provider/integration status, and suggested actions
attached to their owners. `summary` omits entity detail; `full` or a sprint id
adds the same DTO's optional detail section. Stable ids address every object.
The projection includes a state version and sufficient status to verify a prior
mutation or supersession, but excludes prompts, operator instructions, provider
transcripts, secrets, and raw command lines. `forge status`, `next`, `tree`,
`sprint inspect`, integration status, and Desktop views are local projections of
this DTO; they are not separate Host queries.

### Incremental events reuse durable workflow logs

`ReadControlEvents` reads the append-only per-sprint journals instead
of maintaining an in-memory event ring or duplicate event database. Its opaque,
versioned cursor records per-sprint sequence watermarks and the project manifest
version. Reads discover new sprints, merge unseen events deterministically by
occurrence time, sprint creation order, event sequence, and event id, and advance
across filtered-out events so filters cannot create hidden gaps. Cursors are
bounded in size and validated; invalid, future, or incompatible cursors fail
loudly with a fresh safe anchor and never silently rebaseline.

`forge events` performs one bounded read. `forge events --follow` short-polls and
prints NDJSON or localized human output. The snapshot remains authoritative after a
reconnect or cursor failure. Every attention-relevant Stage 11 mutation,
including finding, artifact, gate, and routing changes, must append or cause a
durable workflow invalidation event so a client can refresh the affected branch.
Provider deferral, timeout, supersession, review-floor, and notification-relevant
attention changes follow the same rule. Terminal output and provider transcripts
are never an event stream.

### Status presentation optimizes for attention

The Desktop dashboard and CLI/TUI views prioritize `awaiting_human`, `blocked`,
`failed`, and completed work that has not been acknowledged, followed by active
work. Users can move to the next item needing attention without changing workflow
state. Status is conveyed by text and shape as well as color; all actions are
keyboard reachable, screen-reader named, and announced without repeated focus
stealing. Reduced-motion and high-contrast platform preferences are honored.
Forge derives these states only from durable state machines and provider
contracts, never from terminal text heuristics.

### Human gates use a narrow, shared decision contract

CLI/TUI and Desktop present the same gate context: rationale, relevant diff or
artifact references, findings, compatibility/security impact, and the permitted
decisions. Approve/reject requires an explicit human action, rationale where the
workflow requires it, expected state version, idempotency key, and confirmation.
There is no destructive preselected decision and no agent/plugin permission to
self-approve. A general-purpose scriptable picker is outside the MVP.

Attempt supersession and review-convergence decisions are human-only variants of
this contract. Supersession requires a bounded instruction artifact and creates a
fresh attempt; review convergence presents current findings, severity floor,
iteration count, and the explicit continue/accept-or-override/abort choices from
ADR 0006. Neither action edits frozen sprint inputs in place.

### Notifications are local attention projections

Desktop/OS notifications project durable `awaiting_human`, `blocked`, `failed`,
and `completed` events. They are best-effort, user-configurable, redacted, and
deduplicated by event id. A notification is never the authoritative record and a
delivery failure never changes workflow state. Network notification channels and
custom scripts remain outside the MVP.

### The source compiler produces one agent integration

Stage 9 generates the Claude Code and Codex skill/plugin views from one canonical
Forge integration source. It documents the project snapshot, events, commands,
addressing, status reporting, recovery, and built-in workflow invariants,
including implementation confirmation before new test selection or authoring,
while omitting human-only authority. Generated files carry source hashes,
generator/protocol versions, ownership markers, and minimum Forge versions.
They communicate the invariant; Forge Host enforces it. Generation and optional
installation are idempotent; unknown user-owned files are never overwritten,
and duplicate installations are detected rather than left ambiguous.

### Diagnostics are allowlisted and development is isolated

`forge doctor --bundle` packages only allowlisted, redacted operational evidence:
Forge/protocol/provider versions, startup checks, project state summaries, event
log integrity, worktree registrations, circuit-breaker/retry state, writable
probes, and safe error metadata. It excludes prompts, provider output, diffs,
source contents, raw command lines, credentials, full environment dumps, and
unredacted personal paths. If safe parsing or redaction cannot be proven, the
payload is omitted and the bundle records that omission.

Release uses instance id `forge`; Debug defaults to `forge-dev`; automated tests
use unique ephemeral ids. Instance identity namespaces IPC endpoints, user
configuration, logs, caches, and worktrees under per-user paths resolved through
cross-platform .NET APIs so development cannot alter installed release state.
The shared project lease remains outside those namespaces and prevents both
instances from mutating the same project concurrently.

Forge Host runs on CoreCLR and is not published with NativeAOT until the same
named-mutex semantics pass the full Windows/Linux/macOS process test matrix.

## Consequences

- Stage 8 becomes a host/control-plane stage before compiler and workflow UI work.
- The current in-process file-store assumption is replaced at the application
  boundary by one host writer; store internals stay simple.
- CLI/TUI and Desktop parity becomes structural because both use the same local
  protocol and projections.
- Durable work survives client restarts and safe updates.
- Provider deadlines, deferrals, review gates, attempt supersession, and local
  notifications reuse the same durable state and read contracts; see ADR 0006.
- Host/protocol/lease tests run on Windows, Linux, and macOS against
  `Forge.Host.TestHost`, even though the MVP Desktop, installer, updater
  strategy, real provider adapters, and end-to-end release remain Windows-only.
- Forge owns no OS-specific Host transport, lease, endpoint, or path adapter; the
  pinned .NET runtime owns those platform details.
- ADR 0007 reinforces the BCL-first rule across Forge. The Stage 8 Host remains
  adapter-free; a later native exception would require a superseding ADR and
  could move only the missing OS call, never workflow or protocol behavior.
- The MVP gains no terminal emulator, cookbook, generic picker, event broker, or
  new transport dependency.

## References

- [Named pipes in .NET](https://learn.microsoft.com/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication)
- [`PipeOptions.CurrentUserOnly`](https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions)
- [`NamedWaitHandleOptions`](https://learn.microsoft.com/dotnet/api/system.threading.namedwaithandleoptions)
- [`NamedWaitHandleOptions.CurrentUserOnly`](https://learn.microsoft.com/dotnet/api/system.threading.namedwaithandleoptions.currentuseronly)
