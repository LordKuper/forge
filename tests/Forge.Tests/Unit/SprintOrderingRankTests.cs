using Forge.Desktop.Presentation;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class SprintOrderingRankTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SprintState.AwaitingHuman, 0)]
    [InlineData(SprintState.ReadyToFinalize, 0)]
    [InlineData(SprintState.Running, 1)]
    [InlineData(SprintState.Paused, 2)]
    [InlineData(SprintState.Blocked, 3)]
    [InlineData(SprintState.Failed, 3)]
    [InlineData(SprintState.Draft, 4)]
    [InlineData(SprintState.Ready, 4)]
    public void RankMatchesPlanSection41Buckets(SprintState state, int expectedRank) =>
        Assert.Equal(expectedRank, SprintOrderingRank.Rank(state));

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SprintState.AwaitingHuman, true)]
    [InlineData(SprintState.ReadyToFinalize, true)]
    [InlineData(SprintState.Blocked, false)]
    [InlineData(SprintState.Failed, false)]
    [InlineData(SprintState.Running, false)]
    public void RequiresHumanAttentionIsTheRankZeroBucketOnly(SprintState state, bool expected) =>
        Assert.Equal(expected, SprintOrderingRank.RequiresHumanAttention(state));

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderBySidebarRuleOrdersByBucketThenDescendingCreationSequence()
    {
        (SprintState State, int Sequence)[] sprints =
        [
            (SprintState.Running, 1),
            (SprintState.AwaitingHuman, 2),
            (SprintState.Blocked, 5),
            (SprintState.Paused, 3),
            (SprintState.Running, 4),
            (SprintState.Failed, 6),
        ];

        (SprintState State, int Sequence)[] ordered =
            [.. sprints.OrderBySidebarRule(item => item.State, item => item.Sequence)];

        Assert.Equal(
        [
            (SprintState.AwaitingHuman, 2),
            (SprintState.Running, 4),
            (SprintState.Running, 1),
            (SprintState.Paused, 3),
            (SprintState.Failed, 6),
            (SprintState.Blocked, 5),
        ],
            ordered);
    }
}
