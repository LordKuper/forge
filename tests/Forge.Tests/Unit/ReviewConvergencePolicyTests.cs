using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

public sealed class ReviewConvergencePolicyTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(1, FindingSeverity.Low)]
    [InlineData(2, FindingSeverity.Medium)]
    [InlineData(3, FindingSeverity.High)]
    [InlineData(4, FindingSeverity.High)]
    [InlineData(5, FindingSeverity.Critical)]
    [InlineData(14, FindingSeverity.Critical)]
    public void SeverityFloorForMatchesTheCumulativeAsdBudget(int iteration, FindingSeverity expected)
    {
        Assert.Equal(expected, ReviewConvergencePolicy.SeverityFloorFor(iteration));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SeverityFloorForRejectsAnIterationBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewConvergencePolicy.SeverityFloorFor(0));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(20, true)]
    public void RequiresConvergenceGateFiresBeforeIteration15(int iteration, bool expected)
    {
        Assert.Equal(expected, ReviewConvergencePolicy.RequiresConvergenceGate(iteration));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(FindingSeverity.Info, FindingSeverity.Low, false)]
    [InlineData(FindingSeverity.Low, FindingSeverity.Low, true)]
    [InlineData(FindingSeverity.High, FindingSeverity.Medium, true)]
    public void IsAtOrAboveFloorComparesSeverityOrdinally(FindingSeverity severity, FindingSeverity floor, bool expected)
    {
        Assert.Equal(expected, ReviewConvergencePolicy.IsAtOrAboveFloor(severity, floor));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CoverageIsCompleteOnlyWhenEveryFileAndRubricItemIsCovered()
    {
        CoverageLedger complete = new(["a.cs", "b.cs"], ["rule_1"], ["a.cs", "b.cs"], ["rule_1"]);
        CoverageLedger missingFile = new(["a.cs", "b.cs"], ["rule_1"], ["a.cs"], ["rule_1"]);
        CoverageLedger missingRubric = new(["a.cs"], ["rule_1", "rule_2"], ["a.cs"], ["rule_1"]);

        Assert.True(ReviewConvergencePolicy.IsCoverageComplete(complete));
        Assert.False(ReviewConvergencePolicy.IsCoverageComplete(missingFile));
        Assert.False(ReviewConvergencePolicy.IsCoverageComplete(missingRubric));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RepeatedFindingSetDetectionRequiresTheImmediatelyPrecedingIterationToMatch()
    {
        NormalizedFindingKey key = new("src/a.cs", 10, "message.key", "sha256:" + new string('a', 64));
        SprintId sprintId = SprintId.New();
        ReviewIterationRecord priorChangesRequested = new(
            Guid.NewGuid(), sprintId, new("review"), ReviewDimension.Implementation, ReviewerKind.External, 3,
            ReviewOutcome.ChangesRequested, [key], null, DateTimeOffset.UnixEpoch);
        ReviewIterationRecord priorApproved = priorChangesRequested with { Outcome = ReviewOutcome.Approved };

        Assert.True(ReviewConvergencePolicy.HasRepeatedExternalFindingSet([priorChangesRequested], [key]));
        Assert.False(ReviewConvergencePolicy.HasRepeatedExternalFindingSet([priorApproved], [key]));
        Assert.False(ReviewConvergencePolicy.HasRepeatedExternalFindingSet([], [key]));
        Assert.False(
            ReviewConvergencePolicy.HasRepeatedExternalFindingSet(
                [priorChangesRequested], [key with { Line = 11 }]));
    }
}
