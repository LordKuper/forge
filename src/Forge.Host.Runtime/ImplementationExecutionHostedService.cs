using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;

namespace Forge.Host;

/// <summary>See <see cref="IntakeExecutionOptions"/> — the identical override shape, for the
/// identical reason (a test-overridable poll interval).</summary>
public sealed record ImplementationExecutionOptions(string ProjectRoot, TimeSpan? PollInterval = null)
{
    public TimeSpan Interval => PollInterval ?? TimeSpan.FromSeconds(15);
}

/// <summary>
/// Executes the one <see cref="NodeRole.Implementation"/> node of every running sprint in this
/// Host's project. Stage 11's third node-executor slice, and the first whose provider run is
/// meant to edit files: <see cref="PlanningExecutionHostedService"/> (ADR 0030) explicitly forbids
/// edits in its own prompt and always discards its attempt worktree, because its whole product is
/// a <see cref="Handoff"/> summary, not a diff. This service reads that real `Handoff`, invites the
/// provider to edit, and — if the attempt worktree ends up dirty — stages, commits
/// (<see cref="SprintGitIsolation.CommitAttemptAsync"/>, ADR 0031), and integrates the result into
/// the sprint's own integration branch (<see cref="SprintGitIsolation.IntegrateAsync"/>).
/// </summary>
/// <remarks>
/// Shares every prior node executor's two durability properties: no per-sprint memory (every tick
/// re-derives from durable state), and crash-resumability through <see cref="SprintScheduler"/>'s
/// own idempotency. Resuming an interrupted attempt re-invokes the provider from scratch inside the
/// same (idempotently recreated) attempt worktree — no partial provider transcript or partial edit
/// is trusted across a crash; if the provider left real, uncommitted edits behind before the crash,
/// the resumed run's own `git add -A` simply restages and re-commits them alongside whatever the
/// retried run adds, which is the honest behavior for an attempt worktree this class does not
/// otherwise inspect on resume.
///
/// A stale integration base (<see cref="DiagnosticCodes.WorktreeBaseMismatch"/> — something else
/// integrated into this sprint since the attempt worktree was created) is deliberately not resolved
/// in place with <see cref="SprintGitIsolation.RebaseAttemptAsync"/> in this slice: the attempt is
/// discarded and failed, and the scheduler's own bounded auto-retry mints a fresh attempt against
/// the now-current integration tip on the next tick — clean replay, not an in-place conflict
/// resolution this slice does not need yet (the built-in graph gives `implementation` no sibling
/// that could integrate concurrently with it today, so this path is a defensive guard, not a
/// commonly-taken one).
/// </remarks>
public sealed class ImplementationExecutionHostedService(
    ImplementationExecutionOptions options,
    ISprintStore store,
    SprintScheduler scheduler,
    SprintGitIsolation gitIsolation,
    IWorktreeManager worktrees,
    ProviderCatalog providers,
    IConfigurationRegistry registry,
    IEnvironmentPaths environmentPaths,
    ForgeApplication application,
    ActiveOperationRegistry activeOperations,
    StopOperationCoordinator stopCoordinator,
    ILogger<ImplementationExecutionHostedService> logger) : BackgroundService
{
    private const string FallbackSummary = "Implemented the requested change; the provider returned no summary.";

    private static readonly Action<ILogger, Exception> LogListFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2050, "ImplementationExecutionListFailed"),
        "Executing implementation nodes failed while listing this project's sprints; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogSprintFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2051, "ImplementationExecutionSprintFailed"),
        "Executing the implementation node failed for sprint {SprintId}; continuing with the rest.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogStartRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2052, "ImplementationExecutionStartRejected"),
            "Starting the implementation attempt for sprint {SprintId} was rejected ({DiagnosticCode}); " +
                "retrying next tick.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogCompleteRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2053, "ImplementationExecutionCompleteRejected"),
            "Completing the implementation attempt for sprint {SprintId} was rejected " +
                "({DiagnosticCode}); retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception?> LogDefinitionUnusable = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2054, "ImplementationExecutionDefinitionUnusable"),
        "Sprint {SprintId}'s frozen definition is missing the implementation execution profile, a " +
            "candidate provider for it, or planning's own handoff; its implementation node cannot " +
            "be executed.");

    private static readonly Action<ILogger, Guid, Exception?> LogWorktreeUnavailable =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2055, "ImplementationExecutionWorktreeUnavailable"),
            "Preparing an isolated attempt worktree for sprint {SprintId}'s implementation node " +
                "failed; the attempt is recorded as failed and will retry.");

    private static readonly Action<ILogger, Guid, Exception?> LogWorktreeDiscardFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2056, "ImplementationExecutionWorktreeDiscardFailed"),
            "Discarding sprint {SprintId}'s implementation attempt worktree did not fully succeed; " +
                "a future reconciliation pass must clean it up.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogIntegrationFailed =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2057, "ImplementationExecutionIntegrationFailed"),
            "Integrating sprint {SprintId}'s implementation commit failed ({DiagnosticCode}); the " +
                "attempt is recorded as failed and will retry.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogDiffStatUnavailable =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2058, "ImplementationExecutionDiffStatUnavailable"),
            "Recording the diff summary for sprint {SprintId}'s implementation attempt failed " +
                "({DiagnosticCode}); the change itself is already integrated, so only the timeline " +
                "entry is missing.");

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

        foreach (SprintId sprintId in sprintIds)
        {
            try
            {
                await ExecuteImplementationAsync(sprintId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or FormatException
                or ArgumentNullException or NullReferenceException or OverflowException
                or KeyNotFoundException or ArgumentOutOfRangeException or YamlException
                or JsonException or ConfigurationScopeException or Win32Exception)
            {
                // Matches PlanningExecutionHostedService's own widened filter (ADR 0030) — the
                // identical durable-state-corruption and infrastructure-failure exception shapes
                // reach this service through the same call chain (AdvanceGraphAsync,
                // LoadDefinitionAsync, StartAttemptAsync, CompleteAttemptAsync,
                // ProjectIdentity.ReadProjectIdAsync, SprintGitIsolation's underlying `git.exe`).
                LogSprintFailed(logger, sprintId.Value, exception);
            }
        }
    }

    private async Task ExecuteImplementationAsync(SprintId sprintId, CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await scheduler
            .AdvanceGraphAsync(options.ProjectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);

        SprintDefinition? definition = await store
            .LoadDefinitionAsync(options.ProjectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }

        NodeDefinition? implementation = definition.Graph.FirstOrDefault(
            candidate => candidate.Role == NodeRole.Implementation && candidate.Kind == NodeKind.Work);
        if (implementation is null || !state.Nodes.TryGetValue(implementation.Id, out NodeSnapshot? node))
        {
            return;
        }

        // Plan section 7.3 / ADR 0047 addendum (round 1/2 review of PR #95): checked from the node's
        // own CurrentAttemptId and the attempt's durable state, never from node.State == Running --
        // that id is set once by the node's own `running` transition and never cleared by any of
        // FinishStopAsync's later steps, so it still resolves to the stopping attempt even after a
        // Host crash has left the node `Failed` (mid-rearm) or already `Ready` (rearmed, sprint not
        // yet paused) -- states a Running-only gate can never see again, since nothing else revisits
        // a node once it leaves `Running` on its own. StopConvergedAt is what stops this from firing
        // again once the saga genuinely finished, even after a later, unrelated resume.
        if (node.CurrentAttemptId is { } stoppingAttemptIdText &&
            state.Attempts.TryGetValue(stoppingAttemptIdText, out AttemptSnapshot? stoppingAttempt) &&
            stoppingAttempt.StopRequestedAt is not null && stoppingAttempt.StopConvergedAt is null)
        {
            Guid stoppingProjectId = await ProjectIdentity
                .ReadProjectIdAsync(options.ProjectRoot, registry, cancellationToken).ConfigureAwait(false);
            await stopCoordinator.FinishStopAsync(
                options.ProjectRoot, sprintId, stoppingProjectId, implementation.Id,
                new AttemptId(Guid.Parse(stoppingAttemptIdText)), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (state.Sprint.State != SprintState.Running)
        {
            return;
        }

        if (node.State is not (NodeState.Ready or NodeState.Running))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(definition.BaseCommit) ||
            string.IsNullOrWhiteSpace(definition.Workflow) ||
            string.IsNullOrWhiteSpace(definition.WorkflowVersion))
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        // Checked before StartAttemptAsync, matching PlanningExecutionHostedService's own precedent:
        // a missing profile/provider must never leave a running, unrouted node with nothing to
        // complete it.
        if (!definition.ExecutionProfiles.TryGetValue(ExecutionPhase.Implementation, out ExecutionProfile? profile) ||
            !providers.TryGet(new ProviderId(profile.Provider), out ILlmProvider? provider))
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        // implementation cannot run without planning's real handoff -- there is nothing to
        // implement. Looked up by planning's own role, never a string literal, matching every other
        // node lookup in this file. A custom graph with no planning-role node, or one whose planning
        // node never recorded a handoff (still running, or every attempt failed), leaves this node
        // untouched at `ready` rather than starting an attempt with no plan to follow.
        string? planningNodeId = definition.Graph
            .FirstOrDefault(candidate => candidate.Role == NodeRole.Planning)?.Id;
        Handoff? handoff = planningNodeId is null
            ? null
            : (await scheduler.GetHandoffsAsync(options.ProjectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.NodeId.Value == planningNodeId);
        if (handoff is null)
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        StartAttemptResult started = await scheduler
            .StartAttemptAsync(options.ProjectRoot, sprintId, implementation.Id, node.Version, cancellationToken)
            .ConfigureAwait(false);
        if (!started.Succeeded || started.AttemptId is not { } attemptId)
        {
            LogStartRejected(logger, sprintId.Value, started.DiagnosticCode, null);
            return;
        }

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

        ImplementationAttemptOutcome outcome = await RunImplementationAttemptAsync(
                sprintId, attemptId, definition, profile, provider, documents, manifest, handoff, cancellationToken)
                .ConfigureAwait(false);
        if (outcome.Disposition == ImplementationAttemptDisposition.HostShuttingDown)
        {
            // Mirrors PlanningExecutionHostedService's own Cancelled handling: the node stays
            // `running`, resumed on the next tick after restart.
            return;
        }

        if (outcome.Disposition == ImplementationAttemptDisposition.StopRequested)
        {
            // Post-release audit (PR #101): a stop landed durably right as
            // the provider returned success, after the ActiveOperationRegistry unregister but before
            // the commit/integrate this method already performed internally --
            // RunImplementationAttemptAsync already discarded the worktree and skipped commit/
            // integrate before returning this disposition (see its own remarks). This attempt must
            // not be completed here: the node stays `running`, converged by
            // StopOperationCoordinator.FinishStopAsync's own top-of-tick check on this or the next
            // tick, exactly like the stop-landed-before-the-provider-started case.
            return;
        }

        CompleteAttemptResult completed = outcome.Disposition == ImplementationAttemptDisposition.RateLimited
            ? await scheduler.DeferAttemptAsync(
                options.ProjectRoot, sprintId, implementation.Id, attemptId, manifest.ManifestDigest,
                cancellationToken).ConfigureAwait(false)
            : await scheduler.CompleteAttemptAsync(
                options.ProjectRoot,
                sprintId,
                implementation.Id,
                attemptId,
                outcome.Disposition == ImplementationAttemptDisposition.Succeeded,
                manifest.ManifestDigest,
                outcome.Outputs,
                outcome.Diagnostics,
                cancellationToken).ConfigureAwait(false);
        if (!completed.Succeeded)
        {
            LogCompleteRejected(logger, sprintId.Value, completed.DiagnosticCode, null);
            return;
        }

        if (outcome.Disposition == ImplementationAttemptDisposition.Succeeded && outcome.Summary is not null)
        {
            string? nextNodeId = definition.Graph
                .FirstOrDefault(candidate => candidate.Role == NodeRole.Confirmation)?.Id;
            await scheduler.RecordHandoffAsync(
                options.ProjectRoot,
                sprintId,
                implementation.Id,
                definition.BaseCommit,
                outcome.Summary,
                decisions: [],
                openRisks: [],
                nextNodeIds: nextNodeId is null ? null : [nextNodeId],
                cancellationToken).ConfigureAwait(false);
            // Same accepted, named debt as PlanningExecutionHostedService: a failed
            // RecordHandoffAsync is not retried from here.
        }
    }

    private enum ImplementationAttemptDisposition
    {
        Succeeded,
        Failed,
        RateLimited,
        HostShuttingDown,

        /// <summary>Post-release audit (PR #101): a stop was durably requested
        /// for this attempt after the provider already returned success, discovered by a fresh
        /// re-check <see cref="RunImplementationAttemptAsync"/> performs twice: once right before its
        /// own commit step, and again right before <see cref="SprintGitIsolation.IntegrateAsync"/> --
        /// the actual publish to the sprint's integration branch (PR #101 review finding 2: the first
        /// check alone left the entire commit-duration window unguarded, since
        /// <see cref="SprintGitIsolation.CommitAttemptAsync"/> only writes the attempt's own worktree/
        /// branch, which a discard throws away wholesale). Together the two checks fully prevent the
        /// provider's work from reaching the integration branch; the residual window is only the
        /// ordinary async-scheduling gap between the second check's own read and the
        /// <c>IntegrateAsync</c> call immediately after it. The worktree is already discarded by the
        /// time this is returned.</summary>
        StopRequested,
    }

    private readonly record struct ImplementationAttemptOutcome(
        ImplementationAttemptDisposition Disposition,
        List<string> Outputs,
        List<NodeDiagnostic> Diagnostics,
        string? Summary)
    {
        public static readonly ImplementationAttemptOutcome Cancelled =
            new(ImplementationAttemptDisposition.HostShuttingDown, [], [], null);

        public static readonly ImplementationAttemptOutcome Stopped =
            new(ImplementationAttemptDisposition.StopRequested, [], [], null);

        public static ImplementationAttemptOutcome Failed(NodeDiagnostic diagnostic) =>
            new(ImplementationAttemptDisposition.Failed, [], [diagnostic], null);

        public static ImplementationAttemptOutcome RateLimitedFailure(NodeDiagnostic diagnostic) =>
            new(ImplementationAttemptDisposition.RateLimited, [], [diagnostic], null);

        public static ImplementationAttemptOutcome Success(string digest, string summary) =>
            new(ImplementationAttemptDisposition.Succeeded, [digest], [], summary);
    }

    private async Task<ImplementationAttemptOutcome> RunImplementationAttemptAsync(
        SprintId sprintId,
        AttemptId attemptId,
        SprintDefinition definition,
        ExecutionProfile profile,
        ILlmProvider provider,
        ForgeDocumentSet documents,
        ContextManifest manifest,
        Handoff handoff,
        CancellationToken cancellationToken)
    {
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(options.ProjectRoot, registry, cancellationToken).ConfigureAwait(false);

        GitOperationResult integration = await gitIsolation.EnsureIntegrationWorktreeAsync(
            options.ProjectRoot, projectId, sprintId, definition.BaseCommit, cancellationToken)
            .ConfigureAwait(false);
        if (!integration.Succeeded || integration.Commit is not { } expectedIntegrationTip)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return ImplementationAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", integration.DiagnosticCode, integration.Detail));
        }

        GitOperationResult attemptWorktree = await gitIsolation.CreateAttemptWorktreeAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken).ConfigureAwait(false);
        if (!attemptWorktree.Succeeded)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return ImplementationAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", attemptWorktree.DiagnosticCode, attemptWorktree.Detail));
        }

        string worktreePath = WorktreeLayout.AttemptPath(environmentPaths, projectId, sprintId, attemptId);

        string prompt = BuildPrompt(manifest, documents, handoff);
        AttemptSupervisionResult<ProviderRunResult> supervised;
        // Plan section 7.3: registered before provider/process execution, unregistered in `finally`
        // -- the exact attempt a stop request's ActiveOperationRegistry.TryCancel can reach.
        CancellationTokenSource operation = activeOperations.Register(attemptId, cancellationToken);
        try
        {
            using AttemptSupervisor supervisor = new(
                TimeSpan.FromSeconds(profile.SessionDeadlineSeconds),
                TimeSpan.FromSeconds(profile.IdleDeadlineSeconds),
                operation.Token);
            supervised = await supervisor.SuperviseAsync(async (token, onActivity) =>
            {
                try
                {
                    return await provider.RunAsync(prompt, worktreePath, token, onActivity)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return ProviderRunResult.Failed(ProviderFailureKind.Unknown, exception.Message);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            activeOperations.Unregister(attemptId);
        }

        if (supervised.Reason == AttemptTerminationReason.Cancelled)
        {
            // Deliberately skips the worktree discard below, matching PlanningExecutionHostedService:
            // the same cancellationToken that just cancelled the provider run would also cancel a
            // `git worktree remove` call made with it. Left for a future reconciliation pass.
            return ImplementationAttemptOutcome.Cancelled;
        }

        string? terminationCode = supervised.Reason switch
        {
            AttemptTerminationReason.IdleTimeout => ProviderDiagnosticCodes.IdleTimeout,
            AttemptTerminationReason.SessionTimeout => ProviderDiagnosticCodes.SessionTimeout,
            _ => null,
        };
        if (terminationCode is not null)
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ImplementationAttemptOutcome.Failed(NodeExecutionDiagnostics.Diagnostic("provider", terminationCode));
        }

        ProviderRunResult? result = supervised.Value;
        if (result is null || !result.Succeeded)
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            ProviderFailureKind failure = result?.Failure ?? ProviderFailureKind.Unknown;
            NodeDiagnostic diagnostic = NodeExecutionDiagnostics.Diagnostic(
                "provider", NodeExecutionDiagnostics.MapProviderFailure(failure), result?.Detail);
            return failure == ProviderFailureKind.RateLimited
                ? ImplementationAttemptOutcome.RateLimitedFailure(diagnostic)
                : ImplementationAttemptOutcome.Failed(diagnostic);
        }

        bool dirty = await worktrees.IsDirtyAsync(options.ProjectRoot, worktreePath, cancellationToken)
            .ConfigureAwait(false);
        if (!dirty)
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ImplementationAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", DiagnosticCodes.ImplementationNoChanges));
        }

        // Post-release audit (PR #101): the first of two re-checks (see the
        // second, right before IntegrateAsync below, added by PR #101 review finding 2) at which a
        // stop request racing against a provider that is about to succeed can still be honored -- the
        // durable stop intent's own per-tick check (top of ExecuteImplementationAsync) only runs
        // once, before the provider starts, and is never re-checked between the provider returning
        // and this method's own commit/integrate below. A fresh read here (never the value captured
        // before the provider ran) catches a stop that landed anywhere in between: the
        // ActiveOperationRegistry unregister above already ran, so this attempt's own cancellation
        // token cannot observe it, but the durable intent still can. Discarding here (unlike the
        // live-cancelled branch above) is safe: this attempt's own registered CancellationTokenSource
        // is already disposed, so `cancellationToken` here is the tick's own token, unaffected by the
        // stop's best-effort TryCancel. This check alone is not sufficient: it only guards the
        // upcoming CommitAttemptAsync call, not the IntegrateAsync call after it -- see the second
        // check below for why both are needed.
        if (await StopHasBeenRequestedAsync(sprintId, attemptId, cancellationToken).ConfigureAwait(false))
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ImplementationAttemptOutcome.Stopped;
        }

        string? summary = result.TerminalResult?.Summary;
        string effectiveSummary = string.IsNullOrWhiteSpace(summary) ? FallbackSummary : summary;
        GitOperationResult committed = await gitIsolation.CommitAttemptAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, CommitMessage(effectiveSummary), cancellationToken)
            .ConfigureAwait(false);
        if (!committed.Succeeded)
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ImplementationAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", committed.DiagnosticCode, committed.Detail));
        }

        // PR #101 review finding 2: CommitAttemptAsync only writes into this attempt's own
        // worktree/branch (which DiscardAsync throws away wholesale); IntegrateAsync immediately
        // below is the call that actually publishes to the sprint's shared integration branch, and
        // the check above -- taken before the commit -- left this entire commit duration (unbounded,
        // proportional to the size of the provider's own change) unguarded. This second, fresh read
        // is the genuinely narrowest point: discarding here is exactly as safe as discarding above,
        // since the commit was made to the attempt's own branch and was never published anywhere
        // else.
        if (await StopHasBeenRequestedAsync(sprintId, attemptId, cancellationToken).ConfigureAwait(false))
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ImplementationAttemptOutcome.Stopped;
        }

        // ADR 0059: read BEFORE IntegrateAsync, because a successful integrate discards this
        // attempt's own worktree -- the very worktree this read resolves against. Recorded only
        // after that integrate succeeds, though (below): a diff summary for work that never reached
        // the integration branch would be a durable claim about a change the sprint does not have.
        GitDiffStatResult diffStat = await gitIsolation.ReadDiffStatAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, expectedIntegrationTip,
            committed.Commit ?? expectedIntegrationTip, cancellationToken).ConfigureAwait(false);

        GitOperationResult integrated = await gitIsolation.IntegrateAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, expectedIntegrationTip, cancellationToken)
            .ConfigureAwait(false);
        if (!integrated.Succeeded)
        {
            // Unlike a successful integrate (which discards the attempt worktree itself), a failed
            // one leaves it behind -- this is the one path in this class that must discard
            // explicitly. A stale base (WorktreeBaseMismatch) is not resolved in place; see the
            // class remarks.
            LogIntegrationFailed(logger, sprintId.Value, integrated.DiagnosticCode, null);
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ImplementationAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", integrated.DiagnosticCode, integrated.Detail));
        }

        if (!integrated.CleanupSucceeded)
        {
            LogWorktreeDiscardFailed(logger, sprintId.Value, null);
        }

        await RecordAttemptDiffAsync(sprintId, attemptId, diffStat, cancellationToken).ConfigureAwait(false);

        return ImplementationAttemptOutcome.Success(
            NodeExecutionDiagnostics.Digest(integrated.Commit ?? string.Empty), effectiveSummary);
    }

    /// <summary>ADR 0059: one <see cref="WorkflowEvent.AttemptDiffRecordedType"/> event per attempt,
    /// appended once the attempt's commit is actually on the sprint's integration branch. Never
    /// fails the attempt: the change itself is already integrated and durable by this point, so a
    /// `git` or journal failure here costs an audit record, not work -- the same accepted, named debt
    /// <c>RecordHandoffAsync</c> already carries at this service's other post-success write. An
    /// attempt whose diff is genuinely empty is still recorded (an all-zero payload): "this attempt
    /// changed nothing" is itself a fact worth showing on the timeline, and skipping it would make an
    /// absent record ambiguous between "empty" and "recorded before this feature existed".</summary>
    private async Task RecordAttemptDiffAsync(
        SprintId sprintId, AttemptId attemptId, GitDiffStatResult diffStat, CancellationToken cancellationToken)
    {
        if (!diffStat.Succeeded || diffStat.Stat is not { } stat)
        {
            LogDiffStatUnavailable(logger, sprintId.Value, diffStat.DiagnosticCode, null);
            return;
        }

        try
        {
            await store.AppendAttemptDiffRecordedAsync(
                options.ProjectRoot, sprintId, attemptId, stat, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogDiffStatUnavailable(logger, sprintId.Value, DiagnosticCodes.WorktreeDiffFailed, exception);
        }
    }

    private async Task DiscardAsync(
        SprintId sprintId, Guid projectId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        bool discarded = await gitIsolation
            .DiscardAttemptAsync(options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        if (!discarded)
        {
            LogWorktreeDiscardFailed(logger, sprintId.Value, null);
        }
    }

    /// <summary>A fresh read, never a value captured before the provider ran -- but deliberately
    /// NOT the same clause as the top-of-tick stop convergence gate (above): that gate's own
    /// <c>StopConvergedAt is null</c> half exists only to stop <see cref="StopOperationCoordinator.FinishStopAsync"/>
    /// from re-firing forever once a stop has already fully converged, which does not apply at a
    /// point of no return. A genuinely concurrent second converger --
    /// <c>StageTransitionCoordinator.StopAndFailRunningNodeAsync</c>, a rewind's own step 1,
    /// running from a different thread/call path than this tick -- can append
    /// <c>StopConvergedAt</c> for this exact attempt while this method is mid-commit. Gating on
    /// <c>StopConvergedAt is null</c> here would then see the stop as "already handled" and let the
    /// attempt commit and integrate anyway (PR #101 review finding 1). A stop request in flight at
    /// all -- convergence status irrelevant -- means don't commit.</summary>
    private async Task<bool> StopHasBeenRequestedAsync(
        SprintId sprintId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        SprintWorkflowState? state = await store
            .LoadAsync(options.ProjectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        return state is not null &&
            state.Attempts.TryGetValue(attemptId.Value.ToString("D"), out AttemptSnapshot? attempt) &&
            attempt.StopRequestedAt is not null;
    }

    /// <summary>The commit's own subject line: the summary's first line, bounded well under any
    /// practical limit -- long enough to be useful, short enough that a provider's own verbose
    /// terminal text never produces an unreasonable `git log` entry.</summary>
    private static string CommitMessage(string summary)
    {
        // The first genuinely non-blank line, not merely the first line: a summary whose own first
        // line happens to be blank (but a later line has real content) must never collapse to an
        // empty subject -- `git commit -m ""` is rejected by git outright, which would discard a
        // real, already-verified-dirty edit as a failure purely because of the summary's own line
        // breaks. Falls back to the same fixed text an entirely blank summary already uses.
        string subject = summary
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? FallbackSummary;
        return Truncate(subject, 200);
    }

    /// <summary>Never splits a UTF-16 surrogate pair at the truncation boundary: cutting a string
    /// at a fixed code-unit count with a bare <see cref="ReadOnlySpan{T}"/> slice can land between a
    /// high and low surrogate, producing a malformed string with an unpaired high surrogate at its
    /// end -- exactly the kind of text a `git commit -m` argument must never carry.</summary>
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        int length = maxLength;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length), "…");
    }

    /// <summary>Unlike planning's prompt, this one invites edits and carries planning's own real
    /// <see cref="Handoff"/> ahead of the project's admitted rules/knowledge -- the plan is what
    /// this attempt is actually meant to carry out.</summary>
    private static string BuildPrompt(ContextManifest manifest, ForgeDocumentSet documents, Handoff handoff)
    {
        Dictionary<string, string> bodies = documents.Documents
            .GroupBy(document => document.RelativePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Body, StringComparer.Ordinal);
        StringBuilder builder = new();
        builder.Append(
            "You are the implementation phase of an automated software development sprint. A prior " +
            "planning phase already researched the change; carry it out. Edit, create, or delete " +
            "whatever files the change actually needs in this working directory. Do not commit your " +
            "own changes -- leave them uncommitted; Forge commits them for you once you finish.\n\n");
        builder.Append("## Plan\n").Append(handoff.Summary).Append('\n');
        AppendSection(builder, "Rules", manifest.Layers.Rules, bodies);
        AppendSection(builder, "Knowledge", manifest.Layers.Knowledge, bodies);
        return builder.ToString();
    }

    private static void AppendSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<ContextManifestItem> items,
        Dictionary<string, string> bodies)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.Append("## ").Append(title).Append('\n');
        foreach (ContextManifestItem item in items)
        {
            if (!bodies.TryGetValue(item.RelativePath, out string? body))
            {
                continue;
            }

            builder.Append("### ").Append(item.RelativePath).Append('\n').Append(body).Append('\n');
        }
    }
}
