using System.Globalization;

namespace Forge.Domain;

/// <summary>Matches `aggregate.kind` in docs/contracts/v1/schemas/event.schema.json.</summary>
public enum AggregateKind
{
    Sprint,
    Node,
    Attempt,
}

public sealed record AggregateRef(AggregateKind Kind, string Id, long Version);

/// <summary>The closed set <see cref="DiffFileStat.ChangeKind"/> may take, mirroring
/// `payload.diff.files.items.change_kind` in docs/contracts/v1/schemas/event.schema.json. Plain
/// strings rather than an enum: this value crosses the durable JSON envelope verbatim, and every
/// other closed-set value on that envelope (`to_state`, `blocked_reason`, `outcome`) is already a
/// snake_case string there. <see cref="Binary"/> is not a git status of its own — it is what
/// `git diff --numstat` reports (`-`/`-` instead of line counts) for a file with no textual diff,
/// and it takes precedence over whatever add/delete/modify status that same file also has, because
/// "how many lines changed" is the only question the other kinds answer.</summary>
public static class DiffChangeKinds
{
    public const string Added = "added";
    public const string Deleted = "deleted";
    public const string Modified = "modified";
    public const string Renamed = "renamed";
    public const string Binary = "binary";

    public static bool IsKnown(string? kind) =>
        kind is Added or Deleted or Modified or Renamed or Binary;
}

/// <summary>One changed file inside a <see cref="DiffPayload"/>. <paramref name="Path"/> is always
/// relative to the attempt worktree's root and forward-slashed (git's own `--numstat` output shape),
/// never an absolute or traversing path — see
/// <c>Forge.Application.SprintGitIsolation.ReadDiffStatAsync</c>, which drops any entry that is not
/// syntactically safe rather than recording it. <paramref name="Added"/>/<paramref name="Deleted"/>
/// are both 0 for <see cref="DiffChangeKinds.Binary"/>.</summary>
public sealed record DiffFileStat(string Path, int Added, int Deleted, string ChangeKind);

/// <summary>ADR 0059: structural git statistics for exactly one attempt's own commit — never diff
/// hunk content, which is deliberately not persisted anywhere (it is re-read from git on demand at
/// render time). <paramref name="Files"/> is capped at
/// <see cref="Forge.Application.GitWorktreeManagerDiffStatBudget.MaxFiles"/>;
/// <paramref name="ElidedFiles"/> counts the changed files beyond that cap, so a reader can always
/// tell a genuinely small change from a truncated view of a large one.
/// <paramref name="FilesChanged"/> is the *total* changed-file count (including elided ones), and
/// <paramref name="Insertions"/>/<paramref name="Deletions"/> are likewise totals over every changed
/// file, not only the retained ones.</summary>
public sealed record DiffPayload(
    int FilesChanged,
    int Insertions,
    int Deletions,
    IReadOnlyList<DiffFileStat> Files,
    int ElidedFiles);

/// <summary>ADR 0060: one tool call an implementation attempt's provider made, as durably recorded.
/// <paramref name="Target"/> is the attempt-worktree-relative, forward-slashed, redacted file path a
/// <see cref="Forge.Providers.ProviderToolCallKinds.Edit"/> touched — or <see langword="null"/>, both
/// for every <see cref="Forge.Providers.ProviderToolCallKinds.Command"/> (whose only identifying text
/// would be the command line itself, which ADR 0006 forbids persisting) and for an edit whose vendor
/// path failed the syntactic safety check and was rejected rather than rewritten.
/// <paramref name="DurationMilliseconds"/> is Forge-observed wall time between the vendor's own start
/// and completion lines, never a vendor-reported measurement.
/// <paramref name="ExitCode"/>/<paramref name="Succeeded"/> are the command's own outcome, both
/// <see langword="null"/> when the vendor did not report one.
/// Deliberately carries no change-kind vocabulary of its own, unlike
/// <see cref="DiffFileStat.ChangeKind"/>: a single real capture cannot responsibly define one.</summary>
public sealed record ToolCallStat(
    string Kind,
    string? Target,
    int? DurationMilliseconds,
    int? ExitCode,
    bool? Succeeded);

/// <summary>ADR 0060: what one implementation attempt's provider actually did, never the content it
/// did it with — no command text, no command output, no file content. <paramref name="Calls"/> is
/// capped at <see cref="Forge.Application.ProviderToolUseBudget.MaxCalls"/> with the remainder counted
/// in <paramref name="ElidedCalls"/>, while <paramref name="ToolCalls"/>/<paramref name="Commands"/>/
/// <paramref name="Edits"/> stay totals over every observed call — ADR 0059's "honest totals plus an
/// explicit elision count" rule. Only two per-kind totals exist because only two tool-call kinds are
/// verified by a real recorded provider stream. <paramref name="UnmappedItems"/> counts vendor items
/// this adapter's mapping did not recognize at all; ordinary agent narration is never counted there,
/// or the signal would be noise on every healthy run.</summary>
public sealed record ToolUsePayload(
    int ToolCalls,
    int Commands,
    int Edits,
    IReadOnlyList<ToolCallStat> Calls,
    int ElidedCalls,
    int UnmappedItems);

/// <summary>ADR 0061: what one implementation attempt's provider reported spending, read from the one
/// terminal stream event that carries it. Every member is nullable and independently optional — a
/// vendor reports none, some, or all of them, and nothing here is ever derived, defaulted to zero, or
/// guessed. <paramref name="ContextWindow"/> is the model's own context-window size, the honest
/// denominator for a "used X of Y" reading; it is <see langword="null"/> for every provider that does
/// not publish one (Codex's usage object has no such field), which is deliberately reported as absent
/// rather than filled in from a hardcoded per-model table.
///
/// Carries no free text at all: every field is a plain number, so unlike <see cref="DiffPayload"/> and
/// <see cref="ToolUsePayload"/> there is nothing here for
/// <c>SprintTimelineRedaction.RedactPayload</c> to walk.</summary>
public sealed record UsagePayload(
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheCreationTokens,
    int? ContextWindow);

/// <summary>ADR 0059: the typed, structured half of a <see cref="WorkflowEvent"/> —
/// `payload` in docs/contracts/v1/schemas/event.schema.json. Exists because
/// <see cref="WorkflowEvent.Arguments"/> is a deliberately flat
/// <c>IReadOnlyDictionary&lt;string, string?&gt;</c> that genuinely cannot carry a nested list, and
/// the envelope declares `additionalProperties: false`. One optional sub-object per family:
/// <see cref="Diff"/> (ADR 0059), <see cref="ToolUse"/> (ADR 0060), and <see cref="Usage"/>
/// (ADR 0061). Always <see langword="null"/> for every event type that predates this field, and
/// omitted from the serialized line entirely when null, so every journal line already on disk stays
/// byte-for-byte valid.</summary>
/// <remarks>Every member is required, with no default — ADR 0057/0058's "review every construction
/// site" discipline, which ADR 0060 confirmed is still affordable here and ADR 0061 re-counted rather
/// than assumed: four sites today (the three producing store methods and the codec's own read path).
/// A new family must therefore be considered at every existing producer rather than silently
/// defaulting to absent.</remarks>
public sealed record WorkflowEventPayload(DiffPayload? Diff, ToolUsePayload? ToolUse, UsagePayload? Usage);

/// <summary>
/// One append-only, localization-safe transition record. Mirrors
/// docs/contracts/v1/schemas/event.schema.json; `Arguments["to_state"]` carries the resulting
/// state so a stream of these can be folded back into current sprint/node/attempt state without
/// any transcript or free-text dependency.
/// </summary>
/// <remarks><c>Payload</c> is ADR 0059's structured envelope half — see
/// <see cref="WorkflowEventPayload"/>. Declared last, nullable, and defaulted rather than required:
/// this type is constructed at well over thirty call sites, all but one of which have nothing
/// structured to carry, so ADR 0057/0058's "review every construction site explicitly" discipline
/// would produce thirty-odd mechanical `Payload: null` arguments and no new safety. The one producer
/// that does carry a payload (<c>ISprintStore.AppendAttemptDiffRecordedAsync</c>) is the only place
/// that may set it, and <see cref="WorkflowFold.IsTransitionRecord"/> fails closed on an
/// <see cref="AttemptDiffRecordedType"/> event whose payload is missing, so the default can never
/// silently produce a valid-looking but empty diff record.</remarks>
public sealed record WorkflowEvent(
    Guid EventId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    AggregateRef Aggregate,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Arguments,
    Guid? CorrelationId = null,
    Guid? CausationId = null,
    WorkflowEventPayload? Payload = null)
{
    public const string ToStateArgument = "to_state";
    public const string RouteDecisionRecordedType = "RouteDecisionRecorded";

    /// <summary>An attempt heartbeat: ADR 0006's "safe, throttled activity events" that bump
    /// <see cref="AttemptSnapshot.LastActivityAt"/> without persisting provider content or moving
    /// the attempt through its state machine. Never carries <see cref="ToStateArgument"/>;
    /// <see cref="OccurredAt"/> is itself the activity timestamp. May carry
    /// <see cref="AttemptActivityKindArgument"/> (Stage 11, P11.32-P11.40) — still never provider
    /// content, only a fixed, typed classification of what kind of activity occurred.</summary>
    public const string AttemptActivityRecordedType = "AttemptActivityRecorded";

    /// <summary>Carried on an <see cref="AttemptActivityRecordedType"/> event — see
    /// <see cref="AttemptActivityKind"/>. Optional: an event without it is a plain, untyped
    /// heartbeat, matching every activity event recorded before Stage 11 P11.32-P11.40.</summary>
    public const string AttemptActivityKindArgument = "activity_kind";

    /// <summary>Carried on a node's own transition events so retry policy needs no attempt lookup.</summary>
    public const string AttemptNumberArgument = "attempt_number";

    /// <summary>Carried on a node's `running` transition: the id of the attempt it was started
    /// with, so <see cref="NodeSnapshot.CurrentAttemptId"/> can answer "which attempt does this
    /// node's `running` state belong to" directly, without re-deriving an id from
    /// <see cref="NodeSnapshot.AttemptCount"/> and risking a mismatch once a replacement attempt
    /// (Stage 11, P11.48-P11.55) is involved.</summary>
    public const string CurrentAttemptIdArgument = "current_attempt_id";

    /// <summary>Carried on an attempt's creation event so its owning node is a durable fact, not
    /// something only the caller who happens to pair matching ids remembers.</summary>
    public const string NodeIdArgument = "node_id";

    /// <summary>Carried on the first transition an attempt makes away from `created`, so the
    /// outcome a compound operation committed to is a durable fact a retry must honor — never
    /// something a caller's later, possibly different argument can silently flip.</summary>
    public const string TargetOutcomeArgument = "target_outcome";

    /// <summary>Carried on a sprint's `blocked` transition so *why* it is blocked is a durable fact,
    /// not something re-derived from node state alone — a sprint can be `blocked` for reasons that
    /// look identical from `allSettledGood`/open-findings alone (a stuck node manually retried and
    /// skipped settles every node exactly as cleanly as a late finding does), and only a `blocked`
    /// sprint whose *actual* cause was an open finding may recover automatically once that finding
    /// resolves; every other cause requires the operator's explicit `resume_sprint` decision.</summary>
    public const string BlockedReasonArgument = "blocked_reason";

    /// <summary>ADR 0006's human-only operator-steering command (Stage 11, P11.48-P11.55): "Forge
    /// ... records `AttemptSuperseded`." Appended on the superseded attempt's own aggregate,
    /// alongside (never instead of) its ordinary `AttemptChanged` transition to `cancelled` — this
    /// event carries the bounded operator instruction and is never a transition itself (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptActivityRecordedType"/>'s own
    /// non-transition shape.</summary>
    public const string AttemptSupersededType = "AttemptSuperseded";

    /// <summary>Carried on an <see cref="AttemptSupersededType"/> event — the bounded instruction
    /// artifact ADR 0006 requires ("never hides the original input and outcome": this augments the
    /// record, it never edits or removes it).</summary>
    public const string SupersessionInstructionArgument = "supersession_instruction";

    /// <summary>Carried on the *replacement* attempt's own creation event — ADR 0006's "linkage":
    /// a clean-replacement attempt durably names exactly which attempt it replaced. Absent on an
    /// ordinarily-started attempt.</summary>
    public const string SupersedesAttemptIdArgument = "supersedes_attempt_id";

    /// <summary>Carried on an attempt's creation event when its worktree's git base commit is
    /// already known at creation time — currently only true for a
    /// <see cref="SupersedesAttemptIdArgument"/> clean replacement, which reuses the superseded
    /// attempt's own recorded base rather than drifting to wherever integration currently sits
    /// (ADR 0006: "from the superseded attempt's recorded base"). Absent otherwise: nothing else
    /// today records what commit an attempt's worktree would be created at.</summary>
    public const string BaseCommitArgument = "base_commit";

    /// <summary>Carried on an attempt's creation event when a model-bearing role
    /// (<see cref="Forge.Domain.ExecutionPhase"/>) actually routed to a provider -- the frozen
    /// <see cref="ExecutionProfile.Provider"/> the `RouteDecision` was keyed on
    /// (<see cref="Forge.Application.SprintScheduler.StartAttemptAsync"/>). Absent for a
    /// non-model-bearing role (no routing decision exists) and for any attempt recorded before
    /// this field existed.</summary>
    public const string ProviderArgument = "provider";

    /// <summary>Carried alongside <see cref="ProviderArgument"/> -- the frozen
    /// <see cref="ExecutionProfile.Model"/> half of the same `RouteDecision` key.</summary>
    public const string ModelArgument = "model";

    /// <summary>Plan section 7.3's durable stop intent: recorded once for the exact attempt a
    /// `StopCurrentOperation` request targets, before the stop coordinator relies on the in-memory
    /// <c>ActiveOperationRegistry</c> at all. Appended on the attempt's own aggregate, alongside
    /// (never instead of) its ordinary `AttemptChanged` transition to `cancelled` once the stop
    /// actually converges — this event itself is never a transition (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptSupersededType"/>'s own shape.
    /// Unlike that type, this one is folded into <see cref="AttemptSnapshot.StopRequestedAt"/>: an
    /// executor or restart-recovery pass must be able to ask "does this running attempt already
    /// carry a stop intent" directly, not merely read it back as audit trail.</summary>
    public const string AttemptStopRequestedType = "AttemptStopRequested";

    /// <summary>ADR 0047 addendum: the stop saga's own durable "fully converged" marker, appended
    /// once by <see cref="Forge.Application.StopOperationCoordinator.FinishStopAsync"/> as its last
    /// step, unconditionally, regardless of which of its earlier steps this exact call did or did not
    /// need to (re-)run. Recorded on the attempt's own aggregate, never a transition itself (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptStopRequestedType"/>'s own shape.
    /// Also folded (<see cref="AttemptSnapshot.StopConvergedAt"/>), for the same reason
    /// <see cref="AttemptStopRequestedType"/> is: every node-role executor must be able to ask "does
    /// this node's current attempt still need <c>FinishStopAsync</c>" directly, from durable state,
    /// independent of the node's own current state -- see that field's own remarks for why a check
    /// gated only on <see cref="AttemptStopRequestedType"/> having landed is not enough on its own.</summary>
    public const string AttemptStopConvergedType = "AttemptStopConverged";

    /// <summary>Plan section 8.4's committed-rewind marker (Slice 3): recorded once per committed
    /// <c>MoveSprintToStage</c> rewind, on the sprint's own aggregate. Never a transition itself (no
    /// <see cref="ToStateArgument"/>) -- a rewind's sprint-level effect is a revision bump, not
    /// necessarily a sprint-state change, matching <see cref="AttemptSupersededType"/>'s own
    /// non-transition shape. Folded into <see cref="SprintSnapshot.Revision"/> (never decremented,
    /// never skipped): every node-role executor and prerequisite check reads a sprint's *current*
    /// revision directly from this projection, the same way <see cref="AttemptStopRequestedType"/>
    /// is folded rather than left as audit-only. Round 2 review of PR #96 (critical): also folded
    /// into <see cref="SprintSnapshot.PendingRewindTargetStageId"/>/
    /// <see cref="SprintSnapshot.PendingRewindReason"/>/<see cref="SprintSnapshot.PendingRewindIdempotencyKey"/>
    /// -- this event alone durably means "a rewind targeting this stage, for this reason, under this
    /// key has started and not yet converged," independent of whatever node/sprint state this saga's
    /// own later steps produce. Now also carries <see cref="IdempotencyKeyArgument"/>, so a resumed
    /// retry can recover the *original* caller's key (never mint a new one) and re-enter step 2's own
    /// ledger-keyed replay branch rather than risk a version conflict against a sprint aggregate
    /// version that has since moved on.</summary>
    public const string StageRevisionRecordedType = "StageRevisionRecorded";

    /// <summary>Carried on a <see cref="StageRevisionRecordedType"/> event: the new revision value
    /// (<see cref="StageRevision.Value"/>, as a base-10 integer) this rewind commits to. Also carried
    /// on a node's own `succeeded -> ready`/`succeeded -> pending`/`failed -> pending`/
    /// `awaiting_human -> pending` transitions (plan section 8.4's reopen/invalidate edges) so
    /// <see cref="NodeSnapshot.Revision"/> tracks which revision that node's own execution state now
    /// belongs to -- node identity stays stable; only this argument's value changes.</summary>
    public const string RevisionArgument = "revision";

    /// <summary>Carried on a <see cref="StageRevisionRecordedType"/> event: the stage the rewind
    /// targeted, for the timeline's own actor-visible rendering.</summary>
    public const string TargetStageIdArgument = "target_stage_id";

    /// <summary>Carried on a <see cref="StageRevisionRecordedType"/> event: the operator's bounded,
    /// mandatory reason for the rewind (plan section 8.4 point 1) -- augments the durable record, the
    /// same "never hides the original input" discipline <see cref="SupersessionInstructionArgument"/>
    /// already follows for attempt supersession.</summary>
    public const string RewindReasonArgument = "rewind_reason";

    /// <summary>Round 1 review of PR #96 (finding 1): the whole `MoveSprintToStage` saga's own durable
    /// "fully converged" marker, appended once by
    /// <see cref="Forge.Application.StageTransitionCoordinator.MoveAsync"/> as the very last,
    /// unconditional step of a successful advance or rewind commit -- mirrors
    /// <see cref="AttemptStopConvergedType"/>'s own role for the stop saga. Recorded on the sprint's
    /// own aggregate, never a transition itself (no <see cref="ToStateArgument"/>). Unlike
    /// <see cref="StageRevisionRecordedType"/> (written mid-saga, at step 2, before evidence
    /// supersession/node invalidation/graph re-advance have run), this event exists only once the
    /// *entire* saga has actually finished -- the outer idempotent-replay check
    /// (<see cref="Forge.Application.ISprintStore.TryGetConvergedStageTransitionAsync"/>) keys on
    /// this marker instead of the raw idempotency-key ledger precisely so a crash between step 2 and
    /// the last step can never make a future replay report success on a still-unfinished commit.
    /// Round 2 review of PR #96 (critical): also projected -- clears
    /// <see cref="SprintSnapshot.PendingRewindTargetStageId"/>/<see cref="SprintSnapshot.PendingRewindReason"/>/
    /// <see cref="SprintSnapshot.PendingRewindIdempotencyKey"/>, the durable "unconverged rewind in
    /// progress" marker <see cref="StageRevisionRecordedType"/> below sets. This is what lets
    /// <c>StageTransitionCoordinator.MoveAsync</c> resume a crashed rewind from any of its own steps:
    /// the marker stays set for as long as (and only as long as) the saga has not actually finished.
    /// </summary>
    public const string StageTransitionConvergedType = "StageTransitionConverged";

    /// <summary>Carried on a <see cref="StageTransitionConvergedType"/> event: the caller's own
    /// `MoveSprintToStage` idempotency key this saga just finished converging for -- looked up
    /// directly by <see cref="Forge.Application.ISprintStore.TryGetConvergedStageTransitionAsync"/>
    /// scanning the raw journal, since a sprint can legitimately be moved many times over its life
    /// and each commit's own key must be distinguished. Round 2 review of PR #96 (critical): also
    /// carried on a <see cref="StageRevisionRecordedType"/> event -- there, unlike here, it IS folded
    /// (into <see cref="SprintSnapshot.PendingRewindIdempotencyKey"/>), so a resumed retry can recover
    /// the exact key the original, still-unconverged commit used, rather than only ever seeing it
    /// once the saga has already finished.</summary>
    public const string IdempotencyKeyArgument = "idempotency_key";

    /// <summary>Post-release timeline gap closure (plan section 4.3/6.3, ADR 0054): a user-posted,
    /// bounded free-text message attached to a sprint. Appended to this SAME per-sprint journal
    /// (rather than a second store) so it gets a dense, unique <see cref="Sequence"/> for free and
    /// <see cref="Forge.Application.SprintTimelineProjector"/>'s existing cursor/redaction machinery
    /// needs no second merge step. Recorded on the sprint's own aggregate, never a transition itself
    /// (no <see cref="ToStateArgument"/>), matching <see cref="AttemptSupersededType"/>'s own
    /// non-transition shape. Deduplicated by the caller-supplied <see cref="WorkflowEvent.EventId"/>
    /// itself (see <see cref="Forge.Application.ISprintStore.AppendUserMessageAsync"/>) rather than a
    /// version/idempotency-key pair: nothing about a message post conflicts with concurrent workflow
    /// progress, so it is never gated on the sprint's current version.</summary>
    public const string UserMessagePostedType = "UserMessagePosted";

    /// <summary>Carried on a <see cref="UserMessagePostedType"/> event: the bounded message text
    /// itself (ADR 0054), reusing <see cref="Forge.Application.SprintScheduler.MaxSupersessionInstructionLength"/>
    /// as its bound rather than inventing a new one.</summary>
    public const string UserMessageTextArgument = "message_text";

    /// <summary>ADR 0054, redesigned (round of PR #104 review, finding 1): a node's user-visible
    /// summary (<see cref="Forge.Domain.Handoff.Summary"/>), recorded as its own real entry in this
    /// SAME per-sprint journal the instant <see cref="Forge.Application.SprintScheduler.RecordHandoffAsync"/>
    /// runs -- mirrors <see cref="UserMessagePostedType"/>'s own "give it a real, dense
    /// <see cref="Sequence"/>, never borrow one" shape. The original design instead stamped the
    /// <c>Handoff</c> record itself with the sprint's current <c>LastSequence</c> at record time and
    /// had the projector anchor to "the nearest event at or before" that borrowed value -- unsound on
    /// two counts: the borrowed sequence could belong to a completely unrelated later event appended
    /// in the gap before the handoff write landed (including this very journal's own
    /// <see cref="UserMessagePostedType"/>), and a cursor that had already advanced past that sequence
    /// could never see the handoff once it finally landed, since the two writes are not atomic. A real
    /// event closes both holes: its own <see cref="Sequence"/> is assigned atomically at append time,
    /// the same guarantee every other event type here already has, so there is nothing left to borrow
    /// or anchor. Recorded on the producing node's own aggregate, never a transition itself (no
    /// <see cref="ToStateArgument"/>), matching <see cref="AttemptSupersededType"/>'s own non-transition
    /// shape.</summary>
    public const string AgentSummaryRecordedType = "AgentSummaryRecorded";

    /// <summary>Carried on an <see cref="AgentSummaryRecordedType"/> event: the summary text itself
    /// (<see cref="Forge.Domain.Handoff.Summary"/>, duplicated onto the event) -- the projector never
    /// needs to resolve the separate <c>Handoff</c> store for this item's content, only for its
    /// mutable <see cref="Forge.Domain.Handoff.Superseded"/> flag (see
    /// <see cref="Forge.Application.SprintTimelineProjector.MergeAndPage"/>). The event's own
    /// <see cref="WorkflowEvent.CorrelationId"/> carries the exact <c>Handoff.HandoffId</c> this
    /// summary corresponds to, so that superseded check can still find it.</summary>
    public const string AgentSummaryTextArgument = "summary";

    /// <summary>ADR 0059: the structural git statistics of exactly one implementation attempt's own
    /// commit, recorded once per attempt on that attempt's own aggregate the moment its work reaches
    /// the sprint's integration branch. Never a transition itself (no <see cref="ToStateArgument"/>)
    /// and never folded into any snapshot, matching <see cref="AttemptSupersededType"/>'s own shape:
    /// a diff summary is durable timeline/audit content, not workflow state. Exactly one event per
    /// attempt (never one per changed file) because
    /// <see cref="Forge.Application.FileSprintEventLog"/> re-reads and re-validates the entire
    /// journal on every append, so per-file events would make an attempt's append cost quadratic in
    /// the size of its own change. The per-file detail rides on this event's
    /// <see cref="Payload"/> instead.</summary>
    public const string AttemptDiffRecordedType = "AttemptDiffRecorded";

    /// <summary>Carried on an <see cref="AttemptDiffRecordedType"/> event: the changed-file count as
    /// a base-10 integer. Derived from the event's own <see cref="Payload"/> by the single producing
    /// store method, never supplied independently, so the two can never disagree. Present in
    /// <see cref="Arguments"/> as well as the payload because that is what the localized timeline
    /// template (<c>workflow.attempt_diff_recorded</c>) substitutes — every surface renders a
    /// timeline item through <c>TimelineMessageFormatter</c>, which reads
    /// <see cref="Arguments"/>.</summary>
    public const string DiffFilesChangedArgument = "files_changed";

    /// <summary>Carried on an <see cref="AttemptDiffRecordedType"/> event alongside
    /// <see cref="DiffFilesChangedArgument"/>: total added lines, base-10.</summary>
    public const string DiffInsertionsArgument = "insertions";

    /// <summary>Carried on an <see cref="AttemptDiffRecordedType"/> event alongside
    /// <see cref="DiffFilesChangedArgument"/>: total deleted lines, base-10.</summary>
    public const string DiffDeletionsArgument = "deletions";

    /// <summary>ADR 0060: what one implementation attempt's provider actually did — how many shell
    /// commands it ran and how many files it edited — recorded once per attempt on that attempt's own
    /// aggregate, alongside (never instead of) <see cref="AttemptDiffRecordedType"/>. Never a
    /// transition (no <see cref="ToStateArgument"/>) and never folded into any snapshot, exactly like
    /// its diff sibling: tool-call statistics are durable timeline/audit content, not workflow state.
    /// Exactly one event per attempt for the same reason
    /// (<see cref="Forge.Application.FileSprintEventLog"/> re-reads the whole journal on every
    /// append); the per-call detail rides on this event's <see cref="Payload"/>. Recorded only for a
    /// provider whose adapter actually extracts tool calls — Codex today; Claude capture is separate,
    /// deferred work (ADR 0060).</summary>
    public const string AttemptToolUseRecordedType = "AttemptToolUseRecorded";

    /// <summary>Carried on an <see cref="AttemptToolUseRecordedType"/> event: the total tool-call
    /// count as a base-10 integer, counting every observed call and not only the retained rows.
    /// Derived from the event's own <see cref="Payload"/> by the single producing store method, never
    /// supplied independently, so the rendered summary and the structured payload cannot drift.
    /// </summary>
    public const string ToolCallsArgument = "tool_calls";

    /// <summary>Carried on an <see cref="AttemptToolUseRecordedType"/> event alongside
    /// <see cref="ToolCallsArgument"/>: shell commands run, base-10.</summary>
    public const string ToolCommandsArgument = "commands";

    /// <summary>Carried on an <see cref="AttemptToolUseRecordedType"/> event alongside
    /// <see cref="ToolCallsArgument"/>: files edited, base-10.</summary>
    public const string ToolEditsArgument = "edits";

    /// <summary>ADR 0061: what one implementation attempt's provider reported spending in tokens,
    /// recorded once per attempt on that attempt's own aggregate, alongside (never instead of)
    /// <see cref="AttemptDiffRecordedType"/> and <see cref="AttemptToolUseRecordedType"/>. Never a
    /// transition (no <see cref="ToStateArgument"/>) and never folded into any snapshot, exactly like
    /// both siblings: token accounting is durable timeline/audit content, not workflow state — nothing
    /// in the scheduler, an executor, or a prerequisite check decides anything from it. Exactly one
    /// event per attempt, which here is not merely a cost decision but the shape of the data itself:
    /// each provider reports usage on exactly one terminal stream event per run. The per-field detail
    /// rides on this event's <see cref="Payload"/>.</summary>
    public const string AttemptUsageRecordedType = "AttemptUsageRecorded";

    /// <summary>Carried on an <see cref="AttemptUsageRecordedType"/> event: input plus output tokens
    /// as a base-10 integer. Derived from the event's own <see cref="Payload"/> by the single producing
    /// store method, never supplied independently, so the rendered summary and the structured payload
    /// cannot drift.
    ///
    /// A field the provider did not report contributes 0 to this ONE-LINE SUMMARY, which is the only
    /// place that substitution happens: <see cref="UsagePayload"/> itself keeps the honest
    /// <see langword="null"/>, so a machine consumer can always still tell "reported zero" from "never
    /// reported". The event is not written at all unless at least one field was reported (see
    /// <c>ProviderUsageReport.ToPayload</c>), so this summary can never be an all-zero line standing in
    /// for an observation that never happened.</summary>
    /// <remarks>Named `usage_total` rather than the obvious `total_tokens`, and the same for its two
    /// siblings: <see cref="Forge.Infrastructure.SecretRedactor"/> redacts an entry whose KEY NAME
    /// merely contains `token` (an unanchored match, so `input_tokens` matches as readily as
    /// `api_token`), and <c>SprintTimelineProjector.ToItem</c> runs every event's flat arguments through
    /// that check. An argument named `*_tokens` therefore renders as `[REDACTED:token]` on every
    /// surface — caught here by the CLI acceptance test, which asserted the real numbers. The
    /// structured payload's own field names are unaffected and stay explicit
    /// (`payload.usage.input_tokens`): it is redacted by the typed
    /// <c>SprintTimelineRedaction.RedactPayload</c>, never by the name-matching properties pass. The
    /// word "token" still appears in the rendered sentence, from the localized template rather than
    /// from a key.</remarks>
    public const string UsageTotalTokensArgument = "usage_total";

    /// <summary>Carried on an <see cref="AttemptUsageRecordedType"/> event alongside
    /// <see cref="UsageTotalTokensArgument"/>, under the same reported-as-0 rule and the same
    /// no-`token`-in-the-key rule: input tokens, base-10.</summary>
    public const string UsageInputTokensArgument = "usage_input";

    /// <summary>Carried on an <see cref="AttemptUsageRecordedType"/> event alongside
    /// <see cref="UsageTotalTokensArgument"/>, under the same reported-as-0 rule and the same
    /// no-`token`-in-the-key rule: output tokens, base-10.</summary>
    public const string UsageOutputTokensArgument = "usage_output";
}

public sealed record SprintWorkflowState(
    SprintSnapshot Sprint,
    IReadOnlyDictionary<string, NodeSnapshot> Nodes,
    IReadOnlyDictionary<string, AttemptSnapshot> Attempts,
    long LastSequence);

/// <summary>Converts enum state names to/from the lower snake_case wire form used everywhere.</summary>
public static class WorkflowStateNames
{
    public static string ToSnakeCase<TState>(TState state) where TState : struct, Enum =>
        string.Concat(
            state.ToString().Select(
                (character, index) =>
                    char.IsUpper(character) && index > 0
                        ? $"_{char.ToLowerInvariant(character)}"
                        : char.ToLowerInvariant(character).ToString()));

    public static TState Parse<TState>(string value) where TState : struct, Enum
    {
        foreach (TState candidate in Enum.GetValues<TState>())
        {
            if (ToSnakeCase(candidate) == value)
            {
                return candidate;
            }
        }

        throw new FormatException($"'{value}' is not a known {typeof(TState).Name}.");
    }
}

/// <summary>
/// Pure reconstruction of current sprint/node/attempt state from the durable event stream. The
/// event log is the sole source of truth; nothing here reads or writes a file.
/// </summary>
public static class WorkflowFold
{
    public static SprintWorkflowState Apply(SprintId sprintId, IReadOnlyList<WorkflowEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        SprintSnapshot? sprint = null;
        Dictionary<string, NodeSnapshot> nodes = new(StringComparer.Ordinal);
        Dictionary<string, AttemptSnapshot> attempts = new(StringComparer.Ordinal);
        foreach (WorkflowEvent current in events)
        {
            if (current.Type == WorkflowEvent.AttemptActivityRecordedType)
            {
                // Validated like every other envelope (throws loudly on corruption) but never a
                // transition: it must never gate on or advance a state-machine version. Applied only
                // while the attempt is still non-terminal — the authoritative, race-free half of
                // "never resurrects a settled attempt": a heartbeat that lands after a concurrent
                // completion (append-time only checks the attempt was non-terminal at read time) is
                // silently dropped here on replay instead of leaving a stray post-terminal timestamp.
                _ = IsTransitionRecord(current);
                if (attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? activeAttempt) &&
                    !WorkflowStateMachines.IsTerminal(activeAttempt.State))
                {
                    AttemptActivityKind? kind =
                        current.Arguments.TryGetValue(WorkflowEvent.AttemptActivityKindArgument, out string? kindText) &&
                            kindText is not null
                            ? WorkflowStateNames.Parse<AttemptActivityKind>(kindText)
                            : null;
                    attempts[current.Aggregate.Id] =
                        activeAttempt with { LastActivityAt = current.OccurredAt, LastActivityKind = kind };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.AttemptSupersededType)
            {
                // Validated (throws loudly on corruption) but, like an activity event, never a
                // transition and never projected into the folded snapshot itself: the bounded
                // instruction it carries is durable audit content, not workflow state.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.AttemptStopRequestedType)
            {
                // Validated like every other envelope, never a transition -- but unlike
                // AttemptSuperseded, this one IS projected: StopRequestedAt must be directly
                // queryable so an executor or restart-recovery pass can ask "does this attempt
                // already carry a stop intent" without re-scanning the raw journal.
                _ = IsTransitionRecord(current);
                if (attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? stoppingAttempt))
                {
                    attempts[current.Aggregate.Id] = stoppingAttempt with { StopRequestedAt = current.OccurredAt };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.AttemptStopConvergedType)
            {
                // Same treatment as AttemptStopRequestedType, for the same reason: projected into
                // AttemptSnapshot.StopConvergedAt so an executor can tell a fully-converged stop
                // apart from one still needing FinishStopAsync, directly from durable state.
                _ = IsTransitionRecord(current);
                if (attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? convergedAttempt))
                {
                    attempts[current.Aggregate.Id] = convergedAttempt with { StopConvergedAt = current.OccurredAt };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.UserMessagePostedType)
            {
                // Validated (throws loudly on corruption) but, like AttemptSupersededType, never a
                // transition and never projected into the folded snapshot itself: the bounded message
                // it carries is durable timeline content, not workflow state.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.AgentSummaryRecordedType)
            {
                // Validated (throws loudly on corruption) but, like UserMessagePostedType, never a
                // transition and never projected into the folded snapshot itself: the summary it
                // carries is durable timeline content, not workflow state.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.AttemptDiffRecordedType)
            {
                // Validated (throws loudly on corruption) but, like AttemptSupersededType, never a
                // transition and never projected into the folded snapshot itself: an attempt's diff
                // statistics are durable timeline content, not workflow state -- nothing in the
                // scheduler, an executor, or a prerequisite check ever needs to ask "what did this
                // attempt change" to decide what happens next.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.AttemptToolUseRecordedType)
            {
                // Same treatment as AttemptDiffRecordedType, for the same reason: what an attempt's
                // provider did is durable timeline content, not workflow state -- nothing in the
                // scheduler, an executor, or a prerequisite check ever decides anything from it.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.AttemptUsageRecordedType)
            {
                // Same treatment as AttemptDiffRecordedType/AttemptToolUseRecordedType, for the same
                // reason: what an attempt's provider spent is durable timeline content, not workflow
                // state -- nothing in the scheduler, an executor, or a prerequisite check decides
                // anything from it.
                _ = IsTransitionRecord(current);
                continue;
            }

            if (current.Type == WorkflowEvent.StageRevisionRecordedType)
            {
                // Never a transition (no ToStateArgument) -- but projected, like
                // AttemptStopRequestedType: every prerequisite check and node-role executor must be
                // able to read a sprint's current stage revision directly from its snapshot, not by
                // re-scanning the raw journal. Round 2 review of PR #96 (critical): also projects the
                // durable "unconverged rewind in progress" marker (target/reason/idempotency key) --
                // cleared only by StageTransitionConvergedType below, independent of whatever this
                // saga's own later steps do to node/sprint state.
                _ = IsTransitionRecord(current);
                if (sprint is not null)
                {
                    int revisionValue = int.Parse(
                        current.Arguments[WorkflowEvent.RevisionArgument]!, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    sprint = sprint with
                    {
                        Revision = new(revisionValue),
                        PendingRewindTargetStageId = current.Arguments[WorkflowEvent.TargetStageIdArgument],
                        PendingRewindReason = current.Arguments[WorkflowEvent.RewindReasonArgument],
                        PendingRewindIdempotencyKey = Guid.Parse(current.Arguments[WorkflowEvent.IdempotencyKeyArgument]!),
                    };
                }

                continue;
            }

            if (current.Type == WorkflowEvent.StageTransitionConvergedType)
            {
                // Validated (throws loudly on corruption); the caller's own idempotency key itself is
                // never folded (looked up by scanning the raw journal --
                // FileSprintEventLog.TryGetConvergedStageTransitionAsync -- for the caller's own key,
                // since this is audit/replay-detection content, not workflow state). Round 2 review of
                // PR #96 (critical): this event's mere landing IS projected, though -- it clears
                // whatever unconverged-rewind marker StageRevisionRecordedType set above, since its
                // whole meaning is "the saga this marker described has now fully finished."
                _ = IsTransitionRecord(current);
                if (sprint is not null)
                {
                    sprint = sprint with
                    {
                        PendingRewindTargetStageId = null,
                        PendingRewindReason = null,
                        PendingRewindIdempotencyKey = null,
                    };
                }

                continue;
            }

            if (!IsTransitionRecord(current))
            {
                continue;
            }

            string toState = current.Arguments[WorkflowEvent.ToStateArgument]!;
            switch (current.Aggregate.Kind)
            {
                case AggregateKind.Sprint:
                    // Meaningful only for this event. Finding recovery deliberately carries its
                    // reason across the intermediate `ready` state so a crash can resume safely;
                    // other transitions omit it and clear the prior reason.
                    string? blockedReason = current.Arguments.TryGetValue(
                        WorkflowEvent.BlockedReasonArgument,
                        out string? blockedReasonValue)
                        ? blockedReasonValue
                        : null;
                    // Carried forward from whatever StageRevisionRecordedType/StageTransitionConvergedType
                    // last set (never produced by an ordinary sprint transition) -- an ordinary
                    // transition must never reset a sprint's own revision counter back to Initial, nor
                    // silently clear or fabricate an in-flight rewind marker.
                    sprint = new(
                        sprintId,
                        WorkflowStateNames.Parse<SprintState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        blockedReason,
                        sprint?.Revision ?? default,
                        sprint?.PendingRewindTargetStageId,
                        sprint?.PendingRewindReason,
                        sprint?.PendingRewindIdempotencyKey);
                    break;
                case AggregateKind.Node:
                    nodes.TryGetValue(current.Aggregate.Id, out NodeSnapshot? previousNode);
                    int attemptCount = current.Arguments.TryGetValue(
                        WorkflowEvent.AttemptNumberArgument,
                        out string? countText) && countText is not null
                        ? int.Parse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture)
                        : previousNode?.AttemptCount ?? 0;
                    string? currentAttemptId = current.Arguments.TryGetValue(
                        WorkflowEvent.CurrentAttemptIdArgument,
                        out string? currentAttemptIdValue) && currentAttemptIdValue is not null
                        ? currentAttemptIdValue
                        : previousNode?.CurrentAttemptId;
                    // Carried only on the rewind coordinator's own reopen/invalidate transitions
                    // (`succeeded -> ready`/`succeeded -> pending`/`failed -> pending`/
                    // `awaiting_human -> pending`); every ordinary transition omits it and this node
                    // simply keeps whatever revision it already belonged to.
                    StageRevision nodeRevision = current.Arguments.TryGetValue(
                        WorkflowEvent.RevisionArgument,
                        out string? revisionText) && revisionText is not null
                        ? new(int.Parse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture))
                        : previousNode?.Revision ?? default;
                    nodes[current.Aggregate.Id] = new(
                        new(current.Aggregate.Id),
                        WorkflowStateNames.Parse<NodeState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        attemptCount,
                        currentAttemptId,
                        nodeRevision);
                    break;
                case AggregateKind.Attempt:
                    attempts.TryGetValue(current.Aggregate.Id, out AttemptSnapshot? previousAttempt);
                    string? nodeId = current.Arguments.TryGetValue(
                        WorkflowEvent.NodeIdArgument,
                        out string? nodeIdValue) && nodeIdValue is not null
                        ? nodeIdValue
                        : previousAttempt?.NodeId;
                    string? targetOutcome = current.Arguments.TryGetValue(
                        WorkflowEvent.TargetOutcomeArgument,
                        out string? targetOutcomeValue) && targetOutcomeValue is not null
                        ? targetOutcomeValue
                        : previousAttempt?.TargetOutcome;
                    string? baseCommit = current.Arguments.TryGetValue(
                        WorkflowEvent.BaseCommitArgument,
                        out string? baseCommitValue) && baseCommitValue is not null
                        ? baseCommitValue
                        : previousAttempt?.BaseCommit;
                    AttemptId? supersedesAttemptId = current.Arguments.TryGetValue(
                        WorkflowEvent.SupersedesAttemptIdArgument,
                        out string? supersedesValue) && supersedesValue is not null
                        ? new AttemptId(Guid.Parse(supersedesValue))
                        : previousAttempt?.SupersedesAttemptId;
                    string? provider = current.Arguments.TryGetValue(
                        WorkflowEvent.ProviderArgument,
                        out string? providerValue) && providerValue is not null
                        ? providerValue
                        : previousAttempt?.Provider;
                    string? model = current.Arguments.TryGetValue(
                        WorkflowEvent.ModelArgument,
                        out string? modelValue) && modelValue is not null
                        ? modelValue
                        : previousAttempt?.Model;
                    attempts[current.Aggregate.Id] = new(
                        new(Guid.Parse(current.Aggregate.Id)),
                        WorkflowStateNames.Parse<AttemptState>(toState),
                        current.Aggregate.Version,
                        current.OccurredAt,
                        nodeId,
                        targetOutcome,
                        previousAttempt?.LastActivityAt,
                        previousAttempt?.LastActivityKind,
                        baseCommit,
                        supersedesAttemptId,
                        previousAttempt?.StopRequestedAt,
                        previousAttempt?.StopConvergedAt,
                        provider,
                        model);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unknown aggregate kind '{current.Aggregate.Kind}'.");
            }
        }

        return new(
            sprint ?? throw new InvalidDataException("A sprint event stream must contain a sprint event."),
            nodes,
            attempts,
            events.Count == 0 ? -1 : events[^1].Sequence);
    }

    internal static bool IsTransitionRecord(WorkflowEvent current)
    {
        bool hasState = current.Arguments.TryGetValue(WorkflowEvent.ToStateArgument, out string? toState) &&
            toState is not null;
        if (current.Type == WorkflowEvent.AttemptActivityRecordedType)
        {
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt
                ? throw new InvalidDataException($"Activity event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptSupersededType)
        {
            bool hasInstruction = current.Arguments.TryGetValue(
                WorkflowEvent.SupersessionInstructionArgument, out string? instruction) && instruction is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt || !hasInstruction
                ? throw new InvalidDataException($"Supersession event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptStopRequestedType)
        {
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt
                ? throw new InvalidDataException($"Stop-request event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptStopConvergedType)
        {
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt
                ? throw new InvalidDataException($"Stop-converged event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.UserMessagePostedType)
        {
            bool hasText = current.Arguments.TryGetValue(
                WorkflowEvent.UserMessageTextArgument, out string? text) && text is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Sprint || !hasText
                ? throw new InvalidDataException($"User-message event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AgentSummaryRecordedType)
        {
            bool hasSummary = current.Arguments.TryGetValue(
                WorkflowEvent.AgentSummaryTextArgument, out string? summary) && summary is not null;
            // CorrelationId (not an Arguments entry) carries the owning Handoff.HandoffId -- required
            // so a later rewind's supersession of that Handoff can still be matched back to this
            // already-landed, immutable event (see SprintTimelineProjector.MergeAndPage).
            return hasState || current.Aggregate.Kind != AggregateKind.Node || !hasSummary ||
                current.CorrelationId is null
                ? throw new InvalidDataException($"Agent-summary event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptDiffRecordedType)
        {
            // The payload is what makes this event type meaningful at all -- an AttemptDiffRecorded
            // with no `payload.diff` carries nothing a reader could use, so it is corruption, not a
            // degraded-but-usable record. The three summary arguments are validated too (not merely
            // derived and trusted): they are what the localized timeline template substitutes, and a
            // line hand-edited or written by an older/foreign producer must fail here rather than
            // render as a diff summary with blanks where its counts belong.
            bool hasDiff = current.Payload?.Diff is not null;
            bool hasCounts =
                current.Arguments.GetValueOrDefault(WorkflowEvent.DiffFilesChangedArgument) is not null &&
                current.Arguments.GetValueOrDefault(WorkflowEvent.DiffInsertionsArgument) is not null &&
                current.Arguments.GetValueOrDefault(WorkflowEvent.DiffDeletionsArgument) is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt || !hasDiff || !hasCounts
                ? throw new InvalidDataException($"Attempt-diff event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptToolUseRecordedType)
        {
            // Fails closed exactly like AttemptDiffRecordedType above: an AttemptToolUseRecorded with
            // no `payload.tool_use` carries nothing a reader could use, and the three summary
            // arguments are what the localized template substitutes, so a line hand-edited or written
            // by a foreign producer must fail here rather than render with blanks where its counts
            // belong.
            bool hasToolUse = current.Payload?.ToolUse is not null;
            bool hasCounts =
                current.Arguments.GetValueOrDefault(WorkflowEvent.ToolCallsArgument) is not null &&
                current.Arguments.GetValueOrDefault(WorkflowEvent.ToolCommandsArgument) is not null &&
                current.Arguments.GetValueOrDefault(WorkflowEvent.ToolEditsArgument) is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt || !hasToolUse || !hasCounts
                ? throw new InvalidDataException($"Attempt-tool-use event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.AttemptUsageRecordedType)
        {
            // Fails closed exactly like AttemptDiffRecordedType/AttemptToolUseRecordedType above: an
            // AttemptUsageRecorded with no `payload.usage` carries nothing a reader could use, and the
            // three summary arguments are what the localized template substitutes, so a line
            // hand-edited or written by a foreign producer must fail here rather than render with
            // blanks where its counts belong.
            bool hasUsage = current.Payload?.Usage is not null;
            bool hasCounts =
                current.Arguments.GetValueOrDefault(WorkflowEvent.UsageTotalTokensArgument) is not null &&
                current.Arguments.GetValueOrDefault(WorkflowEvent.UsageInputTokensArgument) is not null &&
                current.Arguments.GetValueOrDefault(WorkflowEvent.UsageOutputTokensArgument) is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Attempt || !hasUsage || !hasCounts
                ? throw new InvalidDataException($"Attempt-usage event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.StageRevisionRecordedType)
        {
            bool hasRevision = current.Arguments.TryGetValue(
                WorkflowEvent.RevisionArgument, out string? revision) && revision is not null;
            bool hasTarget = current.Arguments.TryGetValue(
                WorkflowEvent.TargetStageIdArgument, out string? target) && target is not null;
            bool hasReason = current.Arguments.TryGetValue(
                WorkflowEvent.RewindReasonArgument, out string? reason) && reason is not null;
            // Round 2 review of PR #96 (critical): the caller's own idempotency key must be durable
            // on this event too (not only on StageTransitionConvergedType), so a resumed retry can
            // recover the exact key the original, still-unconverged commit used.
            bool hasKey = current.Arguments.TryGetValue(
                WorkflowEvent.IdempotencyKeyArgument, out string? key) && key is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Sprint ||
                !hasRevision || !hasTarget || !hasReason || !hasKey
                ? throw new InvalidDataException($"Stage-revision event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type == WorkflowEvent.StageTransitionConvergedType)
        {
            bool hasKey = current.Arguments.TryGetValue(
                WorkflowEvent.IdempotencyKeyArgument, out string? key) && key is not null;
            return hasState || current.Aggregate.Kind != AggregateKind.Sprint || !hasKey
                ? throw new InvalidDataException(
                    $"Stage-transition-converged event '{current.EventId}' has an invalid envelope.")
                : false;
        }

        if (current.Type != WorkflowEvent.RouteDecisionRecordedType)
        {
            return hasState
                ? true
                : throw new InvalidDataException(
                    $"Transition event '{current.EventId}' is missing '{WorkflowEvent.ToStateArgument}'.");
        }

        if (hasState || current.Aggregate.Kind != AggregateKind.Sprint || current.Aggregate.Version < 1 ||
            current.MessageKey != "routing.decision_recorded")
        {
            throw new InvalidDataException($"Routing event '{current.EventId}' has an invalid envelope.");
        }

        string Required(string key) => current.Arguments.GetValueOrDefault(key) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Routing event '{current.EventId}' is missing '{key}'.");
        _ = Guid.Parse(Required("attempt_id"));
        _ = Required("node_id");
        _ = Required("provider");
        _ = Required("model");
        _ = Required("surface");
        _ = WorkflowStateNames.Parse<RouteOutcome>(Required("outcome"));
        if (current.Arguments.GetValueOrDefault("failure_class") is { } failure)
        {
            _ = WorkflowStateNames.Parse<FailureClass>(failure);
        }

        return false;
    }
}
