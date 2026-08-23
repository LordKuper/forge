using Forge.Domain;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 4.1's deterministic sidebar/overview sprint ordering: "human attention, running,
/// paused, blocked or failed, then other non-terminal sprints by descending creation sequence."
/// <c>SprintWorkspaceSummary.AttentionRequired</c> (the backend projection) intentionally
/// folds <c>AwaitingHuman</c>/<c>Blocked</c>/<c>Failed</c>/<c>ReadyToFinalize</c> into one boolean
/// (ADR 0049: it is a coarse "needs a human to look at this" signal for notifications), which is too
/// coarse for this ordering rule -- it would place a merely-blocked sprint in the same bucket as one
/// awaiting an actual human decision. This ranks directly off the sprint's own <see cref="SprintState"/>
/// instead, kept as one shared, independently tested rule so the sidebar and the project overview
/// can never silently order the same sprint list two different ways.
/// </summary>
public static class SprintOrderingRank
{
    /// <summary>Lower sorts first. A sprint in a bucket lower than 4 is still non-terminal --
    /// <see cref="WorkflowStateMachines.IsTerminal(SprintState)"/> excludes it before this is ever
    /// consulted.</summary>
    public static int Rank(SprintState state) => state switch
    {
        SprintState.AwaitingHuman or SprintState.ReadyToFinalize => 0,
        SprintState.Running => 1,
        SprintState.Paused => 2,
        SprintState.Blocked or SprintState.Failed => 3,
        _ => 4,
    };

    /// <summary>Human-attention states only (the rank-0 bucket) -- distinct from
    /// <c>SprintWorkspaceSummary.AttentionRequired</c>, which also covers rank 3.</summary>
    public static bool RequiresHumanAttention(SprintState state) => Rank(state) == 0;

    public static IOrderedEnumerable<T> OrderBySidebarRule<T>(
        this IEnumerable<T> sprints, Func<T, SprintState> state, Func<T, int> creationSequence) =>
        sprints
            .OrderBy(item => Rank(state(item)))
            .ThenByDescending(creationSequence);
}
