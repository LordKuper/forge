namespace Forge.Domain;

/// <summary>
/// Mirrors the frozen `sprint`/`node`/`attempt` machines in
/// docs/contracts/v1/state-machines.json exactly. The Stage 0 contract gate
/// (`Stage0.Contracts.Tests.ps1`) validates that JSON file's own internal structural consistency
/// (every referenced state defined, no dangling transitions, terminal states have no outgoing
/// edges); `WorkflowContractTests` spot-checks representative transitions from this table against
/// the same expectations — it does not diff this table against the JSON byte-for-byte, so keep the
/// two in sync by hand when either changes.
/// </summary>
public static class WorkflowStateMachines
{
    public static readonly SprintState SprintInitial = SprintState.Draft;
    public static readonly NodeState NodeInitial = NodeState.Pending;
    public static readonly AttemptState AttemptInitial = AttemptState.Created;

    private static readonly Dictionary<SprintState, IReadOnlyList<SprintState>> Sprint =
        new Dictionary<SprintState, IReadOnlyList<SprintState>>
        {
            [SprintState.Draft] = [SprintState.Ready, SprintState.Cancelled],
            [SprintState.Ready] = [SprintState.Running, SprintState.Cancelled],
            [SprintState.Running] =
            [
                SprintState.Paused,
                SprintState.AwaitingHuman,
                SprintState.Blocked,
                SprintState.Failed,
                SprintState.ReadyToFinalize,
                SprintState.Cancelled,
            ],
            [SprintState.Paused] = [SprintState.Ready, SprintState.Cancelled],
            [SprintState.AwaitingHuman] =
                [SprintState.Running, SprintState.Blocked, SprintState.Cancelled],
            [SprintState.Blocked] = [SprintState.Ready, SprintState.Cancelled],
            [SprintState.Failed] = [SprintState.Ready, SprintState.Cancelled],
            [SprintState.ReadyToFinalize] = [SprintState.Completed, SprintState.Blocked],
            [SprintState.Completed] = [],
            [SprintState.Cancelled] = [],
        };

    private static readonly Dictionary<NodeState, IReadOnlyList<NodeState>> Node =
        new Dictionary<NodeState, IReadOnlyList<NodeState>>
        {
            [NodeState.Pending] = [NodeState.Ready, NodeState.Skipped, NodeState.Cancelled],
            [NodeState.Ready] = [NodeState.Running, NodeState.Skipped, NodeState.Cancelled],
            [NodeState.Running] =
            [
                NodeState.AwaitingHuman,
                NodeState.Succeeded,
                NodeState.Failed,
                NodeState.Cancelled,
            ],
            [NodeState.AwaitingHuman] =
                [NodeState.Running, NodeState.Failed, NodeState.Cancelled],
            [NodeState.Succeeded] = [],
            [NodeState.Failed] = [NodeState.Ready],
            [NodeState.Skipped] = [],
            [NodeState.Cancelled] = [],
        };

    private static readonly Dictionary<AttemptState, IReadOnlyList<AttemptState>> Attempt =
        new Dictionary<AttemptState, IReadOnlyList<AttemptState>>
        {
            [AttemptState.Created] = [AttemptState.Preparing, AttemptState.Cancelled],
            [AttemptState.Preparing] =
                [AttemptState.Running, AttemptState.Failed, AttemptState.Cancelled],
            [AttemptState.Running] =
                [AttemptState.Validating, AttemptState.Failed, AttemptState.Cancelled],
            [AttemptState.Validating] =
                [AttemptState.Succeeded, AttemptState.Failed, AttemptState.Cancelled],
            [AttemptState.Succeeded] = [],
            [AttemptState.Failed] = [],
            [AttemptState.Cancelled] = [],
        };

    public static bool CanTransition(SprintState from, SprintState to) => Sprint[from].Contains(to);

    public static bool CanTransition(NodeState from, NodeState to) => Node[from].Contains(to);

    public static bool CanTransition(AttemptState from, AttemptState to) => Attempt[from].Contains(to);

    /// <summary>An attempt state with no outgoing edges in the frozen machine above — derived
    /// rather than a second hardcoded state list, so it can never drift from the table it reads.</summary>
    public static bool IsTerminal(AttemptState state) => Attempt[state].Count == 0;

    /// <summary>Same derivation as <see cref="IsTerminal(AttemptState)"/>, for the sprint machine —
    /// matches <see cref="Forge.Application.StatusAdvisor"/>'s own inline `Completed`/`Cancelled`
    /// check.</summary>
    public static bool IsTerminal(SprintState state) => Sprint[state].Count == 0;
}
