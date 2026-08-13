using Forge.Configuration;

namespace Forge.Application;

/// <summary>Reads a project's stable identity, shared by every capability that must anchor on it.</summary>
public static class ProjectIdentity
{
    /// <summary>
    /// The manifest's own <c>ProjectId</c>, not the resolved root path string: a project can move on
    /// disk, and Windows paths can differ only in case for the same physical directory, but its
    /// identity must not.
    /// </summary>
    public static async Task<Guid> ReadProjectIdAsync(
        string root,
        IConfigurationRegistry registry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(registry);
        YamlConfigurationStore manifestStore =
            new(ProjectRootResolver.ManifestPath(root), ConfigurationScope.Project, registry);
        ConfigurationDocument document = await manifestStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return document.ProjectId ??
            throw new InvalidOperationException("An initialized project must have a project ID.");
    }
}
