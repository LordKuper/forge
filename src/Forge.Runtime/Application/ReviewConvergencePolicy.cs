using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// The pure decision rules ADR 0006's "ASD severity-floor policy" section specifies: the
/// consecutive-then-cumulative severity floor progression, when the iteration-limit human gate
/// applies, and repeated-normalized-external-finding-set detection. No I/O, no clock — every
/// method is a function of already-known counts and sets, so <c>SprintScheduler</c> can call these
/// synchronously around its own durable state.
/// </summary>
public static class ReviewConvergencePolicy
{
    // ADR 0006: "Default consecutive budgets are low 1, medium 1, high 2, and critical 10. Their
    // cumulative range yields floors low on iteration 1, medium on iteration 2, high on
    // iterations 3-4, critical on iterations 5-14, and an iteration-limit human gate before
    // iteration 15."
    private const int LowBudget = 1;
    private const int MediumBudget = 1;
    private const int HighBudget = 2;
    private const int CriticalBudget = 10;

    /// <summary>The lowest severity a finding must reach to stay `Open` (rather than recorded as
    /// dropped) on <paramref name="iteration"/>, for an iteration count already within budget —
    /// see <see cref="RequiresConvergenceGate"/> for the point past which this no longer applies
    /// without a human decision.</summary>
    public static FindingSeverity SeverityFloorFor(int iteration)
    {
        if (iteration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iteration), iteration, "Iteration must be at least 1.");
        }

        if (iteration <= LowBudget)
        {
            return FindingSeverity.Low;
        }

        if (iteration <= LowBudget + MediumBudget)
        {
            return FindingSeverity.Medium;
        }

        if (iteration <= LowBudget + MediumBudget + HighBudget)
        {
            return FindingSeverity.High;
        }

        return FindingSeverity.Critical;
    }

    /// <summary>True once <paramref name="iteration"/> would exceed the cumulative critical
    /// budget (iteration 15 and beyond) — the point ADR 0006 requires "an iteration-limit human
    /// gate before iteration 15" rather than a further, ever-rising floor.</summary>
    public static bool RequiresConvergenceGate(int iteration) =>
        iteration > LowBudget + MediumBudget + HighBudget + CriticalBudget;

    /// <summary>True when <paramref name="severity"/> meets or exceeds <paramref name="floor"/> —
    /// <see cref="FindingSeverity"/>'s declaration order is already severity order (`Info &lt; Low
    /// &lt; Medium &lt; High &lt; Critical`), so this is a plain ordinal comparison.</summary>
    public static bool IsAtOrAboveFloor(FindingSeverity severity, FindingSeverity floor) => severity >= floor;

    /// <summary>ADR 0006: "An incomplete ledger invalidates that verdict" — every scoped file and
    /// every applicable rubric item must be covered.</summary>
    public static bool IsCoverageComplete(CoverageLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.ScopedFiles.All(file => ledger.CoveredFiles.Contains(file, StringComparer.Ordinal)) &&
            ledger.RubricItemIds.All(item => ledger.CoveredRubricItemIds.Contains(item, StringComparer.Ordinal));
    }

    /// <summary>
    /// ADR 0006: "Two consecutive identical [normalized finding] sets by file, location, rule, and
    /// message fingerprint create a review-convergence human gate." <paramref name="history"/> is
    /// every prior <see cref="ReviewerKind.External"/> record for one dimension, oldest first;
    /// only the immediately preceding record is compared against <paramref name="current"/> — a
    /// set that repeats after an *approved* iteration in between does not count as consecutive.
    /// </summary>
    public static bool HasRepeatedExternalFindingSet(
        IReadOnlyList<ReviewIterationRecord> history,
        IReadOnlyList<NormalizedFindingKey> current)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(current);
        ReviewIterationRecord? previous = history.Count > 0 ? history[^1] : null;
        if (previous is null || previous.Outcome != ReviewOutcome.ChangesRequested)
        {
            return false;
        }

        return new HashSet<NormalizedFindingKey>(previous.ExternalFindings)
            .SetEquals(current);
    }
}
