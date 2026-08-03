namespace Forge.Configuration;

public sealed class ConfigurationStoreFactory(IConfigurationRegistry registry)
{
    public IConfigurationStore CreateUserStore(string localApplicationData) =>
        new JsonConfigurationStore(
            Path.Combine(localApplicationData, "Forge", "config.json"),
            ConfigurationScope.User,
            registry);

    public IConfigurationStore CreateProjectStore(string projectRoot) =>
        new YamlConfigurationStore(
            Path.Combine(projectRoot, ".forge", "manifest.yaml"),
            ConfigurationScope.Project,
            registry);
}
