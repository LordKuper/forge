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
    ActiveOperationRegistry activeOperations,
    StopOperationCoordinator stopCoordinator,
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
                or KeyNotFoundException or ArgumentOutOfRangeException or YamlException
                or JsonException or ConfigurationScopeException or Win32Exception)
            {
                // The first four lines match IntakeExecutionHostedService's own widened filter
                // (ADR 0028, round 7 review) — the same durable-state-corruption exception shapes
                // reachable through SprintScheduler/FileSprintEventLog can surface through this
                // service's identical call chain (AdvanceGraphAsync, LoadDefinitionAsync,
                // StartAttemptAsync, CompleteAttemptAsync), and this service must not have to
                // re-audit their internals separately to know that. Widened further for four
                // failure surfaces intake never had: ArgumentOutOfRangeException from a corrupted
                // (non-positive or absurdly large) ExecutionProfile deadline reaching
                // AttemptSupervisor's own constructor guard; YamlException/JsonException/
                // ConfigurationScopeException from ProjectIdentity.ReadProjectIdAsync reading a
                // manifest.yaml damaged badly enough that YamlConfigurationStore.ReadAsync's own
                // `.previous`-backup recovery cannot apply (matching the exact exception set
                // YamlConfigurationStore.IsRecoverable already names); and Win32Exception from
                // SprintGitIsolation's underlying `git.exe` process failing to start at all (a
                // failure surface intake — which never touches git — never had). Every one of these
                // leaves the node `running`, resumable by the same idempotent restart
                // StartAttemptAsync already gives `intake`; a permanently corrupted profile or
                // manifest logs a warning every tick rather than being rate-limited, matching this
                // service's own "logged, not rate-limited" precedent for a permanently-rejected
                // completion.
                LogSprintFailed(logger, sprintId.Value, exception);
            }
        }
    }

    private async Task ExecutePlanningAsync(SprintId sprintId, CancellationToken cancellationToken)
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

        NodeDefinition? planning = definition.Graph.FirstOrDefault(
            node => node.Role == NodeRole.Planning && node.Kind == NodeKind.Work);
        if (planning is null || !state.Nodes.TryGetValue(planning.Id, out NodeSnapshot? node))
        {
            return;
        }

        // Plan section 7.3 / ADR 0047 addendum: check the durable stop intent from the node's own
        // CurrentAttemptId and the attempt's durable state, never from node.State == Running (see
        // ImplementationExecutionHostedService's own identical check for the full reasoning).
        if (node.CurrentAttemptId is { } stoppingAttemptIdText &&
            state.Attempts.TryGetValue(stoppingAttemptIdText, out AttemptSnapshot? stoppingAttempt) &&
            stoppingAttempt.StopRequestedAt is not null && stoppingAttempt.StopConvergedAt is null)
        {
            Guid stoppingProjectId = await ProjectIdentity
                .ReadProjectIdAsync(options.ProjectRoot, registry, cancellationToken).ConfigureAwait(false);
            await stopCoordinator.FinishStopAsync(
                options.ProjectRoot, sprintId, stoppingProjectId, planning.Id,
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

        PlanningAttemptOutcome outcome = await RunPlanningAttemptAsync(
                sprintId, attemptId, definition, profile, provider, documents, manifest, cancellationToken)
                .ConfigureAwait(false);
        if (outcome.Disposition == PlanningAttemptDisposition.HostShuttingDown)
        {
            // RunPlanningAttemptAsync deliberately skipped both the worktree discard (the same
            // cancelled token would make it throw) and any scheduler completion call — the node
            // stays `running`, resumed on the next tick after restart, the identical
            // crash-resumability story IntakeExecutionHostedService already relies on for its own
            // interrupted attempts.
            return;
        }

        // ADR 0006's durable rate-limit wait (ADR 0018): a RateLimited failure is a retryable
        // condition the shared provider/model/surface routing key should back off from for
        // DefaultRateLimitBackoff, not an ordinary failed attempt — DeferAttemptAsync abandons the
        // attempt and records that routing block itself; it must not be raced by this service's own
        // CompleteAttemptAsync call for the identical attempt.
        CompleteAttemptResult completed = outcome.Disposition == PlanningAttemptDisposition.RateLimited
            ? await scheduler.DeferAttemptAsync(
                options.ProjectRoot, sprintId, planning.Id, attemptId, manifest.ManifestDigest, cancellationToken)
                .ConfigureAwait(false)
            : await scheduler.CompleteAttemptAsync(
                options.ProjectRoot,
                sprintId,
                planning.Id,
                attemptId,
                outcome.Disposition == PlanningAttemptDisposition.Succeeded,
                manifest.ManifestDigest,
                outcome.Outputs,
                outcome.Diagnostics,
                cancellationToken).ConfigureAwait(false);
        if (!completed.Succeeded)
        {
            LogCompleteRejected(logger, sprintId.Value, completed.DiagnosticCode, null);
            return;
        }

        if (outcome.Disposition == PlanningAttemptDisposition.Succeeded && outcome.Summary is not null)
        {
            string? nextNodeId = definition.Graph
                .FirstOrDefault(candidate => candidate.Role == NodeRole.Implementation)?.Id;
            await scheduler.RecordHandoffAsync(
                options.ProjectRoot,
                sprintId,
                planning.Id,
                definition.BaseCommit,
                outcome.Summary,
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

    /// <summary>What the caller must do with the started attempt — an explicit discriminator rather
    /// than inferring "the Host is shutting down" from an otherwise-ambiguous combination of other
    /// fields, which would be fragile to extend the next time a node executor (implementation,
    /// review) copies this shape.</summary>
    private enum PlanningAttemptDisposition
    {
        Succeeded,
        Failed,

        /// <summary>ADR 0006/0018's durable rate-limit wait: the caller must complete the attempt
        /// through <see cref="SprintScheduler.DeferAttemptAsync"/>, not the ordinary
        /// <see cref="SprintScheduler.CompleteAttemptAsync"/> path.</summary>
        RateLimited,

        /// <summary><see cref="AttemptTerminationReason.Cancelled"/>: the Host is shutting down, not
        /// a provider or infrastructure failure. The caller must complete the attempt neither way —
        /// see <see cref="RunPlanningAttemptAsync"/>'s own remarks.</summary>
        HostShuttingDown,
    }

    private readonly record struct PlanningAttemptOutcome(
        PlanningAttemptDisposition Disposition,
        List<string> Outputs,
        List<NodeDiagnostic> Diagnostics,
        string? Summary)
    {
        public static readonly PlanningAttemptOutcome Cancelled =
            new(PlanningAttemptDisposition.HostShuttingDown, [], [], null);

        public static PlanningAttemptOutcome Failed(NodeDiagnostic diagnostic) =>
            new(PlanningAttemptDisposition.Failed, [], [diagnostic], null);

        public static PlanningAttemptOutcome RateLimitedFailure(NodeDiagnostic diagnostic) =>
            new(PlanningAttemptDisposition.RateLimited, [], [diagnostic], null);

        public static PlanningAttemptOutcome Success(string digest, string summary) =>
            new(PlanningAttemptDisposition.Succeeded, [digest], [], summary);
    }

    private async Task<PlanningAttemptOutcome> RunPlanningAttemptAsync(
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
            return PlanningAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", integration.DiagnosticCode, integration.Detail));
        }

        GitOperationResult attemptWorktree = await gitIsolation.CreateAttemptWorktreeAsync(
            options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken).ConfigureAwait(false);
        if (!attemptWorktree.Succeeded)
        {
            LogWorktreeUnavailable(logger, sprintId.Value, null);
            return PlanningAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("git", attemptWorktree.DiagnosticCode, attemptWorktree.Detail));
        }

        string worktreePath = WorktreeLayout.AttemptPath(environmentPaths, projectId, sprintId, attemptId);

        string prompt = BuildPrompt(manifest, documents);
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
                    // ADR 0016 documents RunAsync failing closed with a ProviderRunResult for every
                    // ordinary failure it anticipates; an unexpected exception (e.g. the process
                    // could not even be launched) is converted here rather than left to escape into
                    // this service's own per-sprint catch filter, which is tuned for durable-state
                    // corruption shapes, not process-launch failures.
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
            // Deliberately skips the worktree discard below: the same cancellationToken that just
            // cancelled the provider run would also cancel a `git worktree remove` call made with
            // it. The worktree is left for a future reconciliation pass; see the class remarks.
            return PlanningAttemptOutcome.Cancelled;
        }

        bool discarded = await gitIsolation
            .DiscardAttemptAsync(options.ProjectRoot, projectId, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        if (!discarded)
        {
            LogWorktreeDiscardFailed(logger, sprintId.Value, null);
        }

        string? terminationCode = supervised.Reason switch
        {
            AttemptTerminationReason.IdleTimeout => ProviderDiagnosticCodes.IdleTimeout,
            AttemptTerminationReason.SessionTimeout => ProviderDiagnosticCodes.SessionTimeout,
            _ => null,
        };
        if (terminationCode is not null)
        {
            return PlanningAttemptOutcome.Failed(NodeExecutionDiagnostics.Diagnostic("provider", terminationCode));
        }

        ProviderRunResult? result = supervised.Value;
        if (result is null || !result.Succeeded)
        {
            ProviderFailureKind failure = result?.Failure ?? ProviderFailureKind.Unknown;
            NodeDiagnostic diagnostic = NodeExecutionDiagnostics.Diagnostic(
                "provider", NodeExecutionDiagnostics.MapProviderFailure(failure), result?.Detail);
            return failure == ProviderFailureKind.RateLimited
                ? PlanningAttemptOutcome.RateLimitedFailure(diagnostic)
                : PlanningAttemptOutcome.Failed(diagnostic);
        }

        string? summary = result.TerminalResult?.Summary;
        return string.IsNullOrWhiteSpace(summary)
            ? PlanningAttemptOutcome.Failed(
                NodeExecutionDiagnostics.Diagnostic("provider", ProviderDiagnosticCodes.EmptyTerminalSummary))
            : PlanningAttemptOutcome.Success(NodeExecutionDiagnostics.Digest(summary), summary);
    }

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
}
