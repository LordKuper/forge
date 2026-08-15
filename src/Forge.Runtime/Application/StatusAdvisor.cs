using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

/// <summary>
/// Produces the immutable versioned project snapshot — the single authoritative read model ADR
/// 0005 assigns to `GetProjectSnapshot(detail, sprint_id?)` — and the deterministic recommendation
/// list. Every sprint/node/attempt/finding fact is folded fresh from the durable journal on every
/// call, matching <see cref="FileSprintEventLog"/>'s own "no snapshot cache" stance: at MVP sprint
/// scale, re-folding is cheaper than a second cache-invalidation surface.
/// </summary>
public sealed class StatusAdvisor(IClock clock, ISprintStore store, RoutingLedger routingLedger)
{
    public const string ContractVersion = "1.2.0";

    /// <summary>Kept separate from <see cref="ContractVersion"/>: `suggested-action.schema.json`
    /// did not change when the snapshot's own contract gained provider/startup-check fields, so
    /// its `schema_version` must not follow the snapshot's version.</summary>
    private const string SuggestedActionContractVersion = "1.0.0";
    private const int MaximumResults = 5;

    /// <summary>Sprint work is fail-closed while the project is not initialized, so an
    /// uninitialized project reports no sprints rather than probing a `.forge/sprints/` directory
    /// that cannot exist yet.</summary>
    public async Task<ProjectSnapshot> CreateSnapshotAsync(
        StartupStatus startup,
        ProviderToolchainStatus providers,
        ProviderCatalog catalog,
        SnapshotDetail detail,
        Guid? sprintId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(catalog);
        long stateVersion = StateVersion(startup.Project);
        if (!startup.Project.Initialized)
        {
            return new(
                ContractVersion,
                stateVersion,
                clock.UtcNow,
                new(startup.Project.Root, startup.Project.Initialized),
                startup.State,
                null,
                [],
                [],
                Recommend(startup, stateVersion),
                startup.Checks,
                ProviderHealthProjector.Project(providers, catalog),
                detail);
        }

        IReadOnlyList<SprintJournalEntry> journal =
            await SprintJournal.LoadAllAsync(store, startup.Project.Root, cancellationToken).ConfigureAwait(false);
        List<SprintStatus> sprints = new(journal.Count);
        Dictionary<Guid, SprintWorkflowState> states = new();
        Dictionary<Guid, SprintDefinition> definitions = new();
        for (int index = 0; index < journal.Count; index++)
        {
            SprintJournalEntry entry = journal[index];
            SprintWorkflowState state = entry.Fold();
            SprintDefinition? definition = await store
                .LoadDefinitionAsync(startup.Project.Root, entry.Id, cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
            {
                // A journal with events but no definition file is not a state this store's own
                // write path can produce (SaveDefinitionAsync always lands before any transition
                // is appended) — skip defensively rather than fail the whole snapshot for it.
                continue;
            }

            states[entry.Id.Value] = state;
            definitions[entry.Id.Value] = definition;
            sprints.Add(new(entry.Id.Value, index + 1, state.Sprint.State, definition.Workflow, definition.BaseCommit));
        }

        Guid? activeSprintId = DetermineActiveSprint(sprints);
        IReadOnlyList<Guid> attention = [.. sprints.Where(NeedsAttention).Select(sprint => sprint.Id)];
        Guid? detailTarget = sprintId ?? (detail == SnapshotDetail.Full ? activeSprintId : null);
        SprintDetails? details = detailTarget is { } target && states.TryGetValue(target, out SprintWorkflowState? targetState)
            ? await BuildDetailsAsync(startup.Project.Root, target, targetState, definitions[target], cancellationToken)
                .ConfigureAwait(false)
            : null;

        return new(
            ContractVersion,
            stateVersion,
            clock.UtcNow,
            new(startup.Project.Root, startup.Project.Initialized),
            startup.State,
            activeSprintId,
            sprints,
            attention,
            Recommend(startup, stateVersion),
            startup.Checks,
            ProviderHealthProjector.Project(providers, catalog),
            detail,
            details);
    }

    /// <summary>
    /// The state version advances with every durable project mutation. Initialization is the only
    /// mutation the current stage owns, so the version distinguishes an initialized project root.
    /// </summary>
    public static long StateVersion(ProjectRootStatus project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Initialized ? 1 : 0;
    }

    /// <summary>ADR 0005: "the active sprint is an explicit selection or the only non-terminal
    /// sprint; Forge never silently chooses among multiple candidates." No explicit-selection
    /// command exists yet (deferred), so this only ever resolves the second case.</summary>
    private static Guid? DetermineActiveSprint(IReadOnlyList<SprintStatus> sprints)
    {
        List<Guid> nonTerminal = [.. sprints
            .Where(sprint => sprint.State is not (SprintState.Completed or SprintState.Cancelled))
            .Select(sprint => sprint.Id)];
        return nonTerminal.Count == 1 ? nonTerminal[0] : null;
    }

    /// <summary>Matches the overview's attention priorities (`awaiting_human`, `blocked`, `failed`,
    /// `ready_to_finalize`). `completed` is deliberately excluded: nothing yet records whether
    /// completed work has been acknowledged, and including it here would mean a completed sprint
    /// can never leave attention. Revisit once acknowledgment tracking exists.</summary>
    private static bool NeedsAttention(SprintStatus sprint) =>
        sprint.State is SprintState.AwaitingHuman or SprintState.Blocked or SprintState.Failed
            or SprintState.ReadyToFinalize;

    private async Task<SprintDetails> BuildDetailsAsync(
        string projectRoot,
        Guid sprintId,
        SprintWorkflowState state,
        SprintDefinition definition,
        CancellationToken cancellationToken)
    {
        Dictionary<string, NodeKind> nodeKinds = definition.Graph.ToDictionary(
            node => node.Id,
            node => node.Kind,
            StringComparer.Ordinal);
        List<EntityStatus> nodes = [.. state.Nodes.Values
            .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
            .Select(node => new EntityStatus(
                node.Id.Value,
                WorkflowStateNames.ToSnakeCase(node.State),
                Kind: nodeKinds.TryGetValue(node.Id.Value, out NodeKind kind)
                    ? WorkflowStateNames.ToSnakeCase(kind)
                    : null,
                UpdatedAt: node.UpdatedAt))];
        List<EntityStatus> attempts = [.. state.Attempts.Values
            .OrderBy(attempt => attempt.Id.Value)
            .Select(attempt => new EntityStatus(
                attempt.Id.Value.ToString("D", CultureInfo.InvariantCulture),
                WorkflowStateNames.ToSnakeCase(attempt.State),
                OwnerId: attempt.NodeId,
                Kind: attempt.TargetOutcome,
                UpdatedAt: attempt.UpdatedAt))];
        IReadOnlyList<Finding> findings = await store
            .GetFindingsAsync(projectRoot, new(sprintId), cancellationToken)
            .ConfigureAwait(false);
        List<EntityStatus> findingRows = [.. findings.Select(finding => new EntityStatus(
            finding.FindingId.ToString("D", CultureInfo.InvariantCulture),
            WorkflowStateNames.ToSnakeCase(finding.Status),
            Severity: WorkflowStateNames.ToSnakeCase(finding.Severity)))];
        RetryBudgetRecord budget = await routingLedger
            .GetRetryBudgetAsync(projectRoot, new(sprintId), cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? resumeNotBefore = await routingLedger
            .GetResumeNotBeforeAsync(projectRoot, new(sprintId), cancellationToken)
            .ConfigureAwait(false);

        // Gates and artifacts stay empty until Stage 11 introduces human gates and an addressable
        // artifact store. The schema permits both as always-valid empty values.
        return new(sprintId, nodes, attempts, findingRows, [], [], new(budget.Remaining, resumeNotBefore));
    }

    private static IReadOnlyList<SuggestedAction> Recommend(StartupStatus startup, long stateVersion)
    {
        List<Candidate> candidates = [];
        StartupCheck? failed = startup.Checks.FirstOrDefault(
            check => check.State == StartupCheckState.Failed);
        if (failed is not null && StartupRecovery.CanRecover(failed))
        {
            string checkId = JsonNamingPolicy.SnakeCaseLower.ConvertName(failed.Id.ToString());
            candidates.Add(new(
                "recover_startup",
                AttentionPriority.StartupBlocked,
                SafetyClass.ConfirmMutation,
                new("startup_check", checkId),
                ["startup.fail_closed", "recovery.available"],
                "RecoverStartup",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["check"] = checkId,
                    ["diagnostic_code"] = failed.DiagnosticCode,
                }));
        }

        // A failed startup leaves recovery as the only safe action, so no mutation is offered.
        if (failed is null && startup.Project is { Exists: true, Initialized: false, Unknown: false })
        {
            candidates.Add(new(
                "initialize_project",
                AttentionPriority.StartupBlocked,
                SafetyClass.ConfirmMutation,
                new("project", startup.Project.Root),
                ["project.root_confirmed", "project.forge_directory_absent"],
                "InitializeProject",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["project_root"] = startup.Project.Root,
                }));
        }

        return
        [
            .. candidates
                .OrderBy(candidate => (int)candidate.Priority)
                .ThenBy(candidate => (int)candidate.Safety)
                .ThenBy(candidate => candidate.ActionId, StringComparer.Ordinal)
                .Take(MaximumResults)
                .Select((candidate, index) => candidate.ToAction(index + 1, stateVersion)),
        ];
    }

    private sealed record Candidate(
        string ActionId,
        AttentionPriority Priority,
        SafetyClass Safety,
        ActionTarget Target,
        IReadOnlyList<string> Preconditions,
        string CommandName,
        IReadOnlyDictionary<string, string> Arguments)
    {
        public SuggestedAction ToAction(int rank, long stateVersion) =>
            new(
                SuggestedActionContractVersion,
                ActionId,
                rank,
                string.Create(CultureInfo.InvariantCulture, $"next.{ActionId}.rationale"),
                Arguments,
                Preconditions,
                Safety,
                Target,
                new(CommandName, Arguments, IdempotencyKey(ActionId, Target, stateVersion)),
                stateVersion,
                Safety == SafetyClass.Read
                    ? StaleBehavior.RefreshThenRead
                    : StaleBehavior.RejectWithoutSideEffect);
    }

    /// <summary>Derives a stable idempotency key so an unchanged snapshot repeats the same action.</summary>
    public static Guid IdempotencyKey(string actionId, ActionTarget target, long stateVersion)
    {
        ArgumentNullException.ThrowIfNull(target);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{actionId}|{target.Kind}|{target.Id}|{stateVersion}")));
        return new(hash.AsSpan(0, 16));
    }
}
