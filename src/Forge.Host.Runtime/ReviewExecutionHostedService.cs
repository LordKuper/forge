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
public sealed record ReviewExecutionOptions(string ProjectRoot, TimeSpan? PollInterval = null)
{
    public TimeSpan Interval => PollInterval ?? TimeSpan.FromSeconds(15);
}

/// <summary>
/// Executes the one <see cref="NodeRole.Review"/> node of every running sprint in this Host's
/// project. Stage 11's fourth node-executor slice, and the first to invoke
/// <see cref="SprintScheduler.RecordReviewIterationAsync"/> — ADR 0006/0015's whole
/// severity-floor/convergence engine, built in an earlier stage with zero production callers,
/// matching this stage's own established "primitive ahead of its executor" rhythm.
/// </summary>
/// <remarks>
/// Deliberately narrow, matching every prior slice's own scoping discipline: only
/// <see cref="ReviewDimension.Implementation"/> (the dimension the convergence gate's repeated-
/// finding-set rule is built around) and only <see cref="ReviewerKind.External"/> (a single
/// provider's own verdict; <see cref="ReviewerKind.Internal"/> needs a rubric/coverage-scoping
/// mechanism this slice does not build). The provider's response is parsed for a verdict only — a
/// fixed `APPROVED`/`CHANGES_REQUESTED` marker as the last non-blank line — never individual,
/// located findings: <see cref="ReviewFindingDraft"/> requires evidence and a schema-shaped message
/// key per finding, which reliable structured extraction from free-text provider output is real,
/// separate design work this slice does not attempt. `findings` is always empty; convergence still
/// works correctly on an empty set (ADR 0006's repeated-finding-set rule applies to the empty set
/// exactly like any other), so this is an honest degradation, not a broken feature.
///
/// A review "attempt" spans however many <see cref="SprintScheduler.RecordReviewIterationAsync"/>
/// calls it takes to reach a stopping point, not one call per iteration: an <see cref="ReviewOutcome
/// .Approved"/> verdict or a convergence-gate trip (<see cref="DiagnosticCodes.ReviewIterationLimit"/>/
/// <see cref="DiagnosticCodes.ReviewRepeatedFindings"/>) completes the attempt
/// (<see cref="SprintScheduler.CompleteAttemptAsync"/>, unblocking <c>human_approval</c>); an
/// ordinary unresolved <see cref="ReviewOutcome.ChangesRequested"/> leaves the attempt `running` —
/// resumed with the same attempt id on the next tick, producing the next iteration — rather than
/// counting against <see cref="SprintScheduler.MaxAutomaticRetries"/>, whose fixed budget of two
/// would otherwise permanently fail the node long before ADR 0006's own fourteen-iteration
/// convergence budget ever applies. A genuine technical failure (provider error, timeout, an
/// unparseable verdict) still completes the attempt as failed and is still bounded by that generic
/// retry budget — only "the reviewer asked for changes" is exempt from it.
/// </remarks>
public sealed class ReviewExecutionHostedService(
    ReviewExecutionOptions options,
    ISprintStore store,
    SprintScheduler scheduler,
    SprintGitIsolation gitIsolation,
    ProviderCatalog providers,
    IConfigurationRegistry registry,
    IEnvironmentPaths environmentPaths,
    ForgeApplication application,
    ActiveOperationRegistry activeOperations,
    StopOperationCoordinator stopCoordinator,
    ILogger<ReviewExecutionHostedService> logger) : BackgroundService
{
    // This service owns EventIds 2080-2089; it vacated 2060-2069 so the implementation executor's
    // block could be widened to hold its per-attempt payload events (see that service's own note).
    private static readonly Action<ILogger, Exception> LogListFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2080, "ReviewExecutionListFailed"),
        "Executing review nodes failed while listing this project's sprints; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogSprintFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2081, "ReviewExecutionSprintFailed"),
        "Executing the review node failed for sprint {SprintId}; continuing with the rest.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogStartRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2082, "ReviewExecutionStartRejected"),
            "Starting the review attempt for sprint {SprintId} was rejected ({DiagnosticCode}); " +
                "retrying next tick.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogCompleteRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2083, "ReviewExecutionCompleteRejected"),
            "Completing the review attempt for sprint {SprintId} was rejected ({DiagnosticCode}); " +
                "retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception?> LogDefinitionUnusable = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2084, "ReviewExecutionDefinitionUnusable"),
        "Sprint {SprintId}'s frozen definition is missing the review execution profile, a " +
            "candidate provider for it, or implementation's own handoff; its review node cannot " +
            "be executed.");

    private static readonly Action<ILogger, Guid, Exception?> LogWorktreeUnavailable =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2085, "ReviewExecutionWorktreeUnavailable"),
            "Preparing an isolated attempt worktree for sprint {SprintId}'s review node failed; " +
                "the attempt is recorded as failed and will retry.");

    private static readonly Action<ILogger, Guid, Exception?> LogWorktreeDiscardFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2086, "ReviewExecutionWorktreeDiscardFailed"),
            "Discarding sprint {SprintId}'s review attempt worktree did not fully succeed; a " +
                "future reconciliation pass must clean it up.");

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
                await ExecuteReviewAsync(sprintId, cancellationToken).ConfigureAwait(false);
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
                // Matches every prior node executor's own widened filter (ADR 0028/0030/0032) — the
                // identical durable-state-corruption and infrastructure-failure exception shapes
                // reach this service through the same call chain.
                LogSprintFailed(logger, sprintId.Value, exception);
            }
        }
    }

    private async Task ExecuteReviewAsync(SprintId sprintId, CancellationToken cancellationToken)
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

        NodeDefinition? review = definition.Graph.FirstOrDefault(
            candidate => candidate.Role == NodeRole.Review && candidate.Kind == NodeKind.Work);
        if (review is null || !state.Nodes.TryGetValue(review.Id, out NodeSnapshot? node))
        {
            return;
        }

        // Plan section 7.3 / ADR 0047 addendum: check the durable stop intent from the node's own
        // CurrentAttemptId and the attempt's durable state, never from node.State == Running (see
        // ImplementationExecutionHostedService's own identical check for the full reasoning).
        // Deliberately still narrower than "any node with a CurrentAttemptId" -- an ordinary
        // unresolved ChangesRequested verdict also leaves the node Running for the next review
        // iteration, and must keep doing so; only an attempt that actually carries a not-yet-converged
        // stop intent short-circuits here instead of starting another iteration.
        if (node.CurrentAttemptId is { } stoppingAttemptIdText &&
            state.Attempts.TryGetValue(stoppingAttemptIdText, out AttemptSnapshot? stoppingAttempt) &&
            stoppingAttempt.StopRequestedAt is not null && stoppingAttempt.StopConvergedAt is null)
        {
            Guid stoppingProjectId = await ProjectIdentity
                .ReadProjectIdAsync(options.ProjectRoot, registry, cancellationToken).ConfigureAwait(false);
            await stopCoordinator.FinishStopAsync(
                options.ProjectRoot, sprintId, stoppingProjectId, review.Id,
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

        if (!definition.ExecutionProfiles.TryGetValue(ExecutionPhase.Review, out ExecutionProfile? profile) ||
            !providers.TryGet(new ProviderId(profile.Provider), out ILlmProvider? provider))
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        // Reviews implementation's own work directly -- the review node's graph dependency
        // (test_work) has no executor yet and never records a handoff, the same "bypass the
        // not-yet-built intermediary, read the real content producer" choice implementation itself
        // already made for planning.
        string? implementationNodeId = definition.Graph
            .FirstOrDefault(candidate => candidate.Role == NodeRole.Implementation)?.Id;
        Handoff? handoff = implementationNodeId is null
            ? null
            : (await scheduler.GetHandoffsAsync(options.ProjectRoot, sprintId, cancellationToken)
                .ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.NodeId.Value == implementationNodeId);
        if (handoff is null)
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        StartAttemptResult started = await scheduler
            .StartAttemptAsync(options.ProjectRoot, sprintId, review.Id, node.Version, cancellationToken)
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

        ReviewAttemptOutcome outcome = await RunReviewAttemptAsync(
                sprintId, review.Id, attemptId, definition, profile, provider, documents, manifest, handoff,
                cancellationToken)
                .ConfigureAwait(false);
        switch (outcome.Disposition)
        {
            case ReviewAttemptDisposition.HostShuttingDown:
                // The node stays `running`, resumed on the next tick after restart -- the identical
                // crash-resumability story every prior node executor already relies on.
                return;
            case ReviewAttemptDisposition.NotConverged:
                // Deliberately no scheduler call at all: the attempt stays `running`, resumed with
                // the same attempt id on the next tick to produce the review's own next iteration.
                // See the class remarks for why this must not count against MaxAutomaticRetries.
                return;
        }

        // Post-release audit (PR #101): a stop can be durably requested for this attempt after the
        // provider already returned (any converging disposition), before this method's own
        // CompleteAttemptAsync call below — review's structural analog of
        // ImplementationExecutionHostedService's commit/integrate race, since CompleteAttemptAsync is
        // the point of no return here (a Succeeded completion unblocks human_approval/
        // AdvanceGraphAsync; FinishStopAsync never re-arms an already-Succeeded node). A fresh read
        // here (never a value captured before the provider ran) catches it; the node stays `running`,
        // converged by FinishStopAsync's own top-of-tick check on this or the next tick instead of
        // racing ahead through a normal completion.
        if (await StopHasBeenRequestedAsync(sprintId, attemptId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CompleteAttemptResult completed = outcome.Disposition == ReviewAttemptDisposition.RateLimited
            ? await scheduler.DeferAttemptAsync(
                options.ProjectRoot, sprintId, review.Id, attemptId, manifest.ManifestDigest, cancellationToken)
                .ConfigureAwait(false)
            : await scheduler.CompleteAttemptAsync(
                options.ProjectRoot,
                sprintId,
                review.Id,
                attemptId,
                outcome.Disposition == ReviewAttemptDisposition.Succeeded,
                manifest.ManifestDigest,
                outcome.Outputs,
                outcome.Diagnostics,
                cancellationToken).ConfigureAwait(false);
        if (!completed.Succeeded)
        {
            LogCompleteRejected(logger, sprintId.Value, completed.DiagnosticCode, null);
        }
    }

    private enum ReviewAttemptDisposition
    {
        Succeeded,
        NotConverged,
        Failed,
        RateLimited,
        HostShuttingDown,
    }

    private readonly record struct ReviewAttemptOutcome(
        ReviewAttemptDisposition Disposition, List<string> Outputs, List<NodeDiagnostic> Diagnostics)
    {
        public static readonly ReviewAttemptOutcome Cancelled =
            new(ReviewAttemptDisposition.HostShuttingDown, [], []);

        public static readonly ReviewAttemptOutcome NotConverged =
            new(ReviewAttemptDisposition.NotConverged, [], []);

        public static ReviewAttemptOutcome Failed(NodeDiagnostic diagnostic) =>
            new(ReviewAttemptDisposition.Failed, [], [diagnostic]);

        public static ReviewAttemptOutcome RateLimitedFailure(NodeDiagnostic diagnostic) =>
            new(ReviewAttemptDisposition.RateLimited, [], [diagnostic]);

        public static ReviewAttemptOutcome Success(string digest) =>
            new(ReviewAttemptDisposition.Succeeded, [digest], []);
    }

    private async Task<ReviewAttemptOutcome> RunReviewAttemptAsync(
        SprintId sprintId,
        string nodeId,
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
        if (!integration.Succeeded || integration.Commit is not { } integrationTip)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return ReviewAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", integration.DiagnosticCode, integration.Detail));
        }

        GitOperationResult attemptWorktree = await gitIsolation.CreateAttemptWorktreeAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken).ConfigureAwait(false);
        if (!attemptWorktree.Succeeded)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return ReviewAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", attemptWorktree.DiagnosticCode, attemptWorktree.Detail));
        }

        GitDiffResult diffResult = await gitIsolation.ReadDiffAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, definition.BaseCommit, integrationTip,
            cancellationToken).ConfigureAwait(false);
        if (!diffResult.Succeeded || diffResult.Diff is not { } diff)
        {
            await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);
            return ReviewAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", diffResult.DiagnosticCode, diffResult.Detail));
        }

        string worktreePath = WorktreeLayout.AttemptPath(environmentPaths, projectId, sprintId, attemptId);
        string prompt = BuildPrompt(manifest, documents, handoff, diff, diffResult.Truncated, profile.Lineage);
        AttemptSupervisionResult<ProviderRunResult> supervised;
        // Plan section 7.3: registered before provider/process execution, unregistered in `finally`.
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
            return ReviewAttemptOutcome.Cancelled;
        }

        await DiscardAsync(sprintId, projectId, attemptId, cancellationToken).ConfigureAwait(false);

        string? terminationCode = supervised.Reason switch
        {
            AttemptTerminationReason.IdleTimeout => ProviderDiagnosticCodes.IdleTimeout,
            AttemptTerminationReason.SessionTimeout => ProviderDiagnosticCodes.SessionTimeout,
            _ => null,
        };
        if (terminationCode is not null)
        {
            return ReviewAttemptOutcome.Failed(NodeExecutionDiagnostics.Diagnostic("provider", terminationCode));
        }

        ProviderRunResult? result = supervised.Value;
        if (result is null || !result.Succeeded)
        {
            ProviderFailureKind failure = result?.Failure ?? ProviderFailureKind.Unknown;
            NodeDiagnostic diagnostic = NodeExecutionDiagnostics.Diagnostic(
                "provider", NodeExecutionDiagnostics.MapProviderFailure(failure), result?.Detail);
            return failure == ProviderFailureKind.RateLimited
                ? ReviewAttemptOutcome.RateLimitedFailure(diagnostic)
                : ReviewAttemptOutcome.Failed(diagnostic);
        }

        ReviewOutcome? verdict = ParseVerdict(result.TerminalResult?.Summary);
        if (verdict is not { } outcome)
        {
            return ReviewAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("provider", ProviderDiagnosticCodes.ReviewVerdictUnparseable));
        }

        RecordReviewIterationResult recorded = await scheduler.RecordReviewIterationAsync(
            options.ProjectRoot, sprintId, nodeId, ReviewDimension.Implementation, ReviewerKind.External, outcome,
            findings: [], coverage: null, cancellationToken).ConfigureAwait(false);
        // `RecordReviewIterationAsync` never pairs `Succeeded: false` with `ReviewIterationLimit`/
        // `ReviewRepeatedFindings` -- both diagnostic codes are only ever returned alongside
        // `Succeeded: true` (a convergence-gate trip is a designed stopping point, not a failure) --
        // so this is a plain success check, not a narrower one.
        if (!recorded.Succeeded)
        {
            return ReviewAttemptOutcome.Failed(NodeExecutionDiagnostics.Diagnostic("review", recorded.DiagnosticCode));
        }

        bool converged = outcome == ReviewOutcome.Approved ||
            recorded.DiagnosticCode is DiagnosticCodes.ReviewIterationLimit or DiagnosticCodes.ReviewRepeatedFindings;
        if (!converged)
        {
            return ReviewAttemptOutcome.NotConverged;
        }

        return ReviewAttemptOutcome.Success(
            NodeExecutionDiagnostics.Digest(result.TerminalResult?.Summary ?? outcome.ToString()));
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
    /// <c>StopConvergedAt</c> for this exact attempt while this method is between the provider
    /// returning and <see cref="SprintScheduler.CompleteAttemptAsync"/>. Gating on
    /// <c>StopConvergedAt is null</c> here would then see the stop as "already handled" and let the
    /// attempt complete anyway (PR #101 review finding 1). A stop request in flight at all --
    /// convergence status irrelevant -- means don't complete.</summary>
    private async Task<bool> StopHasBeenRequestedAsync(
        SprintId sprintId, AttemptId attemptId, CancellationToken cancellationToken)
    {
        SprintWorkflowState? state = await store
            .LoadAsync(options.ProjectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        return state is not null &&
            state.Attempts.TryGetValue(attemptId.Value.ToString("D"), out AttemptSnapshot? attempt) &&
            attempt.StopRequestedAt is not null;
    }

    /// <summary>The review prompt's own required output contract: the terminal summary's last
    /// genuinely non-blank line must be exactly one of the two verdict markers (case-insensitive,
    /// surrounding whitespace ignored) — no other structured-output parsing is attempted in this
    /// slice.</summary>
    private static ReviewOutcome? ParseVerdict(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        string? lastLine = summary
            .Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0);
        return lastLine?.ToUpperInvariant() switch
        {
            "APPROVED" => ReviewOutcome.Approved,
            "CHANGES_REQUESTED" => ReviewOutcome.ChangesRequested,
            _ => null,
        };
    }

    /// <summary>Forbids edits, like planning's own prompt — review is read-only. Carries
    /// implementation's real handoff, the bounded diff between the sprint's base and its current
    /// integration tip, and the frozen lineage-independence fact as informational context only
    /// (never enforced by this slice — a same-provider review still runs and still records a real
    /// verdict, exactly as ADR 0006 itself treats reduced lineage separation: recorded, never a
    /// gate).</summary>
    private static string BuildPrompt(
        ContextManifest manifest,
        ForgeDocumentSet documents,
        Handoff handoff,
        string diff,
        bool diffTruncated,
        ExecutionLineage? lineage)
    {
        Dictionary<string, string> bodies = documents.Documents
            .GroupBy(document => document.RelativePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Body, StringComparer.Ordinal);
        StringBuilder builder = new();
        builder.Append(
            "You are the review phase of an automated software development sprint. Read the " +
            "implementation summary and the diff below and decide whether the change is acceptable. " +
            "Do not edit, create, or delete any file in this working directory -- this is a " +
            "read-only review turn.\n\n" +
            "Respond with your reasoning, then end your response with exactly one line containing " +
            "only APPROVED or CHANGES_REQUESTED (no other text on that line).\n\n");
        if (lineage is not null)
        {
            builder.Append("## Reviewer independence\n")
                .Append(
                    lineage.AchievedIndependence
                        ? "You are a different provider/model than the one that implemented this change.\n"
                        : "The same provider/model that implemented this change is reviewing it; " +
                            "reduced independence was recorded for this verdict.\n")
                .Append('\n');
        }

        builder.Append("## Implementation summary\n").Append(handoff.Summary).Append('\n');
        builder.Append("## Diff\n```diff\n").Append(diff);
        if (diffTruncated)
        {
            builder.Append("\n... (truncated)");
        }

        builder.Append("\n```\n");
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
