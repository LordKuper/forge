using System.Security.Cryptography;
using System.Text;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Host;

/// <summary>See <see cref="IntakeExecutionOptions"/> — the identical override shape, for the
/// identical reason (a test-overridable poll interval).</summary>
public sealed record PlanningExecutionOptions(string ProjectRoot, TimeSpan? PollInterval = null)
{
    public TimeSpan Interval => PollInterval ?? TimeSpan.FromSeconds(15);
}

/// <summary>
/// Executes the one <see cref="NodeRole.Planning"/> node of every running sprint in this Host's
/// project. Stage 11's second node-executor slice and the first to invoke a real
/// <see cref="ILlmProvider"/>: <see cref="IntakeExecutionHostedService"/> (ADR 0028) deliberately
/// scoped itself to the one Work role that needs no provider, prompt, deadline, or worktree, and
/// named every model-bearing role as still unexecuted. This service closes that gap for
/// `planning` only — `implementation` and `review` still have no executor.
/// </summary>
/// <remarks>
/// Deliberately narrow about what "planning" produces: a single provider turn, run inside an
/// isolated, throwaway attempt worktree (ADR 0004) with file writes explicitly discouraged by the
/// prompt itself, whose only durable product is a <see cref="Handoff"/> summary for
/// `implementation` to read. Planning making and committing real file edits — which would need
/// <see cref="SprintGitIsolation.IntegrateAsync"/>/<see cref="SprintGitIsolation.RebaseAttemptAsync"/>
/// and a git-commit primitive this repository does not have yet — is out of this slice's scope;
/// see the PR/ADR for the full list of what is deliberately deferred.
///
/// Shares <see cref="IntakeExecutionHostedService"/>'s two durability properties for the same
/// reason: no per-sprint memory (every tick re-derives from durable state), and crash-resumability
/// through <see cref="SprintScheduler"/>'s own idempotency rather than a second mechanism of this
/// service's own. Resuming an interrupted planning attempt re-invokes the provider from scratch —
/// no partial provider transcript is preserved across a crash — which is honestly a wasted turn,
/// not a correctness gap: the attempt worktree is throwaway and the provider is instructed to make
/// no durable change of its own.
/// </remarks>
public sealed class PlanningExecutionHostedService(
    PlanningExecutionOptions options,
    ISprintStore store,
    SprintScheduler scheduler,
    SprintGitIsolation gitIsolation,
    ProviderCatalog providers,
    IConfigurationRegistry registry,
    IEnvironmentPaths environmentPaths,
    ForgeApplication application,
    ILogger<PlanningExecutionHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception> LogListFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2040, "PlanningExecutionListFailed"),
        "Executing planning nodes failed while listing this project's sprints; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogSprintFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2041, "PlanningExecutionSprintFailed"),
        "Executing the planning node failed for sprint {SprintId}; continuing with the rest.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogStartRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2042, "PlanningExecutionStartRejected"),
            "Starting the planning attempt for sprint {SprintId} was rejected ({DiagnosticCode}); retrying next tick.");

    private static readonly Action<ILogger, Guid, string, Exception?> LogCompleteRejected =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2043, "PlanningExecutionCompleteRejected"),
            "Completing the planning attempt for sprint {SprintId} was rejected ({DiagnosticCode}); retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception?> LogDefinitionUnusable = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2044, "PlanningExecutionDefinitionUnusable"),
        "Sprint {SprintId}'s frozen definition is missing the planning execution profile or a " +
            "candidate provider for it; its planning node cannot be executed.");

    private static readonly Action<ILogger, Guid, Exception?> LogWorktreeUnavailable =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2045, "PlanningExecutionWorktreeUnavailable"),
            "Preparing an isolated attempt worktree for sprint {SprintId}'s planning node failed; " +
                "the attempt is recorded as failed and will retry.");

    private static readonly Action<ILogger, Guid, Exception?> LogWorktreeDiscardFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2046, "PlanningExecutionWorktreeDiscardFailed"),
            "Discarding sprint {SprintId}'s planning attempt worktree did not fully succeed; a " +
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
                await ExecutePlanningAsync(sprintId, cancellationToken).ConfigureAwait(false);
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
                // Matches IntakeExecutionHostedService's own widened filter (ADR 0028, round 7
                // review) — the same durable-state-corruption exception shapes reachable through
                // SprintScheduler/FileSprintEventLog can surface through this service's identical
                // call chain (AdvanceGraphAsync, LoadDefinitionAsync, StartAttemptAsync,
                // CompleteAttemptAsync), and this service must not have to re-audit their internals
                // separately to know that.
                LogSprintFailed(logger, sprintId.Value, exception);
            }
        }
    }

    private async Task ExecutePlanningAsync(SprintId sprintId, CancellationToken cancellationToken)
    {
        SprintWorkflowState state = await scheduler
            .AdvanceGraphAsync(options.ProjectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state.Sprint.State != SprintState.Running)
        {
            return;
        }

        SprintDefinition? definition = await store
            .LoadDefinitionAsync(options.ProjectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }

        NodeDefinition? planning = definition.Graph.FirstOrDefault(
            node => node.Role == NodeRole.Planning && node.Kind == NodeKind.Work);
        if (planning is null || !state.Nodes.TryGetValue(planning.Id, out NodeSnapshot? node))
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

        // Checked before StartAttemptAsync, not after: a missing profile or unregistered provider
        // here means this sprint's planning node cannot run at all, and StartAttemptAsync itself
        // would silently skip routing rather than refuse (SprintScheduler only routes when a
        // profile IS found) — leaving a running, unrouted node behind with nothing to complete it.
        // Refusing here instead leaves the node untouched at `ready`, safe to retry once the
        // definition is fixed, exactly like the blank-identity guard above.
        if (!definition.ExecutionProfiles.TryGetValue(ExecutionPhase.Planning, out ExecutionProfile? profile) ||
            !providers.TryGet(new ProviderId(profile.Provider), out ILlmProvider? provider))
        {
            LogDefinitionUnusable(logger, sprintId.Value, null);
            return;
        }

        StartAttemptResult started = await scheduler
            .StartAttemptAsync(options.ProjectRoot, sprintId, planning.Id, node.Version, cancellationToken)
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

        (bool succeeded, List<string> outputs, List<NodeDiagnostic> diagnostics, string? summary) =
            await RunPlanningAttemptAsync(
                sprintId, attemptId, definition, profile, provider, documents, manifest, cancellationToken)
                .ConfigureAwait(false);
        if (!succeeded && diagnostics.Count == 0)
        {
            // AttemptSupervisionResult.Reason == Cancelled: the caller's own token fired (Host
            // shutting down), not a provider or infrastructure failure. RunPlanningAttemptAsync
            // returns this exact empty-diagnostics failure shape only for that case, deliberately
            // skipping both the worktree discard (the same token would make it throw) and
            // CompleteAttemptAsync (the node stays `running`, resumed on the next tick after
            // restart — the identical crash-resumability story IntakeExecutionHostedService already
            // relies on for its own interrupted attempts).
            return;
        }

        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            options.ProjectRoot,
            sprintId,
            planning.Id,
            attemptId,
            succeeded,
            manifest.ManifestDigest,
            outputs,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (!completed.Succeeded)
        {
            LogCompleteRejected(logger, sprintId.Value, completed.DiagnosticCode, null);
            return;
        }

        if (succeeded && summary is not null)
        {
            string? nextNodeId = definition.Graph
                .FirstOrDefault(candidate => candidate.Role == NodeRole.Implementation)?.Id;
            await scheduler.RecordHandoffAsync(
                options.ProjectRoot,
                sprintId,
                planning.Id,
                definition.BaseCommit,
                summary,
                decisions: [],
                openRisks: [],
                nextNodeIds: nextNodeId is null ? null : [nextNodeId],
                cancellationToken).ConfigureAwait(false);
            // A failed RecordHandoffAsync (WorkflowRecordInvalid) is not retried from here: the
            // node has already succeeded and durably recorded its NodeResult, and a crash or
            // conflict on this best-effort write is surfaced only through the missing Handoff
            // itself, matching how a leaked attempt worktree is left for a future reconciliation
            // pass rather than retried inline. Accepted debt, named rather than silently absorbed.
        }
    }

    private async Task<(bool Succeeded, List<string> Outputs, List<NodeDiagnostic> Diagnostics, string? Summary)>
        RunPlanningAttemptAsync(
            SprintId sprintId,
            AttemptId attemptId,
            SprintDefinition definition,
            ExecutionProfile profile,
            ILlmProvider provider,
            ForgeDocumentSet documents,
            ContextManifest manifest,
            CancellationToken cancellationToken)
    {
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(options.ProjectRoot, registry, cancellationToken).ConfigureAwait(false);

        GitOperationResult integration = await gitIsolation.EnsureIntegrationWorktreeAsync(
            options.ProjectRoot, projectId, sprintId, definition.BaseCommit, cancellationToken)
            .ConfigureAwait(false);
        if (!integration.Succeeded)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return (false, [], [WorktreeDiagnostic(integration)], null);
        }

        GitOperationResult attemptWorktree = await gitIsolation.CreateAttemptWorktreeAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken).ConfigureAwait(false);
        if (!attemptWorktree.Succeeded)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return (false, [], [WorktreeDiagnostic(attemptWorktree)], null);
        }

        string worktreePath = WorktreeLayout.AttemptPath(environmentPaths, projectId, sprintId, attemptId);

        string prompt = BuildPrompt(manifest, documents);
        AttemptSupervisionResult<ProviderRunResult> supervised;
        using (AttemptSupervisor supervisor = new(
            TimeSpan.FromSeconds(profile.SessionDeadlineSeconds),
            TimeSpan.FromSeconds(profile.IdleDeadlineSeconds),
            cancellationToken))
        {
            supervised = await supervisor.SuperviseAsync(async (token, onActivity) =>
            {
                try
                {
                    return await provider.RunAsync(prompt, worktreePath, token, onActivity)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // ADR 0016 documents RunAsync failing closed with a ProviderRunResult for every
                    // ordinary failure it anticipates; an unexpected exception (e.g. the process
                    // could not even be launched) is converted here rather than left to escape into
                    // this service's own per-sprint catch filter, which is tuned for durable-state
                    // corruption shapes, not process-launch failures.
                    return ProviderRunResult.Failed(ProviderFailureKind.Unknown, exception.Message);
                }
            }).ConfigureAwait(false);
        }

        if (supervised.Reason == AttemptTerminationReason.Cancelled)
        {
            // Deliberately skips the worktree discard below: the same cancellationToken that just
            // cancelled the provider run would also cancel a `git worktree remove` call made with
            // it. The worktree is left for a future reconciliation pass; see the class remarks.
            return (false, [], [], null);
        }

        bool discarded = await gitIsolation
            .DiscardAttemptAsync(options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        if (!discarded)
        {
            LogWorktreeDiscardFailed(logger, sprintId.Value, null);
        }

        if (supervised.Reason == AttemptTerminationReason.IdleTimeout)
        {
            return (false, [], [new(
                ProviderDiagnosticCodes.IdleTimeout, "provider", $"diagnostic.{ProviderDiagnosticCodes.IdleTimeout}",
                new Dictionary<string, string?>(StringComparer.Ordinal))], null);
        }

        if (supervised.Reason == AttemptTerminationReason.SessionTimeout)
        {
            return (false, [], [new(
                ProviderDiagnosticCodes.SessionTimeout, "provider",
                $"diagnostic.{ProviderDiagnosticCodes.SessionTimeout}",
                new Dictionary<string, string?>(StringComparer.Ordinal))], null);
        }

        ProviderRunResult? result = supervised.Value;
        if (result is null || !result.Succeeded)
        {
            string code = MapFailure(result?.Failure ?? ProviderFailureKind.Unknown);
            Dictionary<string, string?> arguments = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(result?.Detail))
            {
                arguments["detail"] = result.Detail;
            }

            return (false, [], [new(code, "provider", $"diagnostic.{code}", arguments)], null);
        }

        string? summary = result.TerminalResult?.Summary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return (false, [], [new(
                ProviderDiagnosticCodes.EmptyTerminalSummary, "provider",
                $"diagnostic.{ProviderDiagnosticCodes.EmptyTerminalSummary}",
                new Dictionary<string, string?>(StringComparer.Ordinal))], null);
        }

        return (true, [Digest(summary)], [], summary);
    }

    private static NodeDiagnostic WorktreeDiagnostic(GitOperationResult result)
    {
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            arguments["detail"] = result.Detail;
        }

        return new(result.DiagnosticCode, "git", $"diagnostic.{result.DiagnosticCode}", arguments);
    }

    private static string MapFailure(ProviderFailureKind failure) => failure switch
    {
        ProviderFailureKind.NotReady => ProviderDiagnosticCodes.RunNotReady,
        ProviderFailureKind.Authentication => ProviderDiagnosticCodes.AuthenticationRequired,
        ProviderFailureKind.QuotaExceeded => ProviderDiagnosticCodes.QuotaExceeded,
        ProviderFailureKind.RateLimited => ProviderDiagnosticCodes.RateLimited,
        ProviderFailureKind.Policy => ProviderDiagnosticCodes.RunPolicyViolation,
        ProviderFailureKind.Transient => ProviderDiagnosticCodes.RunTransientFailure,
        ProviderFailureKind.MalformedOutput => ProviderDiagnosticCodes.RunMalformedOutput,
        ProviderFailureKind.MissingTerminalResult => ProviderDiagnosticCodes.MissingTerminalResult,
        ProviderFailureKind.DuplicateTerminalResult => ProviderDiagnosticCodes.DuplicateTerminalResult,
        _ => ProviderDiagnosticCodes.RunUnknownFailure,
    };

    /// <summary>A deliberately plain instruction prompt, not a templating system: concatenates
    /// every admitted rule/knowledge document's already-parsed <see cref="ForgeDocument.Body"/> (no
    /// second disk read — <paramref name="documents"/> is the exact parse pass
    /// <paramref name="manifest"/> was compiled from) in the manifest's own admitted order, behind a
    /// fixed header telling the model this is the planning phase and it must not edit, create, or
    /// delete any file. Structured decision/risk extraction from the model's own free-text response
    /// is explicitly out of this slice's scope — see the PR/ADR.</summary>
    private static string BuildPrompt(ContextManifest manifest, ForgeDocumentSet documents)
    {
        Dictionary<string, string> bodies = documents.Documents
            .GroupBy(document => document.RelativePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Body, StringComparer.Ordinal);
        StringBuilder builder = new();
        builder.Append(
            "You are the planning phase of an automated software development sprint. Read the " +
            "project context below and respond with a concise, natural-language plan: the approach " +
            "you recommend and why. Do not edit, create, or delete any file in this working " +
            "directory -- this is a read-only research and reasoning turn; a later phase implements " +
            "the change.\n\n");
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

    private static string Digest(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";
}
