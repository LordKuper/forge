using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;

namespace Forge.Application;

public sealed record CreateSprintCommand(string? ProjectRoot, long ExpectedStateVersion, Guid IdempotencyKey);

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
/// concurrency, idempotency, crash recovery, resume). It intentionally advances the sprint
/// machine one legal hop per call (e.g. `draft` to `ready`, then `ready` to `running`) rather than
/// skipping straight to `running`: the `ready` transition is where a later slice freezes the
/// sprint's base commit, inputs, and policy, and no DAG scheduler exists yet to make a running
/// sprint do anything. CLI/Desktop wiring lands with that scheduler.
///
/// Transition commands (run/cancel/resume) derive their idempotency key from the target sprint's
/// own version, exactly like <see cref="ForgeApplication.InitializeProjectAsync"/>: there is
/// exactly one legal next action for a given sprint version. Sprint creation cannot use that
/// convention — the project's state version does not change when a sprint is created, so the same
/// derived key would forever describe "create the first sprint" and could never create a second
/// one. Callers instead supply their own opaque <see cref="CreateSprintCommand.IdempotencyKey"/>,
/// recorded in a project-level ledger so a retried create-sprint call safely returns the sprint it
/// already created instead of creating a duplicate.
/// </remarks>
public sealed class SprintOrchestrator(
    ProjectRootResolver rootResolver,
    ISprintStore store,
    IConfigurationRegistry registry)
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

        Guid? alreadyCreated = await FindCreatedSprintAsync(status.Root, command.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyCreated is { } reused)
        {
            return new(true, new SprintId(reused), DiagnosticCodes.None);
        }

        SprintId sprintId = SprintId.New();
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
            return new(false, null, outcome.DiagnosticCode);
        }

        await RegisterSprintAsync(status.Root, sprintId, cancellationToken).ConfigureAwait(false);
        await RecordCreatedSprintAsync(status.Root, command.IdempotencyKey, sprintId.Value, cancellationToken)
            .ConfigureAwait(false);
        return new(true, sprintId, DiagnosticCodes.None);
    }

    public Task<SprintTransitionResult> RunSprintAsync(
        SprintTransitionCommand command,
        CancellationToken cancellationToken) =>
        TransitionAsync(command, RunSprintAction, "workflow.sprint_advanced", RunTarget, cancellationToken);

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

    private async Task RegisterSprintAsync(string root, SprintId sprintId, CancellationToken cancellationToken)
    {
        YamlConfigurationStore manifestStore =
            new(ProjectRootResolver.ManifestPath(root), ConfigurationScope.Project, registry);
        ConfigurationDocument document = await manifestStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> sprints = [.. document.Sprints ?? [], sprintId.Value];
        await manifestStore
            .WriteAsync(document with { Sprints = sprints }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreationLedgerPath(string root) =>
        Path.Combine(ProjectRootResolver.ForgeDirectory(root), "sprints", "created.json");

    private static async Task<Guid?> FindCreatedSprintAsync(
        string root,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, Guid> ledger = await ReadCreationLedgerAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return ledger.TryGetValue(idempotencyKey, out Guid sprintId) ? sprintId : null;
    }

    private static async Task RecordCreatedSprintAsync(
        string root,
        Guid idempotencyKey,
        Guid sprintId,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, Guid> ledger = await ReadCreationLedgerAsync(root, cancellationToken)
            .ConfigureAwait(false);
        ledger[idempotencyKey] = sprintId;
        await AtomicConfigurationFile
            .WriteAsync(CreationLedgerPath(root), JsonSerializer.SerializeToUtf8Bytes(ledger), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<Guid, Guid>> ReadCreationLedgerAsync(
        string root,
        CancellationToken cancellationToken)
    {
        string path = CreationLedgerPath(root);
        if (!File.Exists(path))
        {
            return new();
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(bytes) ?? new();
        }
        catch (JsonException)
        {
            // An unreadable ledger degrades to "nothing created yet": at worst a retried create
            // call produces one extra sprint instead of silently losing the request.
            return new();
        }
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
