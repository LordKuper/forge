namespace Forge.Configuration;

public sealed class ConfigurationStoreFactory(IConfigurationRegistry registry)
{
    public static string UserPath(string localApplicationData) =>
        Path.Combine(localApplicationData, "Forge", "config.json");

    public static string ProjectPath(string projectRoot) =>
        Path.Combine(projectRoot, ".forge", "manifest.yaml");

    public IConfigurationStore CreateUserStore(string localApplicationData) =>
        new JsonConfigurationStore(
            UserPath(localApplicationData),
            ConfigurationScope.User,
            registry);

    public IConfigurationStore CreateProjectStore(string projectRoot) =>
        new YamlConfigurationStore(ProjectPath(projectRoot), ConfigurationScope.Project, registry);
}
