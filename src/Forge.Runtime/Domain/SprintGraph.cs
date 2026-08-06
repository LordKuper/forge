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
/// One node in a sprint's frozen graph. <see cref="Id"/> is the stable, workflow-assigned string
/// identity (see <see cref="NodeId"/>); <see cref="DependsOn"/> names other nodes in the same
/// graph that must reach `succeeded` or `skipped` before this one can become `ready`.
/// </summary>
public sealed record NodeDefinition(string Id, NodeKind Kind, IReadOnlyList<string> DependsOn);

/// <summary>
/// Validates a graph before it is ever frozen into a sprint: every dependency must name a node
/// that exists in the same graph, node identities must be unique, and the graph must be acyclic —
/// otherwise no deterministic execution order exists.
/// </summary>
public static class SprintGraphValidator
{
    public static bool IsValid(IReadOnlyList<NodeDefinition> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
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
