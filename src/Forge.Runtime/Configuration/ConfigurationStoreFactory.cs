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

    /// <summary>The pre-instance-isolation user configuration path every Forge version before
    /// instance scoping used, shared by every instance.</summary>
    private static string LegacyUserPath(IEnvironmentPaths paths) =>
        Path.Combine(paths.LocalApplicationData, "Forge", "config.json");

    public IConfigurationStore CreateUserStore(IEnvironmentPaths paths)
    {
        MigrateLegacyUserConfiguration(paths);
        return new JsonConfigurationStore(
            UserPath(paths),
            ConfigurationScope.User,
            registry);
    }

    /// <summary>
    /// One-time, best-effort copy of a pre-instance-isolation user configuration file into its new
    /// instance-scoped location, so an existing install's settings are not silently ignored after
    /// upgrading to instance-scoped paths (AGENTS.md: "Keep migrations and persisted formats
    /// backward-compatible..."). Copies rather than moves — never disrupts a concurrently starting
    /// instance still reading the legacy path, and never overwrites an already-migrated file, so
    /// this is safe to call on every store creation, not just once.
    /// </summary>
    private static void MigrateLegacyUserConfiguration(IEnvironmentPaths paths)
    {
        string legacyPath = LegacyUserPath(paths);
        string currentPath = UserPath(paths);
        if (!File.Exists(legacyPath) || File.Exists(currentPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
            File.Copy(legacyPath, currentPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a race with another starting instance migrating concurrently, or a
            // transient I/O failure, just leaves this instance to start with default configuration
            // — exactly as it would have before this migration existed.
        }
    }

    public IConfigurationStore CreateProjectStore(string projectRoot) =>
        new YamlConfigurationStore(ProjectPath(projectRoot), ConfigurationScope.Project, registry);
}
