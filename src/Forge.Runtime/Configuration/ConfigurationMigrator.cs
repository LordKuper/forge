namespace Forge.Configuration;

public sealed class ConfigurationMigrator(IEnumerable<IConfigurationMigration> migrations)
{
    private readonly IReadOnlyList<IConfigurationMigration> migrations = migrations
        .OrderBy(item => item.FromVersion)
        .ToArray();

    public ConfigurationDocument Migrate(
        ConfigurationDocument document,
        ConfigurationScope scope,
        int targetVersion)
    {
        ConfigurationDocument current = document;
        while (current.SchemaVersion < targetVersion)
        {
            IConfigurationMigration migration = migrations.SingleOrDefault(
                item => item.Scope == scope && item.FromVersion == current.SchemaVersion)
                ?? throw new InvalidOperationException(
                    $"Missing {scope} migration from schema {current.SchemaVersion}.");
            current = migration.Apply(current);
            if (current.SchemaVersion != migration.ToVersion)
            {
                throw new InvalidOperationException("Migration returned an unexpected schema version.");
            }
        }

        return current.SchemaVersion == targetVersion
            ? current
            : throw new InvalidOperationException("Configuration schema is newer than supported.");
    }
}
