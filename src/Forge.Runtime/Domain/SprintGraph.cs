using System.Text.RegularExpressions;

namespace Forge.Domain;

/// <summary>
/// A `Work` node runs through an (abstracted, not-yet-implemented) executor. A `HumanGate` node
/// carries no work of its own — as soon as its dependencies are satisfied, the scheduler pushes it
/// straight to `awaiting_human` for an explicit approve/reject decision.
/// </summary>
public enum NodeKind
{
    Work,
    HumanGate,
}

/// <summary>
/// The workflow-level part a <see cref="NodeKind.Work"/> node plays, distinct from its mechanical
/// <see cref="NodeKind"/>. <see cref="TestWork"/> is the one role the scheduler treats specially
/// (see <see cref="SprintGraphValidator"/>'s caller, <c>SprintScheduler.AdvanceGraphAsync</c>): it
/// never becomes `ready` on dependency completion alone, only once every dependency with role
/// <see cref="Confirmation"/> has a recorded, `Confirmed` <c>Forge.Domain.ConfirmationArtifact</c>.
/// Every other role is descriptive only today — a behavior tag for the built-in
/// `implementation-critical` graph (see <c>Forge.Compiler.ImplementationCriticalGraphBuilder</c>),
/// not a distinct code path, matching the plan's "behavior nodes and rubric data, not a seven-role
/// catalog."
/// </summary>
public enum NodeRole
{
    Generic,
    Intake,
    Planning,
    Implementation,
    Confirmation,
    TestWork,
    Review,
    HumanApproval,
    Finalization,
}

/// <summary>
/// One node in a sprint's frozen graph. <see cref="Id"/> is the stable, workflow-assigned string
/// identity (see <see cref="NodeId"/>); <see cref="DependsOn"/> names other nodes in the same
/// graph that must reach `succeeded` or `skipped` before this one can become `ready`.
/// </summary>
public sealed record NodeDefinition(
    string Id,
    NodeKind Kind,
    IReadOnlyList<string> DependsOn,
    NodeRole Role = NodeRole.Generic);

/// <summary>
/// Validates a graph before it is ever frozen into a sprint: every dependency must name a node
/// that exists in the same graph, node identities must be unique, and the graph must be acyclic —
/// otherwise no deterministic execution order exists.
/// </summary>
public static class SprintGraphValidator
{
    // Node ids become filenames (results, handoffs) and event-log aggregate ids, so they are
    // constrained to a safe, predictable alphabet rather than trusted as arbitrary strings — the
    // schema itself only requires non-empty (node-result.schema.json: minLength 1).
    private static readonly Regex NodeIdPattern = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>The same alphabet gate <see cref="IsValid"/> applies to every node in a graph,
    /// exposed standalone for a caller (<c>FileSprintEventLog.ReviewFloorPinPath</c>) that
    /// interpolates one already-frozen node id into a filename and needs to re-check it directly —
    /// the character set itself is what rules out a path-traversal payload (no `/`, `\`, or `..` can
    /// ever match), which a purely lexical containment check on the resulting path cannot fully
    /// guarantee on its own if the containing directory were ever a symlink.</summary>
    public static bool IsValidNodeId(string id) => NodeIdPattern.IsMatch(id);

    public static bool IsValid(IReadOnlyList<NodeDefinition> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Any(node => !IsValidNodeId(node.Id)))
        {
            return false;
        }

        HashSet<string> ids = new(graph.Select(node => node.Id), StringComparer.Ordinal);
        if (ids.Count != graph.Count)
        {
            return false;
        }

        if (graph.Any(node => node.DependsOn.Any(dependency => !ids.Contains(dependency))))
        {
            return false;
        }

        return !HasCycle(graph);
    }

    private static bool HasCycle(IReadOnlyList<NodeDefinition> graph)
    {
        Dictionary<string, NodeDefinition> byId = graph.ToDictionary(
            node => node.Id,
            node => node,
            StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        foreach (NodeDefinition node in graph)
        {
            if (Visit(node.Id, byId, visited, visiting))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Visit(
        string id,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited,
        HashSet<string> visiting)
    {
        if (visited.Contains(id))
        {
            return false;
        }

        if (!visiting.Add(id))
        {
            return true;
        }

        foreach (string dependency in byId[id].DependsOn)
        {
            if (Visit(dependency, byId, visited, visiting))
            {
                return true;
            }
        }

        visiting.Remove(id);
        visited.Add(id);
        return false;
    }
}
