using Forge.Application;

namespace Forge.Configuration;

public sealed class ConfigurationStoreFactory(IConfigurationRegistry registry)
{
    /// <summary>Namespaced by <see cref="IEnvironmentPaths.InstanceId"/> so release, Debug, and test
    /// instances never share one user configuration file.</summary>
    public static string UserPath(IEnvironmentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.LocalApplicationData, "Forge", paths.InstanceId, "config.json");
    }

    public static string ProjectPath(string projectRoot) =>
        Path.Combine(projectRoot, ".forge", "manifest.yaml");

    public IConfigurationStore CreateUserStore(IEnvironmentPaths paths) =>
        new JsonConfigurationStore(
            UserPath(paths),
            ConfigurationScope.User,
            registry);

    public IConfigurationStore CreateProjectStore(string projectRoot) =>
        new YamlConfigurationStore(ProjectPath(projectRoot), ConfigurationScope.Project, registry);
}
