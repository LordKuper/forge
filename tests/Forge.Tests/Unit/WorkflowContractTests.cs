using Forge.Domain;

namespace Forge.UnitTests;

public sealed class WorkflowContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void SprintStatesMatchFrozenV1Contract()
    {
        string[] states = Enum
            .GetValues<SprintState>()
            .Select(ToSnakeCase)
            .ToArray();

        Assert.Equal(
            [
                "draft",
                "ready",
                "running",
                "awaiting_human",
                "blocked",
                "failed",
                "ready_to_finalize",
                "completed",
                "cancelled",
            ],
            states);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void NodeStatesMatchFrozenV1Contract()
    {
        Assert.Equal(
            [
                "pending",
                "ready",
                "running",
                "awaiting_human",
                "succeeded",
                "failed",
                "skipped",
                "cancelled",
            ],
            Enum.GetValues<NodeState>().Select(WorkflowStateNames.ToSnakeCase));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void AttemptStatesMatchFrozenV1Contract()
    {
        Assert.Equal(
            [
                "created",
                "preparing",
                "running",
                "validating",
                "succeeded",
                "failed",
                "abandoned",
                "cancelled",
            ],
            Enum.GetValues<AttemptState>().Select(WorkflowStateNames.ToSnakeCase));
    }

    [Theory]
    [Trait("Category", "Contract")]
    [InlineData(SprintState.Draft, SprintState.Ready, true)]
    [InlineData(SprintState.Draft, SprintState.Running, false)]
    [InlineData(SprintState.Ready, SprintState.Running, true)]
    [InlineData(SprintState.Running, SprintState.ReadyToFinalize, true)]
    [InlineData(SprintState.ReadyToFinalize, SprintState.Completed, true)]
    [InlineData(SprintState.Completed, SprintState.Draft, false)]
    [InlineData(SprintState.Cancelled, SprintState.Ready, false)]
    public void SprintTransitionsMatchFrozenV1Contract(SprintState from, SprintState to, bool expected) =>
        Assert.Equal(expected, WorkflowStateMachines.CanTransition(from, to));

    [Theory]
    [Trait("Category", "Contract")]
    [InlineData(NodeState.Pending, NodeState.Ready, true)]
    [InlineData(NodeState.Running, NodeState.Succeeded, true)]
    [InlineData(NodeState.Failed, NodeState.Ready, true)]
    [InlineData(NodeState.Succeeded, NodeState.Ready, false)]
    public void NodeTransitionsMatchFrozenV1Contract(NodeState from, NodeState to, bool expected) =>
        Assert.Equal(expected, WorkflowStateMachines.CanTransition(from, to));

    [Theory]
    [Trait("Category", "Contract")]
    [InlineData(AttemptState.Created, AttemptState.Preparing, true)]
    [InlineData(AttemptState.Validating, AttemptState.Succeeded, true)]
    [InlineData(AttemptState.Failed, AttemptState.Abandoned, true)]
    [InlineData(AttemptState.Succeeded, AttemptState.Failed, false)]
    public void AttemptTransitionsMatchFrozenV1Contract(AttemptState from, AttemptState to, bool expected) =>
        Assert.Equal(expected, WorkflowStateMachines.CanTransition(from, to));

    private static string ToSnakeCase(SprintState state) => WorkflowStateNames.ToSnakeCase(state);
}
