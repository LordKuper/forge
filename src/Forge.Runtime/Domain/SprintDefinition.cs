namespace Forge.Domain;

public enum SprintDependencyKind
{
    Commit,
    Artifact,
}

/// <summary>
/// A reference to something outside the sprint's own history. A <see cref="SprintDependencyKind.Commit"/>
/// is immutable by construction. A <see cref="SprintDependencyKind.Artifact"/> must be a
/// content-addressed, published digest; when it names the sprint that produced it through
/// <see cref="SourceSprintId"/>, that sprint must have reached a terminal state, since only a
/// finished sprint's output is considered published rather than still-mutable in-progress state.
/// </summary>
public sealed record SprintDependency(SprintDependencyKind Kind, string Reference, SprintId? SourceSprintId = null);

/// <summary>
/// Everything a sprint freezes once at creation and never changes again: the commit it branches
/// from, the workflow it runs, a snapshot of the project configuration that governed it, and its
/// declared dependencies. Node/attempt/finding/artifact state stays namespaced under the sprint's
/// own directory and is never shared with another sprint's mutable state.
/// </summary>
public sealed record SprintDefinition(
    SprintId Id,
    string BaseCommit,
    string Workflow,
    string WorkflowVersion,
    IReadOnlyDictionary<string, string> ConfigurationSnapshot,
    IReadOnlyList<SprintDependency> Dependencies,
    DateTimeOffset FrozenAt);
