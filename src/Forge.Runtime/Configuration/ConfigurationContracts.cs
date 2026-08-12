using System.Text.Json;

namespace Forge.Configuration;

public enum ConfigurationScope
{
    User,
    Project,
}

public enum ConfigurationProvenance
{
    Session,
    User,
    Project,
    Inherited,
    BuiltInDefault,
}

public sealed record ConfigurationKey(
    string Name,
    ConfigurationScope Scope,
    JsonElement? DefaultValue,
    string? Inherits,
    bool AllowsSessionOverride,
    bool Sensitive);

public sealed record EffectiveConfigurationValue(
    string Key,
    JsonElement Value,
    ConfigurationProvenance Provenance);

public sealed class ConfigurationScopeException(string key, ConfigurationScope requested)
    : InvalidOperationException($"configuration_scope_violation: '{key}' cannot be written to {requested}.")
{
    public const string DiagnosticCode = "configuration_scope_violation";
}

public sealed class ConfigurationMigrationException(string message) : InvalidOperationException(message);

public interface IConfigurationRegistry
{
    IReadOnlyCollection<ConfigurationKey> Keys { get; }

    ConfigurationKey FindRequired(string key);

    void RequireScope(string key, ConfigurationScope scope);
}

public interface IConfigurationStore
{
    ConfigurationScope Scope { get; }

    Task<ConfigurationDocument> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(ConfigurationDocument document, CancellationToken cancellationToken);
}

public sealed record ConfigurationDocument(
    int SchemaVersion,
    IReadOnlyDictionary<string, JsonElement> Values,
    Guid? ProjectId = null,
    string? Workflow = null)
{
    public static ConfigurationDocument Empty { get; } =
        new(1, new Dictionary<string, JsonElement>(StringComparer.Ordinal));
}

public interface IConfigurationMigration
{
    ConfigurationScope Scope { get; }

    int FromVersion { get; }

    int ToVersion { get; }

    ConfigurationDocument Apply(ConfigurationDocument document);
}
