using System.Text.Json;
using Forge.Configuration;

namespace Forge.Application;

/// <summary>Owns the location of the user and project configuration stores.</summary>
public sealed class ScopedConfigurationStores(
    ConfigurationStoreFactory factory,
    IEnvironmentPaths environment)
{
    public const int SchemaVersion = 1;

    public IConfigurationStore User { get; } =
        factory.CreateUserStore(environment.LocalApplicationData);

    public IConfigurationStore Project(string projectRoot) => factory.CreateProjectStore(projectRoot);
}

/// <summary>
/// Reads and writes scoped configuration with provenance. Keys are rejected in the wrong scope
/// and project values are never written outside an initialized project root.
/// </summary>
public sealed class ScopedConfigurationService(
    IConfigurationRegistry registry,
    ConfigurationResolver resolver,
    ConfigurationMigrator migrator,
    ScopedConfigurationStores stores)
{
    public async Task<IReadOnlyList<EffectiveConfigurationValue>> GetUserAsync(
        IReadOnlyDictionary<string, JsonElement>? session,
        CancellationToken cancellationToken)
    {
        ConfigurationDocument document = migrator.Migrate(
            await stores.User.ReadAsync(cancellationToken).ConfigureAwait(false),
            ConfigurationScope.User,
            ScopedConfigurationStores.SchemaVersion);
        IReadOnlyDictionary<string, JsonElement> overrides =
            session ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return [.. Keys(ConfigurationScope.User)
            .Select(key => resolver.ResolveUser(key.Name, overrides, document))];
    }

    public async Task<IReadOnlyList<EffectiveConfigurationValue>> GetProjectAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        ConfigurationDocument document = migrator.Migrate(
            await stores.Project(projectRoot).ReadAsync(cancellationToken).ConfigureAwait(false),
            ConfigurationScope.Project,
            ScopedConfigurationStores.SchemaVersion);
        return [.. Keys(ConfigurationScope.Project)
            .Select(key => resolver.ResolveProject(key.Name, document))];
    }

    public async Task SetUserAsync(
        string key,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        registry.RequireScope(key, ConfigurationScope.User);
        ConfigurationDocument document =
            await stores.User.ReadAsync(cancellationToken).ConfigureAwait(false);
        await stores.User
            .WriteAsync(document with { Values = With(document.Values, key, value) }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetProjectAsync(
        string projectRoot,
        string key,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        registry.RequireScope(key, ConfigurationScope.Project);
        IConfigurationStore store = stores.Project(projectRoot);
        ConfigurationDocument document =
            await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (document.ProjectId is null)
        {
            throw new InvalidDataException(DiagnosticCodes.ProjectNotInitialized);
        }

        await store
            .WriteAsync(document with { Values = With(document.Values, key, value) }, cancellationToken)
            .ConfigureAwait(false);
    }

    private IEnumerable<ConfigurationKey> Keys(ConfigurationScope scope) =>
        registry.Keys.Where(key => key.Scope == scope).OrderBy(key => key.Name, StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> With(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        JsonElement value) =>
        new(values, StringComparer.Ordinal)
        {
            [key] = value,
        };
}
