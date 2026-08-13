# ADR 0006: Supervised execution and bounded review convergence

- Status: Accepted (revised 2026-08-13)
- Date: 2026-08-12
- Contract version: 1.2.0

## Context

Long-running provider work must remain observable, cancellable, secure, and
bounded after Forge Host takes ownership of execution. The current Stage 5
adapter passes prompts on command lines, inherits the host environment, and reads
provider output only after process exit. Those choices are adequate for the
completed adapter proof but not for unattended workflow attempts: prompts may
exceed Windows command-line limits or appear in process inspection, unrelated
credentials reach child processes, silent hangs cannot be distinguished from
slow work, and live progress cannot reach the control plane.

Independent review also needs an explicit convergence policy. A fixed retry
count alone either spends too much on repeated low-severity findings or stops
without a controlled human decision. Forge adopts the severity-floor and
iteration-cap model from Agentic Software Development, not a Git HEAD/diff
no-progress heuristic.

## Decisions

### Provider input, environment, and output are bounded contracts

Forge sends prompts through redirected standard input, never a command-line
argument, environment variable, diagnostic, or log. Each official adapter uses
the vendor's stdin-capable non-interactive mode and an absolute executable path;
shell invocation remains forbidden. This removes command-line length limits and
keeps prompt content out of ordinary process inspection.

Provider children receive a minimal environment assembled by Forge. A frozen
provider environment contract allowlists required platform, home/temp, locale,
proxy, toolchain, and provider-authentication variables. Project content cannot
add variable names or values. Known nested-session markers and credentials for
other providers are removed. Secret values may come only from the provider's
existing authentication mechanism or OS secret storage and are never copied into
sprint state.

Stdout and stderr are consumed concurrently as bounded streams. The adapter
limits a frame, line, aggregate output, and retained safe tail; parses documented
JSON/JSONL incrementally; and applies redaction before any durable or presentation
boundary. Oversized or malformed frames fail closed. Provider prose, terminal
text, and heartbeat text never determine workflow state.

A zero process exit code proves only that the provider transport ended normally.
An attempt succeeds only after Forge receives a schema-valid terminal result for
the owned attempt and every deterministic gate passes. Exit without an explicit
terminal result is a provider failure; duplicate or contradictory terminal
results fail closed.

### Forge Host supervises every provider process

Every attempt has two frozen deadlines: an absolute session deadline and an idle
deadline. Any bounded stream activity resets the idle deadline; model wording
does not. Safe, throttled activity events may update the attempt's last-activity
time without persisting provider content. The durable outcome distinguishes
`provider_idle_timeout`, `provider_session_timeout`, user cancellation, and
ordinary provider failure.

Cancellation or either deadline terminates the entire owned process tree, waits
for exit, drains bounded pipes, and records cleanup outcome. The first
implementation uses `.NET Process.Kill(entireProcessTree: true)` on all platforms.
Windows, Linux, and macOS tests launch a child and grandchild and prove none
survives cancellation, timeout, or normal parent exit. If those tests prove the
BCL guarantee insufficient, only the missing native containment call moves to a
minimal OS adapter under ADR 0007; supervision policy remains cross-platform.

### Rate-limit waiting is durable, not a sleeping worker

A retryable rate limit abandons the failed attempt, records a safe
`resume_not_before` from structured provider metadata or the frozen fallback
policy, releases its executor slot, and leaves the node ready but routing-deferred.
Forge Host re-enqueues it idempotently after the timestamp. The project snapshot and
notifications expose the deferral without raw provider text. Repeated deferral
cannot spin or bypass the sprint retry budget. Quota exhaustion without a safe
retry time blocks and requires normal recovery; it is not guessed into a delay.

### Operator steering supersedes an attempt

An operator may explicitly supersede a non-terminal attempt. The command requires
confirmation, expected state version, idempotency key, target attempt id, and a
bounded instruction artifact. Forge cancels the process tree, discards the owned
worktree, records `AttemptSuperseded`, and creates a fresh attempt for the same
node from the superseded attempt's recorded base. It never edits the frozen plan,
continues a partially modified worktree, or hides the original input and outcome.
Agents and generated integrations cannot invoke this human-only command.

### Three execution profiles are frozen

The sprint snapshot resolves one profile for planning, implementation, and
review. Internal and external review use the same review profile with fresh
attempts and bounded inputs. Finalization is deterministic code, not a model phase. Each profile records
provider, model, effort, sandbox/permission policy, capability allowlist, session
deadline, and idle deadline. Missing values inherit from the project model policy
before the sprint starts; running sprints never follow later configuration
changes. A node receives only its frozen context manifest and profile
capabilities; it inherits no parent transcript and cannot grant itself or a child
additional tools or human-only commands.

Reviewer provider/model independence is best-effort. Forge scans the configured
priority order first for an available reviewer whose provider and model lineage
differ from the implementation lineage. If none is available, it uses the first
available reviewer in the normal priority order, including the same provider or
model family. Every review still uses a new attempt identity, clean context, and
the complete review contract. Forge records whether provider/model separation
was achieved; reduced separation is diagnostic metadata, not a human gate. No
available review model still blocks review. ADR 0008 defines the selection rule.

### One review engine follows the ASD severity-floor policy

One engine runs design and implementation review with independent durable
counters and rubric/scope inputs; it does not encode separate reviewer roles or
pipelines. Each iteration starts fresh reviewer contexts with no authoring
rationale or prior conversational context. Iteration 1 reviews the full scoped
artifact/diff; later iterations review the changes made to address the previous
round plus the still-relevant acceptance and rule context.

Default consecutive budgets are low `1`, medium `1`, high `2`, and critical `10`.
Their cumulative range yields floors low on iteration 1, medium on iteration 2,
high on iterations 3–4, critical on iterations 5–14, and an iteration-limit human
gate before iteration 15. Findings below the current floor are recorded as
dropped, not silently lost. User-approved continuation keeps the counter and pins
the floor at critical; it never resets or re-admits lower severities.

Every internal reviewer emits a coverage ledger for every scoped file and every
applicable rubric item. An incomplete ledger invalidates that verdict and causes
one fresh re-dispatch in the same iteration. All mandatory eligible reviewers
must approve in the same iteration. Fixes run in a new implementation attempt,
not inside the reviewer context.

The external reviewer receives the prior iteration's normalized finding set as
explicit bounded input. Two consecutive identical sets by file, location, rule,
and message fingerprint create a review-convergence human gate with three choices:
accept the findings/known risk, override them with rationale, or abort the sprint.
This is finding-set convergence from the ASD policy; Forge does not use ralphex's
HEAD/diff stalemate detector.

At the cumulative iteration limit the same human gate offers: continue with the
critical-only floor, accept current findings/known risk, or abort. Decisions are
durable, attributable, version-checked, and never available to an agent.

### Notifications are projections of durable attention events

MVP notifications cover `awaiting_human`, `blocked`, `failed`, and `completed`
through Desktop/OS notification APIs. They are best-effort, deduplicated by event
id, localized at display time, and contain only redacted project label, workflow
state, duration, and safe change counts. Delivery failure never changes workflow
state. Notification selection is a user preference; webhook, Slack, email, custom
scripts, and notification-held secrets remain deferred.

## Consequences

- Stage 11 replaces the Stage 5 proof's command-line prompt and buffered output
  path before real node execution is enabled.
- Provider hangs, rate limits, cancellation, and steering become durable workflow
  outcomes visible through the project snapshot and event read-back.
- Normal provider exit cannot masquerade as successful workflow completion, and
  model nodes cannot widen their context or capabilities.
- Review cost narrows deterministically while critical findings remain undroppable
  until a recorded human decision.
- A single-provider configuration can complete review while Forge still prefers
  a distinct provider/model lineage whenever one is available.
- Forge adds no provider wrapper, notification broker, review database, or process
  supervision dependency.

## References

- [ralphex executor](https://github.com/umputun/ralphex/blob/e62caa968aec6a3234b77f4310aa301bb613250f/pkg/executor/executor.go)
- [ralphex Codex executor](https://github.com/umputun/ralphex/blob/e62caa968aec6a3234b77f4310aa301bb613250f/pkg/executor/codex.go)
- [ASD review policy](https://github.com/LordKuper/agentic-software-development/blob/2d1bb2cc667ae6afa94c633338787217f7b8a4cd/.asd/rules/review-policy.md)
- [ASD external-review convergence](https://github.com/LordKuper/agentic-software-development/blob/2d1bb2cc667ae6afa94c633338787217f7b8a4cd/.asd/rules/external-review.md)
