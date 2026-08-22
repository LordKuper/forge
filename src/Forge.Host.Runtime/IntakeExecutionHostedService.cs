using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Host;

/// <summary>
/// How often <see cref="IntakeExecutionHostedService"/> sweeps for runnable `intake` nodes.
/// Configurable only so a test can prove the loop itself fires without waiting out the real
/// interval.
/// </summary>
public sealed record IntakeExecutionOptions(string ProjectRoot, TimeSpan? PollInterval = null)
{
    /// <summary>Deliberately the same 15 seconds <see cref="ResumeSchedulerHostedService"/> uses:
    /// both services answer the same question ("is durable state sitting on progress nothing else
    /// will make?") and a second, differently-tuned number would need a reason this slice does not
    /// have. Intake is cheap (a `.forge/` parse plus an in-memory digest), so a shorter interval
    /// would buy latency nothing needs; a longer one would leave a freshly-run sprint visibly idle.
    /// </summary>
    public TimeSpan Interval => PollInterval ?? TimeSpan.FromSeconds(15);
}

/// <summary>
/// Executes the one <see cref="NodeRole.Intake"/> node of every running sprint in this Host's
/// project — and nothing else. This is Stage 11's first node-executor slice, deliberately scoped to
/// the single Work role that needs no provider, prompt, deadline, routing decision, or worktree:
/// <see cref="ExecutionProfilePolicy.PhaseFor"/> returns <see langword="null"/> for
/// <see cref="NodeRole.Intake"/>, so its whole job is deterministic — parse the project's
/// `.forge/` documents (ADR 0009) and freeze the sprint's reproducible context manifest (ADR 0012).
/// Every model-bearing role (planning, implementation, review) and every other Work role
/// (confirmation, test-work, finalization) still has no executor; see ADR 0028.
/// </summary>
/// <remarks>
/// The first code in this repository that mutates durable workflow state without a human command
/// behind it, so both durability properties are load-bearing rather than polish:
/// <list type="bullet">
/// <item>Holds no per-sprint memory. Every tick re-derives entirely from durable state — a Host
/// restart mid-attempt loses nothing.</item>
/// <item>Crash-resumable through <see cref="SprintScheduler"/>'s own idempotency, not a second
/// mechanism of this service's own: <see cref="SprintScheduler.StartAttemptAsync"/> hands back the
/// already-recorded attempt id for a node it already moved to `running`, and
/// <see cref="SprintScheduler.CompleteAttemptAsync"/> treats a replay against an already-saved
/// <see cref="NodeResult"/> as done rather than as a conflict. A tick that dies between the two
/// therefore finishes on the next tick instead of duplicating or wedging anything.</item>
/// </list>
/// <see cref="ControlPlaneHostedService"/> owns this service's lifetime directly — starting it only
/// after winning the project lease and stopping it before releasing that lease — so a Host that
/// loses the lease race never executes a node against durable state it does not own.
/// </remarks>
public sealed class IntakeExecutionHostedService(
    IntakeExecutionOptions options,
    ISprintStore store,
    SprintScheduler scheduler,
    ForgeApplication application,
    IConfigurationRegistry registry,
    StopOperationCoordinator stopCoordinator,
    ILogger<IntakeExecutionHostedService> logger) : BackgroundService
{
    /// <summary>Re-exported for this service's own existing tests/callers; the real value now lives
    /// on <see cref="TokenBudgetResolver.DefaultTokenBudget"/>, shared with every other node
    /// executor that resolves `context.token_budget` (ADR 0029) — see there for the fallback's own
    /// justification.</summary>
    public const int DefaultTokenBudget = TokenBudgetResolver.DefaultTokenBudget;

    /// <summary><see cref="NodeDiagnostic.Category"/> for a `.forge/` parse failure recorded against
    /// a succeeded intake node — the free-form lowercase convention
    /// <see cref="SprintScheduler.DeferAttemptAsync"/>'s own `"provider"` category established.</summary>
    private const string DocumentDiagnosticCategory = "context";

    /// <summary>No diagnostic code is reserved for a budget-truncated context item anywhere in this
    /// repository (unlike a `.forge/` parse failure, which reuses <c>ForgeDocumentError.DiagnosticCode</c>
    /// verbatim) — this is the first caller that needs one. `context_item_truncated` follows the
    /// existing `snake_case` diagnostic-code convention.</summary>
    private const string TruncatedDiagnosticCode = "context_item_truncated";

    private static readonly Action<ILogger, Exception> LogListFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2030, "IntakeExecutionListFailed"),
        "Executing intake nodes failed while listing this project's sprints; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogSprintFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2031, "IntakeExecutionSprintFailed"),
        "Executing the intake node failed for sprint {SprintId}; continuing with the rest.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogStartRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2032, "IntakeExecutionStartRejected"),
            "Starting the intake attempt for sprint {SprintId} was rejected ({DiagnosticCode}); retrying next tick.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogCompleteRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2033, "IntakeExecutionCompleteRejected"),
            "Completing the intake attempt for sprint {SprintId} was rejected ({DiagnosticCode}); retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception?> LogDefinitionUnusable = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2034, "IntakeExecutionDefinitionUnusable"),
        "Sprint {SprintId}'s frozen definition is missing the identity a context manifest needs; " +
            "its intake node cannot be executed.");

    /// <summary>Ticks immediately on start, matching <see cref="ResumeSchedulerHostedService"/>
    /// rather than <see cref="NotificationDeliveryHostedService"/>: an unexecuted intake node is a
    /// sprint making no progress at all, not a best-effort side channel, so waiting out a full
    /// interval after a Host restart would be a real (if bounded) stall.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Interval);
        do
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SprintId> sprintIds;
        try
        {
            sprintIds = await store.ListAsync(options.ProjectRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogListFailed(logger, exception);
            return;
        }

        // Every eligible sprint every tick, isolating per-sprint failures — the same shape
        // ResumeSchedulerHostedService uses. Executing only one sprint's intake per tick would make
        // a project's startup latency scale with its sprint count for no benefit: intake does no
        // provider work and holds no exclusive resource, so there is nothing here to serialize.
        foreach (SprintId sprintId in sprintIds)
        {
            try
            {
                await ExecuteIntakeAsync(sprintId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or FormatException
                or ArgumentNullException or NullReferenceException or OverflowException
                or KeyNotFoundException)
            {
                // Matches ResumeSchedulerHostedService's filter for the shared reasons (IOException/
                // UnauthorizedAccessException/InvalidOperationException/InvalidDataException: one
                // sprint's unreadable journal or definition, or a race with its own concurrent
                // deletion, must never stop every other sprint's intake from running), widened past
                // it for one this service alone needs.
                //
                // Round 7 review found an eleventh instance of a defect class six prior rounds spent
                // patching one call site at a time inside FileSprintEventLog's Persisted* DTO reads:
                // SprintScheduler.StartAttemptAsync's `running`-node resume path parses
                // NodeSnapshot.CurrentAttemptId with a bare Guid.Parse — that value is a free-form
                // event-journal argument (event.schema.json types it as string|number|boolean|null,
                // never validated as a GUID), not a Persisted* DTO field, so no amount of auditing
                // FileSprintEventLog's own deserialization would ever have caught it. That is the
                // actual lesson eleven rounds converge on: this service cannot enumerate every way a
                // *different* corrupt durable-state shape could reach it through SprintScheduler's own
                // internals, which this service does not own and must not have to re-audit every time
                // they change. The service's own outer per-sprint boundary — the one place every one
                // of the eleven instances escaped through, regardless of which inner method or file
                // threw — is caught here instead: every exception type any instance has actually
                // produced (FormatException, ArgumentNullException, NullReferenceException,
                // OverflowException, KeyNotFoundException), on top of the corrupt-durable-state
                // exceptions ResumeSchedulerHostedService's own precedent already established. This
                // does not replace FileSprintEventLog's own per-method normalization (ADR 0028's still-
                // deferred audit remains worth doing, for better error messages and because other
                // future callers benefit from it too) — it is the backstop for whatever that audit,
                // wherever it eventually lands, has not yet reached.
                LogSprintFailed(logger, sprintId.Value, exception);
            }
        }
    }

    private async Task ExecuteIntakeAsync(SprintId sprintId, CancellationToken cancellationToken)
    {
        // Called here rather than relied upon from ResumeSchedulerHostedService's own tick: the two
        // services run on independent timers, so this one must not assume a promotion already
        // happened this tick. AdvanceGraphAsync is idempotent, so calling it from both is free.
        SprintWorkflowState state = await scheduler
            .AdvanceGraphAsync(options.ProjectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Sprint.State != SprintState.Running)
        {
            // A draft sprint's dependency-free intake node is already `ready` (AdvanceGraphAsync
            // promotes regardless of sprint state), but StartAttemptAsync would refuse it with
            // `sprint_not_running`. Skipping quietly here keeps that ordinary, expected state from
            // logging a rejection every interval for the life of the sprint.
            return;
        }

        SprintDefinition? definition = await store
            .LoadDefinitionAsync(options.ProjectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }

        // Found by role, never by the `"intake"` literal: a sprint created with a custom graph (the
        // shape most of this repository's own tests use) legitimately has no intake node at all, and
        // a node that merely happens to be named `intake` without the role carries none of the
        // guarantees this executor depends on — above all that it needs no execution profile.
        NodeDefinition? intake = definition.Graph.FirstOrDefault(
            node => node.Role == NodeRole.Intake && node.Kind == NodeKind.Work);
        if (intake is null || !state.Nodes.TryGetValue(intake.Id, out NodeSnapshot? node))
        {
            return;
        }

        // `running` is not skipped as "someone else's work": nothing else in this codebase starts a
        // Work node's attempt, so a `running` intake node can only be this service's own attempt
        // interrupted before it completed. Resuming it is the entire crash-recovery path — skipping
        // it would strand the node forever, since no other verb moves a `running` node onward.
        if (node.State is not (NodeState.Ready or NodeState.Running))
        {
            return;
        }

        // Plan section 7.3: check the durable stop intent before resuming an attempt. Intake never
        // registers with ActiveOperationRegistry (it invokes no provider/process), but a stop
        // request could in principle still land against it mid-tick; without this check its stop
        // intent would be recorded but never converged, wedging the sprint at `running` forever.
        if (node.State == NodeState.Running && node.CurrentAttemptId is { } stoppingAttemptIdText &&
            state.Attempts.TryGetValue(stoppingAttemptIdText, out AttemptSnapshot? stoppingAttempt) &&
            stoppingAttempt.StopRequestedAt is not null)
        {
            Guid stoppingProjectId = await ProjectIdentity
                .ReadProjectIdAsync(options.ProjectRoot, registry, cancellationToken).ConfigureAwait(false);
            await stopCoordinator.FinishStopAsync(
                options.ProjectRoot, sprintId, stoppingProjectId, intake.Id,
                new AttemptId(Guid.Parse(stoppingAttemptIdText)), cancellationToken).ConfigureAwait(false);
            return;
        }

        // ContextManifestCompiler.Compile throws ArgumentException for a blank identity field. That
        // is not in the tick's catch filter (and widening the filter would swallow genuine
        // programming errors elsewhere), so a definition damaged after freeze is rejected here
        // instead — the same "this sprint cannot be served, the rest still can" outcome, reached
        // deliberately.
        if (string.IsNullOrWhiteSpace(definition.BaseCommit) ||
            string.IsNullOrWhiteSpace(definition.Workflow) ||
            string.IsNullOrWhiteSpace(definition.WorkflowVersion))
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        StartAttemptResult started = await scheduler
            .StartAttemptAsync(options.ProjectRoot, sprintId, intake.Id, node.Version, cancellationToken)
            .ConfigureAwait(false);
        if (!started.Succeeded || started.AttemptId is not { } attemptId)
        {
            LogStartRejected(logger, sprintId.Value, started.DiagnosticCode, null);
            return;
        }

        // Parsed per sprint rather than once per tick: keeping the parse inside this sprint's own
        // failure boundary is worth more than sharing one result across the (normally at most one)
        // sprint per project with a runnable intake node.
        ForgeDocumentSet documents = await new ForgeDocumentCompiler()
            .ParseAsync(options.ProjectRoot, cancellationToken)
            .ConfigureAwait(false);
        int tokenBudget = await TokenBudgetResolver
            .ResolveAsync(application, options.ProjectRoot, cancellationToken).ConfigureAwait(false);
        ContextManifest manifest = ContextManifestCompiler.Compile(
            sprintId.Value,
            definition.BaseCommit,
            definition.Workflow,
            definition.WorkflowVersion,
            documents,
            tokenBudget);

        // The manifest digest is a pure function of the sprint's frozen identity, the token budget,
        // and every admitted item's own content digest (ADR 0012) — exactly what this attempt
        // consumed, so it is the attempt's `input_digest`. The admitted items' per-item digests are
        // its `outputs`: the content-addressed handles to what intake actually selected, in the
        // manifest's own deterministic rules-then-knowledge order. Both already have
        // `node-result.schema.json`'s required `sha256:<64 hex>` shape.
        List<string> outputs =
        [
            .. manifest.Layers.Rules.Select(item => item.Digest),
            .. manifest.Layers.Knowledge.Select(item => item.Digest),
        ];
        List<NodeDiagnostic> diagnostics =
        [
            .. documents.Errors.Select(ToDiagnostic),
            .. manifest.Truncated.Select(ToDiagnostic),
        ];
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            options.ProjectRoot,
            sprintId,
            intake.Id,
            attemptId,
            succeeded: true,
            manifest.ManifestDigest,
            outputs,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (!completed.Succeeded)
        {
            // Including WorkflowRecordInvalid: the next tick re-derives everything from durable
            // state and retries the same idempotent path, which is the only safe response — this
            // service must never leave a `running` node behind by throwing here.
            LogCompleteRejected(logger, sprintId.Value, completed.DiagnosticCode, null);
        }
    }

    /// <summary>A malformed `.forge/` document degrades intake's admitted context; it never fails
    /// the node (ADR 0028). Only the document's `.forge/`-relative path is carried as an argument —
    /// never the parser's own message, which can quote document content, and never an absolute
    /// path.</summary>
    private static NodeDiagnostic ToDiagnostic(ForgeDocumentError error) => new(
        error.DiagnosticCode,
        DocumentDiagnosticCategory,
        $"diagnostic.{error.DiagnosticCode}",
        new Dictionary<string, string?>(StringComparer.Ordinal) { ["relative_path"] = error.RelativePath });

    /// <summary>A budget-truncated document is degradation just like a parse error, and left the same
    /// way for the durable record instead of being silently dropped — the whole point of recording a
    /// `.forge/`-parse diagnostic one line above this is that a caller inspecting a "succeeded" intake
    /// node can tell it actually saw everything intended, and a truncated item is exactly the case
    /// where it did not.</summary>
    private static NodeDiagnostic ToDiagnostic(ContextManifestTruncatedItem item) => new(
        TruncatedDiagnosticCode,
        DocumentDiagnosticCategory,
        $"diagnostic.{TruncatedDiagnosticCode}",
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["relative_path"] = item.RelativePath,
            ["reason"] = item.Reason,
        });
}
