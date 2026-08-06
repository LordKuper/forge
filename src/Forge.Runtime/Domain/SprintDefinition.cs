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
/// <summary>
/// A sprint freezes two language signals separately, and neither is re-read once the sprint
/// exists: <see cref="SprintDefinition.ConversationLanguage"/> (from user-scope `language.llm`) is
/// what a provider is spoken to in; the project-scope `artifacts.language.user_facing`/
/// `agent_facing` keys already captured in <see cref="SprintDefinition.ConfigurationSnapshot"/>
/// govern the language of what a node produces. Conflating the two would mean a user's personal
/// interaction language could leak into a project's committed, shared-language artifacts.
/// </summary>
public sealed record SprintDefinition(
    SprintId Id,
    string BaseCommit,
    string Workflow,
    string WorkflowVersion,
    IReadOnlyDictionary<string, string> ConfigurationSnapshot,
    IReadOnlyList<SprintDependency> Dependencies,
    IReadOnlyList<NodeDefinition> Graph,
    string ConversationLanguage,
    string ArtifactPolicySnapshotHash,
    DateTimeOffset FrozenAt);
