namespace Forge.Domain;

/// <summary>
/// Mirrors the frozen `sprint`/`node`/`attempt` machines in
/// docs/contracts/v1/schemas/state-machines.json exactly. WorkflowContractTests enforces parity.
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
                SprintState.AwaitingHuman,
                SprintState.Blocked,
                SprintState.Failed,
                SprintState.ReadyToFinalize,
                SprintState.Cancelled,
            ],
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
            [AttemptState.Validating] = [AttemptState.Succeeded, AttemptState.Failed],
            [AttemptState.Succeeded] = [],
            [AttemptState.Failed] = [AttemptState.Abandoned],
            [AttemptState.Abandoned] = [],
            [AttemptState.Cancelled] = [],
        };

    public static bool CanTransition(SprintState from, SprintState to) => Sprint[from].Contains(to);

    public static bool CanTransition(NodeState from, NodeState to) => Node[from].Contains(to);

    public static bool CanTransition(AttemptState from, AttemptState to) => Attempt[from].Contains(to);
}
