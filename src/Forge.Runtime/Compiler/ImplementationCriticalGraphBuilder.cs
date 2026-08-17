using Forge.Domain;

namespace Forge.Compiler;

/// <summary>
/// Builds the frozen node graph for Forge's one built-in workflow, `implementation-critical`
/// (ADR 0001: the only enabled workflow for the MVP). Pure and deterministic — the same graph
/// every time — so <c>Forge.Application.SprintOrchestrator.CreateSprintAsync</c> can use it as the
/// default for every managed project without a caller ever having to hand-assemble one.
/// </summary>
/// <remarks>
/// Isolated implementation, confirmation, and test-work nodes with a dependency edge from
/// confirmation to test-work satisfy the plan's Stage 11 item: test-work can only ever become
/// eligible once the confirmation node it depends on has run (structurally) — and, per
/// <see cref="NodeRole.TestWork"/>'s doc comment, only once that run recorded a `Confirmed`
/// artifact (behaviorally, enforced by the scheduler, not by this graph shape alone). Intake,
/// planning, review, and finalization are named nodes today with no executor behind them yet —
/// the same "shape now, producer later" gap ADR 0009 left for structured handoffs.
/// </remarks>
public static class ImplementationCriticalGraphBuilder
{
    public const string IntakeNodeId = "intake";
    public const string PlanningNodeId = "planning";
    public const string ImplementationNodeId = "implementation";
    public const string ConfirmationNodeId = "confirmation";
    public const string TestWorkNodeId = "test_work";
    public const string ReviewNodeId = "review";
    public const string HumanApprovalNodeId = "human_approval";
    public const string FinalizationNodeId = "finalization";

    public static IReadOnlyList<NodeDefinition> Build() =>
    [
        new(IntakeNodeId, NodeKind.Work, [], NodeRole.Intake),
        new(PlanningNodeId, NodeKind.Work, [IntakeNodeId], NodeRole.Planning),
        new(ImplementationNodeId, NodeKind.Work, [PlanningNodeId], NodeRole.Implementation),
        new(ConfirmationNodeId, NodeKind.Work, [ImplementationNodeId], NodeRole.Confirmation),
        new(TestWorkNodeId, NodeKind.Work, [ConfirmationNodeId], NodeRole.TestWork),
        new(ReviewNodeId, NodeKind.Work, [TestWorkNodeId], NodeRole.Review),
        new(HumanApprovalNodeId, NodeKind.HumanGate, [ReviewNodeId], NodeRole.HumanApproval),
        new(FinalizationNodeId, NodeKind.Work, [HumanApprovalNodeId], NodeRole.Finalization),
    ];
}
