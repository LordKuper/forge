using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

/// <summary>
/// A direct <see cref="FileSprintEventLog.SaveReviewIterationAsync"/>/<see cref="FileSprintEventLog.GetReviewIterationsAsync"/>
/// round trip with every field distinct (including a populated <see cref="CoverageLedger"/> and
/// two <see cref="NormalizedFindingKey"/> entries), matching the precedent set for
/// <c>ExecutionProfilePersistenceTests</c> — a symmetric fixture could never catch a transposed
/// mapping field.
/// </summary>
public sealed class ReviewIterationPersistenceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SavingAndLoadingAReviewIterationRoundTripsEveryFieldExactly()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        ReviewIterationRecord record = new(
            Guid.NewGuid(),
            sprintId,
            new("review"),
            ReviewDimension.Design,
            ReviewerKind.External,
            7,
            ReviewOutcome.ChangesRequested,
            [
                new("src/a.cs", 10, "rule.one", "sha256:" + new string('a', 64)),
                new(null, null, "rule.two", "sha256:" + new string('b', 64)),
            ],
            new(["a.cs", "b.cs"], ["rule_1", "rule_2"], ["a.cs"], ["rule_1"]),
            DateTimeOffset.UnixEpoch.AddDays(1));

        await log.SaveReviewIterationAsync(root.Path, record, cancellationToken);
        IReadOnlyList<ReviewIterationRecord> reloaded =
            await log.GetReviewIterationsAsync(root.Path, sprintId, cancellationToken);

        ReviewIterationRecord actual = Assert.Single(reloaded);
        Assert.Equal(record.NodeId, actual.NodeId);
        Assert.Equal(record.Dimension, actual.Dimension);
        Assert.Equal(record.ReviewerKind, actual.ReviewerKind);
        Assert.Equal(record.Iteration, actual.Iteration);
        Assert.Equal(record.Outcome, actual.Outcome);
        Assert.Equal(record.ExternalFindings, actual.ExternalFindings);
        Assert.Equal(record.Coverage!.ScopedFiles, actual.Coverage!.ScopedFiles);
        Assert.Equal(record.Coverage.RubricItemIds, actual.Coverage.RubricItemIds);
        Assert.Equal(record.Coverage.CoveredFiles, actual.Coverage.CoveredFiles);
        Assert.Equal(record.Coverage.CoveredRubricItemIds, actual.Coverage.CoveredRubricItemIds);
        Assert.Equal(record.RecordedAt, actual.RecordedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadingReviewIterationsForASprintWithNoneReturnsEmpty()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        IReadOnlyList<ReviewIterationRecord> reviews =
            await log.GetReviewIterationsAsync(root.Path, SprintId.New(), cancellationToken);

        Assert.Empty(reviews);
    }
}
