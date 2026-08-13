using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Configuration;
using Forge.Domain;

namespace Forge.Application;

public sealed record CreateSprintCommand(
    string? ProjectRoot,
    long ExpectedStateVersion,
    Guid IdempotencyKey,
    IReadOnlyList<SprintDependency>? Dependencies = null,
    IReadOnlyList<NodeDefinition>? Graph = null);

public sealed record CreateSprintResult(bool Succeeded, SprintId? SprintId, string DiagnosticCode);

public sealed record SprintTransitionCommand(
    string? ProjectRoot,
    SprintId SprintId,
    long ExpectedStateVersion,
    Guid IdempotencyKey);

public sealed record SprintTransitionResult(bool Succeeded, SprintSnapshot? Sprint, string DiagnosticCode);

/// <summary>
/// Creates and transitions sprints through the frozen <c>sprint</c> state machine. Every mutation
/// is validated, carries an expected state version and idempotency key, and is durable before
/// this method returns — matching every other mutating capability in <see cref="ForgeApplication"/>.
/// </summary>
/// <remarks>
/// This is the persistence slice of Stage 6 (durable sprint/node/attempt state, optimistic
/// concurrency, idempotency, crash recovery, resume) plus the frozen-inputs slice (base commit,
/// workflow/configuration policy, dependencies, cross-sprint isolation). Creation freezes the
/// sprint's <see cref="SprintDefinition"/> once and for good: current `HEAD`, the workflow
/// contract version, and a snapshot of the project's effective configuration, none of which are
/// re-read afterward even if the project or Git state moves on. `run` still advances the sprint
/// machine one legal hop per call (`draft` to `ready`, then `ready` to `running`) rather than
/// skipping straight to `running`, since no DAG scheduler exists yet to make a running sprint do
/// anything; CLI/Desktop wiring lands with that scheduler.
///
/// Transition commands (run/cancel/resume) derive their idempotency key from the target sprint's
/// own version, exactly like <see cref="ForgeApplication.InitializeProjectAsync"/>: there is
/// exactly one legal next action for a given sprint version. Sprint creation cannot use that
/// convention — the project's state version does not change when a sprint is created, so the same
/// derived key would forever describe "create the first sprint" and could never create a second
/// one. Callers instead supply their own opaque <see cref="CreateSprintCommand.IdempotencyKey"/>;
/// the sprint's own id is derived deterministically from the project root and that key (see
/// <see cref="DeriveSprintId"/>), so a retried create-sprint call always targets the exact same
/// sprint directory instead of needing a separate lookup ledger that could itself drift out of
/// sync with what actually landed on disk.
/// </remarks>
public sealed class SprintOrchestrator(
    ProjectRootResolver rootResolver,
    ISprintStore store,
    IConfigurationRegistry registry,
    IRepository repository,
    ScopedConfigurationService configuration,
    IClock clock,
    SprintScheduler scheduler)
{
    public const string CreateSprintAction = "create_sprint";
    public const string RunSprintAction = "run_sprint";
    public const string CancelSprintAction = "cancel_sprint";
    public const string ResumeSprintAction = "resume_sprint";

    /// <summary>The key a caller must present to run <paramref name="sprint"/> from its current state.</summary>
    public static Guid RunSprintKey(SprintSnapshot sprint) => TransitionKey(RunSprintAction, sprint);

    /// <summary>The key a caller must present to cancel <paramref name="sprint"/> from its current state.</summary>
    public static Guid CancelSprintKey(SprintSnapshot sprint) => TransitionKey(CancelSprintAction, sprint);

    /// <summary>The key a caller must present to resume <paramref name="sprint"/> from its current state.</summary>
    public static Guid ResumeSprintKey(SprintSnapshot sprint) => TransitionKey(ResumeSprintAction, sprint);

    private static Guid TransitionKey(string actionId, SprintSnapshot sprint)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        return StatusAdvisor.IdempotencyKey(
            actionId,
            new("sprint", sprint.Id.Value.ToString("D")),
            sprint.Version);
    }

    public async Task<CreateSprintResult> CreateSprintAsync(
        CreateSprintCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(command.ProjectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        if (command.ExpectedStateVersion != StatusAdvisor.StateVersion(status))
        {
            return new(false, null, DiagnosticCodes.SuggestionStale);
        }

        // The manifest's own `ProjectId` — not the root path string — anchors sprint identity: a
        // relocated project directory or a differently cased Windows path must still resolve to the
        // exact same sprint for the same idempotency key, since both name the same project.
        Guid projectId = await ProjectIdentity.ReadProjectIdAsync(status.Root, registry, cancellationToken)
            .ConfigureAwait(false);
        SprintId sprintId = DeriveSprintId(projectId, command.IdempotencyKey);
        IReadOnlyList<SprintId> existingSprints =
            await store.ListAsync(status.Root, cancellationToken).ConfigureAwait(false);
        if (existingSprints.Contains(sprintId))
        {
            // Every creation write for this exact (project, idempotency key) pair already landed and
            // was marked complete. The sprint journal is the only runtime registry.
            return new(true, sprintId, DiagnosticCodes.None);
        }

        IReadOnlyList<SprintDependency> dependencies = command.Dependencies ?? [];
        string dependencyDiagnostic = ValidateDependencies(dependencies);
        if (dependencyDiagnostic != DiagnosticCodes.None)
        {
            return new(false, null, dependencyDiagnostic);
        }

        IReadOnlyList<NodeDefinition> graph = command.Graph ?? [];
        if (!SprintGraphValidator.IsValid(graph))
        {
            return new(false, null, DiagnosticCodes.SprintGraphInvalid);
        }

        string baseCommit;
        try
        {
            baseCommit = await repository.GetHeadAsync(status.Root, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            return new(false, null, DiagnosticCodes.RepositoryHeadUnavailable);
        }

        // Every write below targets the final, deterministic id directly — never a separate staging
        // id — and every one of them is safe to repeat: the first event replays through the store's
        // own idempotency key, `InitializeGraphAsync` tolerates a node that a prior, interrupted call
        // already created, and the definition is reused rather than rebuilt if one is already durable
        // (below). `ListAsync` cannot observe any of this until `MarkSprintCreatedAsync` runs last, so
        // a crash at any point before then simply leaves an invisible, safely resumable sprint behind.
        AppendOutcome outcome = await store.AppendTransitionAsync(
            status.Root,
            sprintId,
            AggregateKind.Sprint,
            sprintId.Value.ToString("D"),
            "SprintChanged",
            "workflow.sprint_created",
            WorkflowStateNames.ToSnakeCase(WorkflowStateMachines.SprintInitial),
            0,
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            // `AppendTransitionAsync` durably appends the event before it records the idempotency
            // key (see its own remarks), so a crash in that exact window leaves this sprint's first
            // event durable at version 1 with the key unrecorded and no marker yet — a retry then
            // computes the *same* deterministic id and hits `expectedVersion 0 != 1` here, forever,
            // unless that conflict is verified against durable state and treated as the resumed case
            // rather than a hard failure.
            bool alreadyStarted = outcome.DiagnosticCode == DiagnosticCodes.WorkflowEventConflict &&
                await store.LoadAsync(status.Root, sprintId, cancellationToken).ConfigureAwait(false) is not null;
            if (!alreadyStarted)
            {
                return new(false, null, outcome.DiagnosticCode);
            }
        }

        // A resumed call reuses whatever was already frozen instead of re-deriving it from this
        // retry's own inputs: HEAD may have moved, configuration may have changed, and a caller could
        // even supply a different graph — none of that may retroactively rewrite a sprint's supposedly
        // frozen definition once `definition.json` itself is durable (not merely once the first event
        // is — a crash between the two still re-derives once, from this same retry's own inputs,
        // which is harmless: nothing could have observed the not-yet-saved definition, and the graph
        // is not initialized from it until after this whole block).
        SprintDefinition? definition =
            await store.LoadDefinitionAsync(status.Root, sprintId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            IReadOnlyDictionary<string, string> configurationSnapshot =
                await ConfigurationSnapshotAsync(status.Root, cancellationToken).ConfigureAwait(false);
            definition = new(
                sprintId,
                baseCommit,
                ProjectInitializer.WorkflowName,
                ProjectInitializer.WorkflowContractVersion,
                configurationSnapshot,
                dependencies,
                graph,
                await ConversationLanguageAsync(cancellationToken).ConfigureAwait(false),
                ArtifactPolicySnapshotHash(configurationSnapshot),
                clock.UtcNow);
            await store.SaveDefinitionAsync(status.Root, definition, cancellationToken).ConfigureAwait(false);
        }

        await scheduler.InitializeGraphAsync(status.Root, sprintId, definition.Graph, cancellationToken)
            .ConfigureAwait(false);
        await store.MarkSprintCreatedAsync(status.Root, sprintId, cancellationToken).ConfigureAwait(false);

        return new(true, sprintId, DiagnosticCodes.None);
    }

    public async Task<SprintDefinition?> GetDefinitionAsync(
        string? projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return status.Initialized
            ? await store.LoadDefinitionAsync(status.Root, sprintId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<SprintTransitionResult> RunSprintAsync(
        SprintTransitionCommand command,
        CancellationToken cancellationToken)
    {
        SprintTransitionResult result = await TransitionAsync(
                command, RunSprintAction, "workflow.sprint_advanced", RunTarget, cancellationToken)
            .ConfigureAwait(false);
        if (result is { Succeeded: true, Sprint.State: SprintState.Running })
        {
            // A node graph may open with a human gate; entering `running` is the only moment that
            // gate becomes eligible to auto-promote to `awaiting_human`, so the scheduler needs a
            // chance to react right here rather than waiting for some other call to happen along.
            ProjectRootStatus status = await rootResolver
                .ResolveAsync(command.ProjectRoot, cancellationToken)
                .ConfigureAwait(false);
            await scheduler.AdvanceGraphAsync(status.Root, command.SprintId, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    public Task<SprintTransitionResult> CancelSprintAsync(
        SprintTransitionCommand command,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            command,
            CancelSprintAction,
            "workflow.sprint_cancelled",
            _ => SprintState.Cancelled,
            cancellationToken);

    public Task<SprintTransitionResult> ResumeSprintAsync(
        SprintTransitionCommand command,
        CancellationToken cancellationToken) =>
        TransitionAsync(command, ResumeSprintAction, "workflow.sprint_resumed", ResumeTarget, cancellationToken);

    public async Task<SprintSnapshot?> GetSprintAsync(
        string? projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return null;
        }

        SprintWorkflowState? state =
            await store.LoadAsync(status.Root, sprintId, cancellationToken).ConfigureAwait(false);
        return state?.Sprint;
    }

    private static SprintState RunTarget(SprintState current) =>
        current switch
        {
            SprintState.Draft => SprintState.Ready,
            SprintState.Ready => SprintState.Running,
            _ => current,
        };

    private static SprintState ResumeTarget(SprintState current) =>
        current switch
        {
            SprintState.AwaitingHuman => SprintState.Running,
            SprintState.Blocked or SprintState.Failed => SprintState.Ready,
            _ => current,
        };

    private async Task<SprintTransitionResult> TransitionAsync(
        SprintTransitionCommand command,
        string actionId,
        string messageKey,
        Func<SprintState, SprintState> resolveTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(command.ProjectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new(false, null, status.DiagnosticCode);
        }

        SprintWorkflowState? state = await store
            .LoadAsync(status.Root, command.SprintId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            return new(false, null, DiagnosticCodes.SprintNotFound);
        }

        if (!IsFresh(command.ExpectedStateVersion, command.IdempotencyKey, actionId,
                new("sprint", command.SprintId.Value.ToString("D")), state.Sprint.Version))
        {
            return new(false, state.Sprint, DiagnosticCodes.SuggestionStale);
        }

        SprintState target = resolveTarget(state.Sprint.State);
        if (target == state.Sprint.State || !WorkflowStateMachines.CanTransition(state.Sprint.State, target))
        {
            return new(false, state.Sprint, DiagnosticCodes.SprintTransitionInvalid);
        }

        AppendOutcome outcome = await store.AppendTransitionAsync(
            status.Root,
            command.SprintId,
            AggregateKind.Sprint,
            command.SprintId.Value.ToString("D"),
            "SprintChanged",
            messageKey,
            WorkflowStateNames.ToSnakeCase(target),
            state.Sprint.Version,
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded
            ? new(true, outcome.State!.Sprint, DiagnosticCodes.None)
            : new(false, state.Sprint, outcome.DiagnosticCode);
    }

    // Node ids become event-log aggregate ids; commit/artifact references become part of a frozen,
    // cross-sprint-trusted definition, so both are constrained to a canonical, unambiguous alphabet
    // rather than trusted as arbitrary strings.
    // `\z` (absolute end), not `$`, which in .NET also matches immediately before a trailing '\n' —
    // "aaa…a\n" must not validate as a canonical id.
    private static readonly Regex CommitIdPattern = new(@"\A[0-9a-f]{40}\z|\A[0-9a-f]{64}\z", RegexOptions.Compiled);
    private static readonly Regex ArtifactDigestPattern = new(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.Compiled);

    /// <summary>
    /// A commit dependency must be a canonical, immutable object id — never a mutable ref such as a
    /// branch name, and never abbreviated. An artifact dependency must be a full, canonical digest.
    /// An artifact dependency that also names its source sprint claims that sprint published the
    /// exact digest; that can only be trusted once a durable, cross-sprint publication record
    /// exists, and none does yet (Stage 6 does not produce artifacts), so it is rejected outright —
    /// checking the source sprint's own state first would not change that outcome, only leak whether
    /// it exists or has completed. An artifact dependency with no source sprint is trusted as an
    /// already-published, content-addressed digest — there is no Forge-tracked sprint to check.
    /// </summary>
    private static string ValidateDependencies(IReadOnlyList<SprintDependency> dependencies)
    {
        foreach (SprintDependency dependency in dependencies)
        {
            if (dependency.Kind == SprintDependencyKind.Commit)
            {
                if (!CommitIdPattern.IsMatch(dependency.Reference))
                {
                    return DiagnosticCodes.SprintDependencyInvalid;
                }

                continue;
            }

            if (!ArtifactDigestPattern.IsMatch(dependency.Reference))
            {
                return DiagnosticCodes.SprintDependencyInvalid;
            }

            if (dependency.SourceSprintId is not null)
            {
                return DiagnosticCodes.SprintDependencyNotPublished;
            }
        }

        return DiagnosticCodes.None;
    }

    private static SprintId DeriveSprintId(Guid projectId, Guid idempotencyKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"sprint|{projectId:D}|{idempotencyKey:D}"));
        return new(new Guid(hash.AsSpan(0, 16)));
    }

    private async Task<IReadOnlyDictionary<string, string>> ConfigurationSnapshotAsync(
        string root,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<EffectiveConfigurationValue> values =
            await configuration.GetProjectAsync(root, cancellationToken).ConfigureAwait(false);
        return values.ToDictionary(value => value.Key, value => value.Value.GetRawText(), StringComparer.Ordinal);
    }

    /// <summary>The language a provider is spoken to in, resolved and frozen independently of any
    /// project artifact language.</summary>
    private async Task<string> ConversationLanguageAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<EffectiveConfigurationValue> values =
            await configuration.GetUserAsync(null, cancellationToken).ConfigureAwait(false);
        return values.First(value => value.Key == "language.llm").Value.GetString() ?? "en";
    }

    /// <summary>
    /// A stable digest over the project's frozen artifact-language policy, so a generated
    /// artifact's metadata can name exactly which policy snapshot governed it (Stage 9+); nothing
    /// here produces an artifact yet.
    /// </summary>
    private static string ArtifactPolicySnapshotHash(IReadOnlyDictionary<string, string> configurationSnapshot)
    {
        string userFacing = configurationSnapshot.GetValueOrDefault("artifacts.language.user_facing", "\"en\"");
        string agentFacing = configurationSnapshot.GetValueOrDefault("artifacts.language.agent_facing", "\"en\"");
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"artifacts.language.user_facing={userFacing}|artifacts.language.agent_facing={agentFacing}"));
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    private static bool IsFresh(
        long expectedStateVersion,
        Guid idempotencyKey,
        string actionId,
        ActionTarget target,
        long stateVersion) =>
        expectedStateVersion == stateVersion &&
        idempotencyKey == StatusAdvisor.IdempotencyKey(actionId, target, stateVersion);
}
