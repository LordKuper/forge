using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host.Client;
using Forge.Providers;
using YamlDotNet.Core;

namespace Forge.Application;

public sealed record InitializeProjectCommand(
    string? Root,
    bool Confirmed,
    long ExpectedStateVersion,
    Guid IdempotencyKey,
    string UserFacingLanguage = "en",
    string AgentFacingLanguage = "en");

public sealed record ConfigurationWriteResult(bool Succeeded, string DiagnosticCode)
{
    public static ConfigurationWriteResult Success { get; } = new(true, DiagnosticCodes.None);
}

/// <summary><see cref="Sprint"/> is populated only once <see cref="Succeeded"/> reflects the
/// sprint actually reaching `completed` — a failed merge or an already-in-flight attempt reports
/// <see cref="Node"/> alone (mirroring every other node-settling mutation's own result shape), since
/// the sprint itself has not moved in either of those cases.</summary>
public sealed record FinalizeSprintResult(
    bool Succeeded, NodeSnapshot? Node, SprintSnapshot? Sprint, string DiagnosticCode);

public sealed record ConfigurationView(
    IReadOnlyList<EffectiveConfigurationValue> Values,
    string DiagnosticCode)
{
    public static ConfigurationView Empty { get; } = new([], DiagnosticCodes.None);
}

public sealed record ProjectOverview(StartupStatus Startup, ProjectSnapshot Snapshot);

/// <summary>
/// ADR 0005: every `.forge/` mutation this interface names must be routed through the project's
/// Host, never executed against a local <see cref="ForgeApplication"/> once one is reachable — see
/// <c>RemoteForgeMutations</c> (`Forge.Host.Client`-backed) versus <see cref="ForgeApplication"/>
/// itself (the Host's own in-process implementation; also the client-side fallback while no
/// project is initialized yet, since a Host cannot exist before <c>InitializeProjectAsync</c> gives
/// it a project id to key its lease/pipe on — ADR 0005's "minimal bootstrap... path needed before a
/// host is available"). `RefreshProviderHealthAsync` and `InitializeProjectAsync` are deliberately
/// excluded: the former is already serialized by its own per-user <c>IProviderInstallLock</c>
/// regardless of which process calls it, and the latter is the bootstrap step itself.
/// </summary>
public interface IForgeMutations
{
    /// <summary>Quarantines unreadable configuration so a failed startup can reach a usable state.</summary>
    Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>Converts the raw surface input using the declared type of the key.</summary>
    Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken);

    /// <summary>Writes `CLAUDE.md`/`AGENTS.md` for every enabled provider (ADR 0011). Idempotent;
    /// never overwrites a target file that is not Forge-owned.</summary>
    Task<IntegrationWriteResult> InstallIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>Deletes a Forge-owned `CLAUDE.md`/`AGENTS.md` for every enabled provider (ADR
    /// 0011). Idempotent; never deletes a target file that is not Forge-owned.</summary>
    Task<IntegrationWriteResult> RemoveIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>ADR 0005's human-only `workflow.review` capability: approves or rejects a
    /// `awaiting_human` gate node. <paramref name="confirmed"/> must be <see langword="true"/> —
    /// unlike <see cref="InstallIntegrationAsync"/>/<see cref="RemoveIntegrationAsync"/>, this never
    /// falls back to a config-driven bypass, since <c>interaction.confirm_destructive</c> is a value
    /// an agent could itself set through <c>forge config</c>.</summary>
    Task<NodeActionResult> ResolveGateAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool approved,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>The human-only `workflow.confirm` capability: records whether a sprint's
    /// `confirmation` node's implementation meets its definition of done, driving that Work node's
    /// own attempt to a terminal state in the same call — no executor exists for this role (see
    /// <see cref="ExecutionProfilePolicy.PhaseFor"/>), so nothing else ever settles it.
    /// <paramref name="confirmed"/> must be <see langword="true"/> — the same no-config-bypass rule
    /// as <see cref="ResolveGateAsync"/>.</summary>
    Task<RecordConfirmationResult> ConfirmNodeAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        ConfirmationOutcome outcome,
        string definitionOfDone,
        IReadOnlyList<ConfirmationEvidence> evidence,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>The human-only `workflow.test_work` capability: records whether new tests were
    /// added to protect the scope, or a justified decision was made that none were needed, driving
    /// that Work node's own attempt to a terminal state in the same call — same reasoning as
    /// <see cref="ConfirmNodeAsync"/> (no executor exists for this role either).
    /// <paramref name="confirmed"/> must be <see langword="true"/> — the same no-config-bypass rule
    /// as <see cref="ResolveGateAsync"/>.</summary>
    Task<RecordTestWorkResult> RecordTestWorkAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        TestWorkOutcome outcome,
        string justification,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>The human-only `workflow.finalize` capability (ADR 0036): merges a sprint's
    /// isolated integration branch into the project's own default branch (the branch checked out
    /// when the sprint was created, frozen as <see cref="SprintDefinition.DefaultBranch"/>) and, on
    /// success, completes the sprint. Fast-forward-only and fails closed on any divergence, a dirty
    /// working directory, or the wrong branch checked out — this never runs `git checkout` itself,
    /// so the project's own working directory never changes which branch it is on because Forge
    /// ran. <paramref name="confirmed"/> must be <see langword="true"/> — the same no-config-bypass
    /// rule as <see cref="ResolveGateAsync"/>.</summary>
    Task<FinalizeSprintResult> FinalizeSprintAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>ADR 0005/0018's human-only `attempt.supersede` capability: cancels a non-terminal
    /// attempt and creates a linked replacement. <paramref name="confirmed"/> must be
    /// <see langword="true"/> — the same no-config-bypass rule as <see cref="ResolveGateAsync"/>.</summary>
    Task<CompleteAttemptResult> SupersedeAttemptAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        string instruction,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>ADR 0044/plan section 7's human-only `workflow.stop_operation` capability: durably
    /// records a stop intent for the sprint's exact active attempt and cancels it without settling
    /// the sprint as failed or consuming automatic retry budget. <paramref name="confirmed"/> must
    /// be <see langword="true"/> -- the same no-config-bypass rule as
    /// <see cref="SupersedeAttemptAsync"/>. Convergence (process-tree termination, worktree discard,
    /// node re-arm, sprint pause) completes asynchronously as the owning executor observes the
    /// cancellation; a successful result here means the intent is durable and cancellation was
    /// requested, not that convergence has already finished.</summary>
    Task<StopOperationResult> StopCurrentOperationAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>Creates a sprint from the project's canonical `implementation-critical` graph
    /// (ADR 0001). Not confirmable/destructive — each call is a stateless attempt to create one new
    /// sprint, so a caller that wants a crash-safe retry to resume rather than mint a second sprint
    /// must not simply re-invoke this method (see the ADR for this slice).
    /// <paramref name="title"/> is the operator's optional short label for the new sprint (ADR
    /// 0057), frozen once into <see cref="Forge.Domain.SprintDefinition.Title"/> and never renamed
    /// afterward. <see langword="null"/> or blank means no title; anything longer than
    /// <see cref="SprintOrchestrator.MaxSprintTitleLength"/> (measured after redaction) is refused
    /// with <see cref="DiagnosticCodes.SprintTitleTooLong"/> before anything is written.</summary>
    Task<CreateSprintResult> CreateSprintAsync(string? projectRoot, string? title, CancellationToken cancellationToken);

    /// <summary>Advances a sprint one legal hop (`draft` to `ready`, then `ready` to `running`) —
    /// not a single call to a running sprint, matching <see cref="SprintOrchestrator"/>'s own
    /// contract. Not confirmable: starting/advancing a sprint is additive, not destructive.</summary>
    Task<SprintTransitionResult> RunSprintAsync(string? projectRoot, Guid sprintId, CancellationToken cancellationToken);

    /// <summary>Un-blocks a `blocked` sprint back to `ready`. Not confirmable: resuming is
    /// additive, not destructive.</summary>
    Task<SprintTransitionResult> ResumeSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken);

    /// <summary>Cancels a sprint. Confirmable like <see cref="InstallIntegrationAsync"/>/
    /// <see cref="RemoveIntegrationAsync"/>: <paramref name="confirmed"/> may fall back to
    /// `interaction.confirm_destructive` — this is an ordinary destructive mutation, not one of the
    /// human-only capabilities <see cref="ResolveGateAsync"/>/<see cref="SupersedeAttemptAsync"/>
    /// are.</summary>
    Task<SprintTransitionResult> CancelSprintAsync(
        string? projectRoot, Guid sprintId, bool confirmed, CancellationToken cancellationToken);

    /// <summary>Plan section 8.5's `sprint.move_stage` capability, permission
    /// `human_stage_transition_confirm`: commits an already-assessed advance or rewind. The Host
    /// recomputes <see cref="ForgeApplication.AssessStageTransitionAsync"/> immediately before
    /// mutating and rejects a
    /// stale <paramref name="expectedStateVersion"/>/<paramref name="assessmentToken"/> without any
    /// side effect -- a caller-held assessment is never trusted (ADR 0046). <paramref name="reason"/>
    /// is mandatory (and bounded) for a rewind, ignored for an advance (plan section 8.3 requires no
    /// reason to move into normal, unstarted territory); <paramref name="confirmed"/> must be
    /// <see langword="true"/> — the same no-config-bypass rule as <see cref="SupersedeAttemptAsync"/>.
    /// </summary>
    Task<MoveStageResult> MoveSprintToStageAsync(
        string? projectRoot,
        Guid sprintId,
        string targetStageId,
        long expectedStateVersion,
        string? assessmentToken,
        string? reason,
        bool confirmed,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Post-release timeline gap closure (plan section 4.3/6.3, ADR 0054): appends a bounded
    /// user-posted message to the sprint's own timeline. Not confirmable/destructive -- posting a
    /// message is additive, matching <see cref="CreateSprintAsync"/>/<see cref="RunSprintAsync"/>'s
    /// own reasoning (plan section 6.4's <c>AvailableAction.safety_class</c> concept: this needs no
    /// confirmation gate, unlike <see cref="SupersedeAttemptAsync"/> or <see cref="CancelSprintAsync"/>).
    /// </summary>
    Task<PostSprintMessageResult> PostSprintMessageAsync(
        string? projectRoot, Guid sprintId, string text, CancellationToken cancellationToken);
}

/// <summary>
/// The single entry point both surfaces use. Presentation adapters format and collect input;
/// every check, mutation, and recommendation is decided here.
/// </summary>
public sealed class ForgeApplication(
    StartupPipeline pipeline,
    ProjectRootResolver rootResolver,
    ProjectInitializer initializer,
    StartupRecovery recovery,
    StatusAdvisor advisor,
    IConfigurationRegistry registry,
    ScopedConfigurationService configuration,
    IProviderToolchainManager providerToolchain,
    ProviderCatalog providerCatalog,
    ControlEventsReader eventsReader,
    IProviderEnablementSource providerEnablement,
    IntegrationInstallationService integrationInstallation,
    ISprintStore sprintStore,
    SprintScheduler scheduler,
    SprintOrchestrator orchestrator,
    IRepository repository,
    RoutingLedger routingLedger,
    IWorktreeManager worktrees,
    IEnvironmentPaths paths,
    IFileSystem fileSystem,
    IClock clock,
    StopOperationCoordinator stopCoordinator,
    ActiveOperationRegistry activeOperations,
    StageTransitionAssessor stageAssessor,
    StageTransitionCoordinator stageCoordinator,
    WorkspaceSummaryProjector workspaceSummary,
    SprintTimelineProjector sprintTimeline,
    AvailableActionProjector availableActions) : IForgeMutations
{
    public const string InitializeProjectAction = "initialize_project";

    /// <summary>Matches <c>ControlPlaneHostedService</c>'s own `typeof(...).Assembly.GetName().Version`
    /// idiom for reporting a build's own product version (ADR 0011's `generator_version`).</summary>
    private static readonly string GeneratorVersion =
        typeof(ForgeApplication).Assembly.GetName().Version!.ToString(3);

    /// <summary>The key any surface must present to initialize the observed project state.</summary>
    public static Guid InitializationKey(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return StatusAdvisor.IdempotencyKey(
            InitializeProjectAction,
            new("project", snapshot.Project.Root),
            snapshot.StateVersion);
    }

    public async Task<StartupStatus> GetStartupStatusAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        (await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false)).Status;

    /// <summary>Runs the startup sequence once and derives the status snapshot from it.</summary>
    public async Task<ProjectOverview> GetOverviewAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        await GetOverviewAsync(projectRoot, SnapshotDetail.Summary, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Same as <see cref="GetOverviewAsync(string?,CancellationToken)"/>, additionally
    /// requesting the named or (with <see cref="SnapshotDetail.Full"/>) active sprint's detail
    /// section — the read model behind `GetProjectSnapshot(detail, sprint_id?)`.</summary>
    public async Task<ProjectOverview> GetOverviewAsync(
        string? projectRoot,
        SnapshotDetail detail,
        Guid? sprintId,
        CancellationToken cancellationToken)
    {
        (StartupStatus startup, ProviderToolchainStatus providers) =
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return new(
            startup,
            await advisor
                .CreateSnapshotAsync(startup, providers, providerCatalog, detail, sprintId, cancellationToken)
                .ConfigureAwait(false));
    }

    public async Task<ProjectSnapshot> GetProjectSnapshotAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        (await GetOverviewAsync(projectRoot, cancellationToken).ConfigureAwait(false)).Snapshot;

    public async Task<ProjectSnapshot> GetProjectSnapshotAsync(
        string? projectRoot,
        SnapshotDetail detail,
        Guid? sprintId,
        CancellationToken cancellationToken) =>
        (await GetOverviewAsync(projectRoot, detail, sprintId, cancellationToken).ConfigureAwait(false)).Snapshot;

    /// <summary>ADR 0005/0038's `forge doctor --bundle`: allowlisted, redacted operational evidence
    /// only (`diagnostic-bundle.schema.json`). Every value here is already a machine code, a count,
    /// or a boolean — nothing free-text (prompts, provider output, diffs, source contents, raw
    /// command lines, credentials, environment values, or unredacted paths) is ever included, so no
    /// section needs <see cref="Infrastructure.SecretRedactor"/> the way <see cref="Infrastructure.SafeLogger"/>
    /// does; the allowlist itself is the redaction. Each section is collected independently and, if
    /// it throws for any reason, added to <see cref="DiagnosticBundle.Omissions"/> by name instead of
    /// failing the whole bundle — the same "if safe collection cannot be proven, omit rather than
    /// guess" rule ADR 0005 states for redaction specifically, applied uniformly here since a single
    /// broken section (e.g. a corrupt sprint file) must never hide every other section's own healthy
    /// evidence from `forge doctor --bundle`, which exists precisely to diagnose that kind of
    /// problem.</summary>
    public async Task<DiagnosticBundle> CollectDiagnosticBundleAsync(
        string? projectRoot, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        List<string> omissions = [];

        IReadOnlyList<DiagnosticProviderVersion> providers = [];
        IReadOnlyList<StartupCheck> startupChecks = [];
        DiagnosticProjectSummary project = new(false, 0);
        try
        {
            ProjectOverview overview =
                await GetOverviewAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            startupChecks = overview.Startup.Checks;
            providers = [.. overview.Snapshot.Providers.Select(
                entry => new DiagnosticProviderVersion(entry.Id, entry.Version))];
            project = new(overview.Snapshot.Project.Initialized, overview.Snapshot.Sprints.Count);
        }
        catch (Exception)
        {
            omissions.Add("startup_checks");
            omissions.Add("providers");
            omissions.Add("project");
        }

        IReadOnlyList<SprintId> sprintIds = [];
        if (status.Initialized)
        {
            try
            {
                sprintIds = await sprintStore.ListAsync(status.Root, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                omissions.Add("event_log_integrity");
                omissions.Add("circuit_breakers");
                omissions.Add("retry_budget");
            }
        }

        DiagnosticEventLogIntegrity eventLogIntegrity = new(true, DiagnosticCodes.None);
        if (!omissions.Contains("event_log_integrity"))
        {
            try
            {
                eventLogIntegrity = await CollectEventLogIntegrityAsync(status.Root, sprintIds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                omissions.Add("event_log_integrity");
            }
        }

        DiagnosticWorktreeRegistrations worktreeRegistrations = new(0, 0);
        try
        {
            IReadOnlyList<WorktreeRegistration> registrations = status.Initialized
                ? await worktrees.ListAsync(status.Root, cancellationToken).ConfigureAwait(false)
                : [];
            worktreeRegistrations = new(
                registrations.Count, registrations.Count(entry => !entry.Exists));
        }
        catch (Exception)
        {
            omissions.Add("worktree_registrations");
        }

        List<DiagnosticCircuitBreaker> circuitBreakers = [];
        DiagnosticRetryBudget retryBudget = new(RoutingLedger.DefaultRetryBudget, RoutingLedger.DefaultRetryBudget);
        if (!omissions.Contains("circuit_breakers"))
        {
            try
            {
                (circuitBreakers, retryBudget) = await CollectRoutingStateAsync(
                    status.Root, sprintIds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                omissions.Add("circuit_breakers");
                omissions.Add("retry_budget");
            }
        }

        List<DiagnosticWritableProbe> writableProbes = [];
        try
        {
            writableProbes = await CollectWritableProbesAsync(status.Root, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            omissions.Add("writable_probes");
        }

        return new(
            DiagnosticBundle.ContractVersion,
            clock.UtcNow,
            GeneratorVersion,
            ControlProtocol.Version,
            providers,
            startupChecks,
            project,
            eventLogIntegrity,
            worktreeRegistrations,
            circuitBreakers,
            retryBudget,
            writableProbes,
            omissions);
    }

    /// <summary>ADR 0042's `forge eval`: a pass/fail report over the updater, provider, bootstrap,
    /// and workflow subsystems plus the project model-policy gate. Every check reuses an existing
    /// command's own logic rather than a second probing path -- <see cref="StartupPipeline.RunAsync"/>
    /// (already `forge doctor --startup`'s own backing) covers the first three areas directly;
    /// <see cref="Forge.Domain.SprintGraphValidator"/> against the canonical
    /// <see cref="ImplementationCriticalGraphBuilder"/> graph covers workflow structural validity with
    /// no sprint created; <see cref="ModelPolicyGate"/> covers the allowlist gate against the same
    /// frozen-candidate derivation <c>SprintOrchestrator.CreateSprintAsync</c> uses.</summary>
    public async Task<EvaluationReport> RunEvaluationAsync(
        string? projectRoot, CancellationToken cancellationToken)
    {
        (StartupStatus startup, _) = await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        List<EvaluationCheck> checks = [.. startup.Checks.Select(FromStartupCheck)];

        checks.Add(SprintGraphValidator.IsValid(ImplementationCriticalGraphBuilder.Build())
            ? EvaluationCheck.Passed(EvaluationArea.Workflow, "graph")
            : new(EvaluationArea.Workflow, "graph", EvaluationState.Failed, DiagnosticCodes.SprintGraphInvalid));

        checks.AddRange(await EvaluateModelPolicyAsync(projectRoot, cancellationToken).ConfigureAwait(false));

        EvaluationState state = checks.Any(check => check.State == EvaluationState.Failed)
            ? EvaluationState.Failed
            : checks.Any(check => check.State == EvaluationState.Blocked)
                ? EvaluationState.Blocked
                : EvaluationState.Passed;
        return new(EvaluationReport.ContractVersion, clock.UtcNow, state, checks);
    }

    /// <summary>Round 1 review of PR #87: an unmapped <see cref="StartupCheckId"/> now throws a
    /// named <see cref="ArgumentOutOfRangeException"/> (matching the identical pattern already used
    /// for <see cref="StartupCheckState"/> just below) instead of an opaque
    /// <see cref="KeyNotFoundException"/> from a dictionary indexer. Every existing
    /// <c>EvaluationTests</c> case calls <see cref="RunEvaluationAsync"/> against the full,
    /// unfiltered check list <see cref="StartupPipeline.RunAsync"/> always returns, so a future
    /// <see cref="StartupCheckId"/> added without a corresponding arm here already fails the very
    /// next test run, not silently in production.</summary>
    private static EvaluationArea AreaFor(StartupCheckId id) => id switch
    {
        StartupCheckId.UserConfiguration or StartupCheckId.Language or StartupCheckId.Platform or
            StartupCheckId.ProjectRoot or StartupCheckId.ProjectConfiguration => EvaluationArea.Bootstrap,
        StartupCheckId.UpdateStrategy or StartupCheckId.Release => EvaluationArea.Updater,
        StartupCheckId.Providers => EvaluationArea.Provider,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unmapped startup check id."),
    };

    private static EvaluationCheck FromStartupCheck(StartupCheck check) => new(
        AreaFor(check.Id),
        JsonNamingPolicy.SnakeCaseLower.ConvertName(check.Id.ToString()),
        check.State switch
        {
            StartupCheckState.Passed => EvaluationState.Passed,
            StartupCheckState.Skipped => EvaluationState.Skipped,
            StartupCheckState.Blocked => EvaluationState.Blocked,
            StartupCheckState.Failed => EvaluationState.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(check), check.State, "Unknown startup check state."),
        },
        check.DiagnosticCode);

    /// <summary>One check per frozen-candidate provider (the same
    /// <c>ProviderCatalog.ResolveEnabled</c> derivation <c>SprintOrchestrator.CreateSprintAsync</c>
    /// freezes) against the project's own configured <c>models.allowed_models</c> policy -- a
    /// dry-run report requiring no sprint. An unreadable project configuration reports the whole
    /// area <see cref="EvaluationState.Blocked"/> rather than guessing at an empty policy.</summary>
    private async Task<IReadOnlyList<EvaluationCheck>> EvaluateModelPolicyAsync(
        string? projectRoot, CancellationToken cancellationToken)
    {
        ConfigurationView project = await GetProjectConfigurationAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        if (project.DiagnosticCode != DiagnosticCodes.None)
        {
            return [new(EvaluationArea.ModelPolicy, "configuration", EvaluationState.Blocked, project.DiagnosticCode)];
        }

        IReadOnlyList<string> allowedModels = ModelPolicyGate.ParseAllowedModels(project.Values);
        IReadOnlyList<string>? enabledProviderIds = await providerEnablement
            .GetEnabledIdsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ILlmProvider> enabledProviders = providerCatalog.ResolveEnabled(enabledProviderIds);
        List<EvaluationCheck> checks = [.. enabledProviders.Select(provider =>
            ModelPolicyGate.IsAllowed(provider.Id.Value, provider.DefaultModel, allowedModels)
                ? EvaluationCheck.Passed(EvaluationArea.ModelPolicy, provider.Id.Value)
                : new EvaluationCheck(
                    EvaluationArea.ModelPolicy,
                    provider.Id.Value,
                    EvaluationState.Failed,
                    DiagnosticCodes.ModelPolicyViolation))];
        // Round 1 review of PR #87: a configured entry naming a provider id no enabled provider
        // matches (a typo, a stale/renamed entry) otherwise enforces nothing and reports nothing —
        // surfaced here as its own check rather than silently passing. Round 2 review: Blocked, not
        // Failed -- ModelPolicyGate.UnmatchedProviderIds' own doc comment calls this legitimate ("a
        // project may list models for a provider it has not enabled yet"), and Failed would both
        // move `forge eval`'s exit code and contradict that doc comment's own claim.
        checks.AddRange(ModelPolicyGate
            .UnmatchedProviderIds(allowedModels, [.. enabledProviders.Select(provider => provider.Id.Value)])
            .Select(providerId => new EvaluationCheck(
                EvaluationArea.ModelPolicy, providerId, EvaluationState.Blocked, DiagnosticCodes.ModelPolicyProviderUnknown)));
        return checks;
    }

    /// <summary>The minimal proactive integrity check this codebase has today: every persisted
    /// sprint's definition and folded state must load without throwing. `FileSprintEventLog` already
    /// throws <see cref="DiagnosticCodes.WorkflowLogCorrupted"/>-shaped exceptions reactively per
    /// record (see its own read methods) — this walks every sprint once so corruption is reported
    /// proactively by `forge doctor --bundle` instead of only being discovered the next time
    /// something happens to touch the affected sprint.</summary>
    private async Task<DiagnosticEventLogIntegrity> CollectEventLogIntegrityAsync(
        string root, IReadOnlyList<SprintId> sprintIds, CancellationToken cancellationToken)
    {
        foreach (SprintId sprintId in sprintIds)
        {
            try
            {
                await sprintStore.LoadDefinitionAsync(root, sprintId, cancellationToken).ConfigureAwait(false);
                await sprintStore.LoadAsync(root, sprintId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return new(false, DiagnosticCodes.WorkflowLogCorrupted);
            }
        }

        return new(true, DiagnosticCodes.None);
    }

    /// <summary>Circuit breakers are sprint-scoped (<see cref="RoutingLedger"/>'s own remarks), so a
    /// project-wide list is every distinct <see cref="HealthKey"/> any sprint's route decisions ever
    /// named, each resolved through the same derivation <see cref="RoutingLedger.GetCircuitBreakerAsync"/>
    /// already performs. The retry budget is likewise per-sprint (one shared budget for every
    /// node/attempt in that sprint) with no project-wide figure to report as-is: <see cref="DiagnosticRetryBudget.Total"/>
    /// is <see cref="RoutingLedger.DefaultRetryBudget"/> itself, since every sprint is given the
    /// identical fixed total, and <see cref="DiagnosticRetryBudget.Remaining"/> is the minimum
    /// remaining across every sprint that has consumed any of it — the sprint closest to exhausting
    /// its budget is the one worth surfacing in a diagnostic snapshot, and reporting the full total
    /// when no sprint has route decisions yet correctly says nothing has been consumed.</summary>
    private async Task<(List<DiagnosticCircuitBreaker> Breakers, DiagnosticRetryBudget RetryBudget)>
        CollectRoutingStateAsync(string root, IReadOnlyList<SprintId> sprintIds, CancellationToken cancellationToken)
    {
        List<DiagnosticCircuitBreaker> breakers = [];
        int? minRemaining = null;
        foreach (SprintId sprintId in sprintIds)
        {
            IReadOnlyList<RouteDecision> decisions = await routingLedger
                .GetRouteDecisionsAsync(root, sprintId, cancellationToken).ConfigureAwait(false);
            if (decisions.Count == 0)
            {
                continue;
            }

            RetryBudgetRecord budget = await routingLedger
                .GetRetryBudgetAsync(root, sprintId, cancellationToken).ConfigureAwait(false);
            minRemaining = Math.Min(minRemaining ?? budget.Remaining, budget.Remaining);

            foreach (HealthKey key in decisions.Select(decision => decision.Key).Distinct())
            {
                CircuitBreakerRecord? breaker = await routingLedger
                    .GetCircuitBreakerAsync(root, sprintId, key, cancellationToken).ConfigureAwait(false);
                if (breaker is not null)
                {
                    breakers.Add(new(
                        $"{sprintId.Value:D}/{key.Canonical}", breaker.State));
                }
            }
        }

        return (breakers, new(RoutingLedger.DefaultRetryBudget, minRemaining ?? RoutingLedger.DefaultRetryBudget));
    }

    /// <summary>Probes exactly the two directories Forge itself ever writes durable state to: the
    /// project's own `.forge/` directory and this instance's namespaced share of
    /// <see cref="IEnvironmentPaths.LocalApplicationData"/> (user configuration, worktrees, and —
    /// once they exist — logs/caches, per ADR 0005). Writes and immediately overwrites a fixed,
    /// clearly diagnostic-named marker file rather than a randomly named one, so repeated runs leave
    /// at most one stray file per directory instead of accumulating one per run.</summary>
    private async Task<List<DiagnosticWritableProbe>> CollectWritableProbesAsync(
        string root, CancellationToken cancellationToken)
    {
        (string Label, string Directory)[] targets =
        [
            ("project", ProjectRootResolver.ForgeDirectory(root)),
            ("local_application_data", Path.Combine(paths.LocalApplicationData, "Forge", paths.InstanceId)),
        ];
        List<DiagnosticWritableProbe> probes = [];
        foreach ((string label, string directory) in targets)
        {
            bool writable;
            try
            {
                Directory.CreateDirectory(directory);
                await fileSystem.WriteAllTextAsync(
                    Path.Combine(directory, ".diagnostic-write-probe"),
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);
                writable = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writable = false;
            }

            probes.Add(new(label, writable));
        }

        return probes;
    }

    /// <summary>The bounded, cursor-driven incremental read behind `ReadControlEvents`. See
    /// <see cref="ControlEventsReader"/> for the merge/cursor contract. An uninitialized or
    /// unresolvable project root reports no events rather than probing a `.forge/sprints/`
    /// directory that cannot exist yet — matching <see cref="StatusAdvisor.CreateSnapshotAsync(StartupStatus,ProviderToolchainStatus,ProviderCatalog,SnapshotDetail,Guid?,CancellationToken)"/>.</summary>
    public async Task<ControlEventsPage> ReadControlEventsAsync(
        string? projectRoot,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (status.Initialized)
        {
            return await eventsReader.ReadAsync(status.Root, cursor, cancellationToken).ConfigureAwait(false);
        }

        // An uninitialized project has no journal to poll, but that is a distinct outcome from a
        // genuine "caught up, nothing new" read of an initialized project — both must not collapse
        // to the same DiagnosticCodes.None a caller could otherwise mistake for real progress. A
        // cursor that was itself already stale/malformed still reports that, unmasked.
        ControlEventsPage empty = ControlEventsPage.Empty(cursor);
        return empty.DiagnosticCode == DiagnosticCodes.None
            ? empty with { DiagnosticCode = DiagnosticCodes.ProjectNotInitialized }
            : empty;
    }

    /// <summary>
    /// Read-only discovery, matching the `provider.health` capability's declared `query`/`read`
    /// contract. Installing or updating is a separate, explicit action: <see cref="RefreshProviderHealthAsync"/>.
    /// </summary>
    public Task<ProviderToolchainStatus> GetProviderHealthAsync(CancellationToken cancellationToken) =>
        providerToolchain.CheckAsync(cancellationToken);

    /// <summary>ADR 0008: "`forge models --refresh` bypasses the time limit but still checks
    /// availability before invoking an updater." Re-checks every enabled provider against a
    /// fresh, cache-bypassing release lookup and installs or updates only when that check finds a
    /// missing/broken install or a newer release, then rechecks authentication for all of them.
    /// Routine startup performs the same maintenance respecting the cache instead — see
    /// <see cref="StartupPipeline"/>.</summary>
    public Task<ProviderToolchainStatus> RefreshProviderHealthAsync(CancellationToken cancellationToken) =>
        providerToolchain.EnsureReadyAsync(bypassReleaseCache: true, cancellationToken);

    /// <summary>Projects a toolchain status onto the versioned provider-health contract, adding a
    /// read-only entry for every registered-but-disabled provider (ADR 0008/P8.83-88) — the same
    /// projection <see cref="GetOverviewAsync(string?,SnapshotDetail,Guid?,CancellationToken)"/>
    /// folds into the snapshot, exposed directly for callers (e.g. `forge models`) that only need
    /// provider health, not a full snapshot.</summary>
    public IReadOnlyList<ProviderHealthEntry> ProjectProviderHealth(ProviderToolchainStatus status) =>
        ProviderHealthProjector.Project(status, providerCatalog);

    /// <summary>Plan section 6.5's reserved `provider.quota_status` query (ADR 0043/0052): every
    /// enabled and registered-but-disabled provider's quota reading. Read-only and offline, like
    /// <see cref="GetProviderHealthAsync"/> -- but, like that method, it issues its own fresh,
    /// uncached <see cref="IProviderToolchainManager.CheckAsync"/> probe (a `--version` child process
    /// plus an authentication probe per enabled provider); it is not "cheap" to call repeatedly. For
    /// `forge models quota`, which has not already checked the toolchain this invocation, that cost
    /// is the whole point. A caller that already holds a <see cref="ProviderHealthEntry"/> set from
    /// an earlier probe in the same render pass (e.g. <c>SidebarViewModel</c>, which gets one
    /// from <see cref="GetWorkspaceSummaryAsync"/> per project) must call
    /// <see cref="ProjectProviderQuota"/> instead of this method, to avoid a redundant second probe
    /// for a value ADR 0052 guarantees is constant (`Unknown`) regardless (PR #100 review finding 1).</summary>
    public async Task<IReadOnlyList<ProviderQuotaSnapshot>> GetProviderQuotaStatusAsync(CancellationToken cancellationToken)
    {
        ProviderToolchainStatus status = await providerToolchain.CheckAsync(cancellationToken).ConfigureAwait(false);
        return ProviderQuotaProjector.Project(status, providerCatalog, clock.UtcNow);
    }

    /// <summary>Projects an already-computed <see cref="ProviderHealthEntry"/> set onto the quota
    /// contract without any new toolchain probe -- the counterpart to
    /// <see cref="ProjectProviderHealth"/> for a caller that already holds provider health from
    /// earlier in the same render pass and must not pay for
    /// <see cref="GetProviderQuotaStatusAsync"/>'s own fresh probe again (PR #100 review finding 1).</summary>
    public IReadOnlyList<ProviderQuotaSnapshot> ProjectProviderQuota(IReadOnlyCollection<ProviderHealthEntry> providers) =>
        ProviderQuotaProjector.Project(providers, providerCatalog, clock.UtcNow);

    /// <summary>Quarantines unreadable configuration so a failed startup can reach a usable state.</summary>
    public async Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        StartupStatus startup =
            (await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false)).Status;
        if (startup.FirstFailure is not { } failure)
        {
            return new(true, null, DiagnosticCodes.None);
        }

        if (!confirmed)
        {
            return new(false, failure.Id, DiagnosticCodes.ConfirmationRequired);
        }

        RecoverStartupResult result =
            await recovery.RecoverAsync(startup, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return result;
        }

        // Success means the startup sequence no longer fails, not merely that a file moved.
        StartupStatus repaired =
            (await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false)).Status;
        return repaired.FirstFailure is { } remaining
            ? new(false, remaining.Id, remaining.DiagnosticCode)
            : result;
    }

    public async Task<InitializeProjectResult> InitializeProjectAsync(
        InitializeProjectCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        StartupStatus startup =
            (await pipeline.RunAsync(command.Root, cancellationToken).ConfigureAwait(false)).Status;
        ProjectRootStatus status = startup.Project;
        if (startup.FirstFailure is { } failure)
        {
            return new(false, status.Root, null, failure.DiagnosticCode);
        }

        long stateVersion = StatusAdvisor.StateVersion(status);
        if (command.ExpectedStateVersion != stateVersion ||
            command.IdempotencyKey != StatusAdvisor.IdempotencyKey(
                InitializeProjectAction,
                new("project", status.Root),
                stateVersion))
        {
            return new(false, status.Root, null, DiagnosticCodes.SuggestionStale);
        }

        bool confirmed = command.Confirmed ||
            !await RequiresConfirmationAsync(cancellationToken).ConfigureAwait(false);
        return await initializer
            .InitializeAsync(
                new(
                    status.Root,
                    confirmed,
                    command.UserFacingLanguage,
                    command.AgentFacingLanguage),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Honours `interaction.confirm_destructive`; an unreadable value stays fail-closed.</summary>
    private async Task<bool> RequiresConfirmationAsync(CancellationToken cancellationToken)
    {
        ConfigurationView user =
            await GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        EffectiveConfigurationValue? value = user.Values
            .FirstOrDefault(item => item.Key == "interaction.confirm_destructive");
        return value?.Value.ValueKind != JsonValueKind.False;
    }

    public async Task<ConfigurationView> GetUserConfigurationAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return new(
                await configuration.GetUserAsync(null, cancellationToken).ConfigureAwait(false),
                DiagnosticCodes.None);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new([], DiagnosticCodes.ConfigurationInvalid);
        }
    }

    public async Task<ConfigurationView> GetProjectConfigurationAsync(
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new([], status.DiagnosticCode);
        }

        try
        {
            return new(
                await configuration
                    .GetProjectAsync(status.Root, cancellationToken)
                    .ConfigureAwait(false),
                DiagnosticCodes.None);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new([], DiagnosticCodes.ConfigurationInvalid);
        }
    }

    /// <summary>Converts the raw surface input using the declared type of the key.</summary>
    public async Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken)
    {
        ConfigurationKey descriptor;
        try
        {
            descriptor = registry.FindRequired(key);
        }
        catch (KeyNotFoundException)
        {
            return new(false, DiagnosticCodes.ConfigurationKeyUnknown);
        }

        return await SetConfigurationAsync(
                scope,
                projectRoot,
                key,
                ConfigurationValueParser.Parse(rawValue, descriptor),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        try
        {
            if (scope == ConfigurationScope.User)
            {
                RequireRegisteredProviders(key, value);
                await configuration.SetUserAsync(key, value, cancellationToken).ConfigureAwait(false);
                return ConfigurationWriteResult.Success;
            }

            ProjectRootStatus status = await rootResolver
                .ResolveAsync(projectRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!status.Initialized)
            {
                return new(false, status.DiagnosticCode);
            }

            await configuration
                .SetProjectAsync(status.Root, key, value, cancellationToken)
                .ConfigureAwait(false);
            return ConfigurationWriteResult.Success;
        }
        catch (ConfigurationScopeException)
        {
            return new(false, DiagnosticCodes.ConfigurationScopeViolation);
        }
        catch (KeyNotFoundException)
        {
            return new(false, DiagnosticCodes.ConfigurationKeyUnknown);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new(false, DiagnosticCodes.ConfigurationInvalid);
        }
    }

    /// <summary>The read-only `forge integration skill generate` preview (ADR 0011) — a plain read
    /// like <see cref="GetStartupStatusAsync"/>, never routed through the Host, since it writes
    /// nothing.</summary>
    public async Task<IntegrationInspectionResult> InspectIntegrationAsync(
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return IntegrationInspectionResult.Empty(status.DiagnosticCode);
        }

        (IReadOnlyList<ProviderId> Enabled, string UserFacing, string AgentFacing)? inputs =
            await ResolveIntegrationInputsAsync(status.Root, cancellationToken).ConfigureAwait(false);
        if (inputs is not { } resolved)
        {
            return IntegrationInspectionResult.Empty(DiagnosticCodes.ConfigurationInvalid);
        }

        return await integrationInstallation
            .InspectAsync(status.Root, resolved.Enabled, resolved.UserFacing, resolved.AgentFacing, GeneratorVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IntegrationWriteResult> InstallIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        MutateIntegrationAsync(projectRoot, confirmed, integrationInstallation.InstallAsync, cancellationToken);

    public Task<IntegrationWriteResult> RemoveIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        MutateIntegrationAsync(projectRoot, confirmed, integrationInstallation.RemoveAsync, cancellationToken);

    private async Task<IntegrationWriteResult> MutateIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        Func<string, IReadOnlyList<ProviderId>, string, string, string, CancellationToken, Task<IntegrationWriteResult>> mutate,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return IntegrationWriteResult.Empty(status.DiagnosticCode);
        }

        bool actuallyConfirmed = confirmed ||
            !await RequiresConfirmationAsync(cancellationToken).ConfigureAwait(false);
        if (!actuallyConfirmed)
        {
            return IntegrationWriteResult.Empty(DiagnosticCodes.ConfirmationRequired);
        }

        (IReadOnlyList<ProviderId> Enabled, string UserFacing, string AgentFacing)? inputs =
            await ResolveIntegrationInputsAsync(status.Root, cancellationToken).ConfigureAwait(false);
        if (inputs is not { } resolved)
        {
            return IntegrationWriteResult.Empty(DiagnosticCodes.ConfigurationInvalid);
        }

        return await mutate(
                status.Root, resolved.Enabled, resolved.UserFacing, resolved.AgentFacing, GeneratorVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The same enabled-provider resolution <c>SprintOrchestrator.CreateSprintAsync</c>
    /// freezes into a sprint (ADR 0008), and the same artifact-language extraction
    /// <c>SprintOrchestrator.ConversationLanguageAsync</c> uses for `language.llm` — re-derived
    /// fresh on every call, never cached, since integration state carries no frozen snapshot of its
    /// own (ADR 0011).</summary>
    private async Task<(IReadOnlyList<ProviderId> Enabled, string UserFacing, string AgentFacing)?> ResolveIntegrationInputsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? enabledProviderIds =
            await providerEnablement.GetEnabledIdsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderId> enabled =
            [.. providerCatalog.ResolveEnabled(enabledProviderIds).Select(provider => provider.Id)];

        ConfigurationView project = await GetProjectConfigurationAsync(root, cancellationToken).ConfigureAwait(false);
        if (project.DiagnosticCode != DiagnosticCodes.None)
        {
            return null;
        }

        string userFacing = project.Values
            .FirstOrDefault(value => value.Key == "artifacts.language.user_facing")?.Value.GetString() ?? "en";
        string agentFacing = project.Values
            .FirstOrDefault(value => value.Key == "artifacts.language.agent_facing")?.Value.GetString() ?? "en";
        return (enabled, userFacing, agentFacing);
    }

    /// <summary>ADR 0005's human-only `workflow.review` capability. The caller supplies only
    /// <paramref name="sprintId"/>/<paramref name="nodeId"/>/<paramref name="approved"/> — the
    /// expected node version and idempotency key are derived here from a fresh state read, exactly
    /// like <c>SurfaceParityTests</c> already does in-process, so no snapshot projection needs to
    /// expose a raw entity version for a caller to round-trip back in.</summary>
    public async Task<NodeActionResult> ResolveGateAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool approved,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        if (!confirmed)
        {
            return new(false, null, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        SprintWorkflowState? state =
            await sprintStore.LoadAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        Guid key = SprintScheduler.ResolveHumanGateKey(id, node);
        return await scheduler
            .ResolveHumanGateAsync(status.Root, id, nodeId, approved, node.Version, key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The human-only `workflow.confirm` capability. Same server-side
    /// version/idempotency-key derivation as <see cref="ResolveGateAsync"/>.</summary>
    public async Task<RecordConfirmationResult> ConfirmNodeAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        ConfirmationOutcome outcome,
        string definitionOfDone,
        IReadOnlyList<ConfirmationEvidence> evidence,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(definitionOfDone);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!confirmed)
        {
            return new(false, null, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        SprintWorkflowState? state =
            await sprintStore.LoadAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        Guid key = SprintScheduler.ConfirmNodeKey(id, node);
        return await scheduler
            .ConfirmNodeAsync(
                status.Root, id, nodeId, outcome, definitionOfDone, evidence, node.Version, key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The human-only `workflow.test_work` capability. Same server-side
    /// version/idempotency-key derivation as <see cref="ResolveGateAsync"/>.</summary>
    public async Task<RecordTestWorkResult> RecordTestWorkAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        TestWorkOutcome outcome,
        string justification,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(justification);
        if (!confirmed)
        {
            return new(false, null, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        SprintWorkflowState? state =
            await sprintStore.LoadAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, DiagnosticCodes.NodeNotFound);
        }

        Guid key = SprintScheduler.RecordTestWorkKey(id, node);
        return await scheduler
            .RecordTestWorkAsync(status.Root, id, nodeId, outcome, justification, node.Version, key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>ADR 0036's human-only `workflow.finalize` capability. Unlike every other node-
    /// settling mutation, this one's real action (a git merge into the project's own working
    /// directory) is genuine external I/O — deliberately orchestrated here rather than folded into
    /// one <see cref="SprintScheduler"/> call the way <see cref="ConfirmNodeAsync"/>/
    /// <see cref="RecordTestWorkAsync"/> are, since <see cref="SprintScheduler"/> itself stays a
    /// pure state machine with no I/O beyond durable event-log state. This mirrors how every
    /// model-bearing node's own executor is structured (real work happens between
    /// <see cref="SprintScheduler.StartAttemptAsync"/> and <see cref="SprintScheduler.CompleteAttemptAsync"/>,
    /// orchestrated outside <see cref="SprintScheduler"/>), just synchronous and CLI-triggered
    /// instead of a background poll loop.</summary>
    public async Task<FinalizeSprintResult> FinalizeSprintAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        if (!confirmed)
        {
            return new(false, null, null, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        SprintWorkflowState? state =
            await sprintStore.LoadAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(false, null, null, DiagnosticCodes.SprintNotFound);
        }

        if (!state.Nodes.TryGetValue(nodeId, out NodeSnapshot? node))
        {
            return new(false, null, null, DiagnosticCodes.NodeNotFound);
        }

        // An already-terminal node is never re-acted on: a resumed call after the sprint already
        // completed reports that success back rather than attempting a second, redundant merge (a
        // fast-forward-only merge of an already-merged branch is harmless, but there is nothing left
        // to do, and re-running StartAttemptAsync against a Succeeded node would only be rejected).
        // The node and the sprint settle in two separate durable writes (CompleteAttemptAsync then
        // CompleteSprintAsync); a crash or dropped call between them can leave the node Succeeded
        // while the sprint is still ReadyToFinalize. CompleteSprintAsync is idempotent, so a resumed
        // call closes that gap here instead of reporting a false success for a wedged sprint.
        if (node.State is NodeState.Succeeded or NodeState.Failed)
        {
            if (node.State == NodeState.Succeeded && state.Sprint.State != SprintState.Completed)
            {
                SprintTransitionResult resumed = await scheduler
                    .CompleteSprintAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
                return new(resumed.Succeeded, node, resumed.Sprint, resumed.DiagnosticCode);
            }

            bool succeeded = node.State == NodeState.Succeeded;
            return new(succeeded, node, succeeded ? state.Sprint : null, DiagnosticCodes.None);
        }

        SprintDefinition? definition =
            await sprintStore.LoadDefinitionAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return new(false, null, null, DiagnosticCodes.SprintNotFound);
        }

        NodeDefinition? definedNode = definition.Graph.FirstOrDefault(item => item.Id == nodeId);
        if (definedNode is null || definedNode.Role != NodeRole.Finalization || definedNode.Kind != NodeKind.Work)
        {
            return new(false, null, null, DiagnosticCodes.NodeKindMismatch);
        }

        if (definition.DefaultBranch is null)
        {
            return new(false, null, null, DiagnosticCodes.SprintDefaultBranchUnavailable);
        }

        return await CompleteFinalizationAsync(
            status.Root, id, nodeId, node, definition.DefaultBranch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FinalizeSprintResult> CompleteFinalizationAsync(
        string projectRoot,
        SprintId sprintId,
        string nodeId,
        NodeSnapshot node,
        string defaultBranch,
        CancellationToken cancellationToken)
    {
        StartAttemptResult started = await scheduler
            .StartAttemptAsync(projectRoot, sprintId, nodeId, node.Version, cancellationToken)
            .ConfigureAwait(false);
        if (!started.Succeeded || started.AttemptId is not { } attemptId)
        {
            return new(false, null, null, started.DiagnosticCode);
        }

        GitOperationResult merged = await repository
            .MergeSprintIntoDefaultBranchAsync(
                projectRoot, defaultBranch, WorktreeLayout.IntegrationBranch(sprintId), cancellationToken)
            .ConfigureAwait(false);
        if (!merged.Succeeded)
        {
            NodeDiagnostic diagnostic = new(
                merged.DiagnosticCode, "git", merged.DiagnosticCode, new Dictionary<string, string?>(StringComparer.Ordinal));
            CompleteAttemptResult failed = await scheduler.CompleteAttemptAsync(
                projectRoot, sprintId, nodeId, attemptId, false, Digest(merged.DiagnosticCode),
                outputs: [], diagnostics: [diagnostic], cancellationToken).ConfigureAwait(false);
            return new(false, failed.Node, null, merged.DiagnosticCode);
        }

        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            projectRoot, sprintId, nodeId, attemptId, true, Digest(merged.Commit ?? string.Empty),
            outputs: [Digest(merged.Commit ?? string.Empty)], diagnostics: [], cancellationToken)
            .ConfigureAwait(false);
        if (!completed.Succeeded)
        {
            return new(false, completed.Node, null, completed.DiagnosticCode);
        }

        SprintTransitionResult sprintCompleted =
            await scheduler.CompleteSprintAsync(projectRoot, sprintId, cancellationToken).ConfigureAwait(false);
        return new(sprintCompleted.Succeeded, completed.Node, sprintCompleted.Sprint, sprintCompleted.DiagnosticCode);
    }

    private static string Digest(string text) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";

    /// <summary>ADR 0005/0018's human-only `attempt.supersede` capability. Same server-side
    /// version/idempotency-key derivation as <see cref="ResolveGateAsync"/>.</summary>
    public async Task<CompleteAttemptResult> SupersedeAttemptAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        string instruction,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        if (!confirmed)
        {
            return new(false, null, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        AttemptId attempt = new(attemptId);
        SprintWorkflowState? state =
            await sprintStore.LoadAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        if (!state.Attempts.TryGetValue(attemptId.ToString("D"), out AttemptSnapshot? attemptSnapshot))
        {
            return new(false, null, DiagnosticCodes.WorkflowEventConflict);
        }

        Guid key = SprintScheduler.SupersedeAttemptKey(id, attemptSnapshot);
        return await scheduler
            .SupersedeAttemptAsync(
                status.Root, id, attempt, attemptSnapshot.Version, key, confirmed, instruction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Post-release timeline gap closure (ADR 0054): resolves the project root and mints
    /// this message's own idempotency anchor server-side (the same "server-side version/idempotency-
    /// key derivation" <see cref="SupersedeAttemptAsync"/>'s own remarks describe), then delegates to
    /// <see cref="SprintScheduler.PostUserMessageAsync"/>.</summary>
    public async Task<PostSprintMessageResult> PostSprintMessageAsync(
        string? projectRoot, Guid sprintId, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        return await scheduler
            .PostUserMessageAsync(status.Root, new(sprintId), Guid.NewGuid(), text, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>ADR 0044/plan section 7's human-only `workflow.stop_operation` capability. Same
    /// no-config-bypass confirmation rule as <see cref="SupersedeAttemptAsync"/>; every other
    /// rejection reason (no active operation, already-settled attempt, stale sprint, changed active
    /// attempt) is <see cref="StopOperationCoordinator.RequestStopAsync"/>'s own responsibility.</summary>
    public async Task<StopOperationResult> StopCurrentOperationAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return new(false, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, status.DiagnosticCode);
        }

        return await stopCoordinator
            .RequestStopAsync(status.Root, new(sprintId), new(attemptId), activeOperations, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Plan section 8.1's read-only `workflow.assess_stage_transition` query. Not on
    /// <see cref="IForgeMutations"/> (it mutates nothing) -- mirrors <see cref="GetOverviewAsync(string?,CancellationToken)"/>'s
    /// own "resolve root, delegate to a pure computation, return" shape rather than the confirmable
    /// mutation pattern above.</summary>
    public async Task<StageTransitionAssessment> AssessStageTransitionAsync(
        string? projectRoot, Guid sprintId, string targetStageId, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return status.Initialized
            ? await stageAssessor.AssessAsync(status.Root, new(sprintId), targetStageId, cancellationToken)
                .ConfigureAwait(false)
            : StageTransitionAssessment.NotFound(new(sprintId), status.DiagnosticCode);
    }

    /// <summary>ADR 0046's `sprint.move_stage` capability. Same no-config-bypass confirmation rule
    /// as <see cref="SupersedeAttemptAsync"/>; every other rejection reason (stale assessment,
    /// unmet prerequisite, missing rewind reason, terminal sprint) is
    /// <see cref="StageTransitionCoordinator.MoveAsync"/>'s own responsibility.</summary>
    public async Task<MoveStageResult> MoveSprintToStageAsync(
        string? projectRoot,
        Guid sprintId,
        string targetStageId,
        long expectedStateVersion,
        string? assessmentToken,
        string? reason,
        bool confirmed,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return new(false, null, null, DiagnosticCodes.ConfirmationRequired);
        }

        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, null, status.DiagnosticCode);
        }

        return await stageCoordinator.MoveAsync(
            status.Root, new(sprintId), targetStageId, expectedStateVersion, assessmentToken, reason, confirmed,
            idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plan section 6.2's reserved `workspace.summary` query: one project's bounded
    /// sidebar/status-header row (ADR 0043/0049). Deliberately catalog-agnostic -- the CLI's own
    /// `forge workspace summary` calls this once per <see cref="ProjectCatalogEntry"/> and pairs each
    /// result with that entry's own alias/last-route, since a project's Host has no notion of the
    /// local catalog at all. <paramref name="includeDiffStats"/> opts into
    /// <see cref="SprintWorkspaceSummary.DiffStat"/>, the one member of that row that costs `git`
    /// processes to compute (up to three per active sprint) -- left <see langword="false"/> it is
    /// reported absent and this query stays as cheap as it was before ADR 0069, which is what a caller
    /// that fans this out across a whole catalog on every refresh needs (PR #126 review
    /// finding 2).</summary>
    public Task<ProjectWorkspaceSummary> GetWorkspaceSummaryAsync(
        string? projectRoot, bool includeDiffStats, CancellationToken cancellationToken) =>
        workspaceSummary.CreateAsync(projectRoot, includeDiffStats, cancellationToken);

    /// <summary>Plan section 6.3's reserved `sprint.timeline` query: a bounded, cursor-paged
    /// projection of one sprint's existing append-only workflow journal (ADR 0043/0049). Matches
    /// every other read here (<see cref="AssessStageTransitionAsync"/>, <see cref="ReadControlEventsAsync"/>):
    /// resolve root, delegate to a pure computation, return. This is the one method every surface
    /// (CLI text, CLI `--json`, and the Host's wire response) calls to obtain a timeline page, so
    /// <see cref="SprintTimelineRedaction.Apply(SprintTimelinePage)"/> (redaction pass 2 of 2, plan
    /// 12.3) runs exactly once, right here, rather than being left to each caller to remember.</summary>
    public async Task<SprintTimelinePage> GetSprintTimelineAsync(
        string? projectRoot, Guid sprintId, string? cursor, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (status.Initialized)
        {
            SprintTimelinePage page = await sprintTimeline
                .CreateAsync(status.Root, sprintId, cursor, cancellationToken)
                .ConfigureAwait(false);
            return SprintTimelineRedaction.Apply(page);
        }

        SprintTimelinePage empty = SprintTimelinePage.Empty(sprintId, cursor, DiagnosticCodes.None);
        return empty.DiagnosticCode == DiagnosticCodes.None
            ? empty with { DiagnosticCode = DiagnosticCodes.ProjectNotInitialized }
            : empty;
    }

    /// <summary>Plan section 6.4's reserved `workspace.available_actions` query. With
    /// <paramref name="sprintId"/> given, wraps <see cref="AvailableActionProjector.ForSprintAsync"/>
    /// (lifecycle actions plus one <see cref="StageTransitionAssessor"/>-backed row per candidate
    /// stage) -- requires an initialized project, since there is no sprint state to derive from
    /// otherwise. Without one, wraps the same project-level <see cref="SuggestedAction"/> list the
    /// project overview already shows (ADR 0043/0049: extends, never duplicates, that existing
    /// concept) -- computed the same way for an uninitialized project as for an initialized one,
    /// since <c>initialize_project</c> is itself one of those suggestions.</summary>
    public async Task<IReadOnlyList<AvailableAction>> GetAvailableActionsAsync(
        string? projectRoot, Guid? sprintId, CancellationToken cancellationToken)
    {
        if (sprintId is { } id)
        {
            ProjectRootStatus status =
                await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            return status.Initialized
                ? await availableActions.ForSprintAsync(status.Root, id, cancellationToken).ConfigureAwait(false)
                : [];
        }

        ProjectSnapshot snapshot = await GetProjectSnapshotAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return AvailableActionProjector.ForProject(snapshot.Project.Root, snapshot.SuggestedActions);
    }

    public async Task<CreateSprintResult> CreateSprintAsync(
        string? projectRoot, string? title, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        return await orchestrator
            .CreateSprintAsync(
                new(status.Root, StatusAdvisor.StateVersion(status), Guid.NewGuid(), Title: title),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SprintTransitionResult> RunSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        SprintSnapshot? sprint =
            await orchestrator.GetSprintAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (sprint is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        return await orchestrator
            .RunSprintAsync(
                new(status.Root, id, sprint.Version, SprintOrchestrator.RunSprintKey(sprint)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SprintTransitionResult> ResumeSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintId id = new(sprintId);
        SprintSnapshot? sprint =
            await orchestrator.GetSprintAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (sprint is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        return await orchestrator
            .ResumeSprintAsync(
                new(status.Root, id, sprint.Version, SprintOrchestrator.ResumeSprintKey(sprint)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SprintTransitionResult> CancelSprintAsync(
        string? projectRoot, Guid sprintId, bool confirmed, CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        bool effectiveConfirmed =
            confirmed || !await RequiresConfirmationAsync(cancellationToken).ConfigureAwait(false);
        if (!effectiveConfirmed)
        {
            return new(false, null, DiagnosticCodes.ConfirmationRequired);
        }

        SprintId id = new(sprintId);
        SprintSnapshot? sprint =
            await orchestrator.GetSprintAsync(status.Root, id, cancellationToken).ConfigureAwait(false);
        if (sprint is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        return await orchestrator
            .CancelSprintAsync(
                new(status.Root, id, sprint.Version, SprintOrchestrator.CancelSprintKey(sprint)), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsRecoverable(Exception error) =>
        error is JsonException or YamlException or InvalidDataException or FormatException or
            ConfigurationMigrationException or ConfigurationScopeException or IOException or
            UnauthorizedAccessException;

    /// <summary>
    /// ADR 0008: "duplicates or an identifier with no registration invalidate configuration."
    /// Duplicate rejection is already enforced by user-config.schema.json's `uniqueItems`; an
    /// unregistered id can only be caught here, against the actual composed provider catalog,
    /// since the schema has no knowledge of which providers this Forge build ships.
    /// </summary>
    private void RequireRegisteredProviders(string key, JsonElement value)
    {
        if (key != ConfigurationKeys.ProvidersEnabled || value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string id = item.GetString() ?? string.Empty;
            if (!providerCatalog.Contains(new ProviderId(id)))
            {
                throw new InvalidDataException($"Unknown provider id '{id}'.");
            }
        }
    }
}
