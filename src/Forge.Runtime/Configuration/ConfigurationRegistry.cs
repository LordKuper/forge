using System.Text.Json;

namespace Forge.Configuration;

public sealed class ConfigurationRegistry : IConfigurationRegistry
{
    private readonly IReadOnlyDictionary<string, ConfigurationKey> keys;

    public ConfigurationRegistry(IEnumerable<ConfigurationKey>? keys = null)
    {
        ConfigurationKey[] registered = (keys ?? CreateDefaultKeys()).ToArray();
        this.keys = registered.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (this.keys.Count != registered.Length)
        {
            throw new ArgumentException("Configuration key names must be unique.", nameof(keys));
        }
    }

    public IReadOnlyCollection<ConfigurationKey> Keys => keys.Values.ToArray();

    public ConfigurationKey FindRequired(string key) =>
        keys.TryGetValue(key, out ConfigurationKey? value)
            ? value
            : throw new KeyNotFoundException($"Unknown configuration key '{key}'.");

    public void RequireScope(string key, ConfigurationScope scope)
    {
        if (FindRequired(key).Scope != scope)
        {
            throw new ConfigurationScopeException(key, scope);
        }
    }

    private static IEnumerable<ConfigurationKey> CreateDefaultKeys()
    {
        yield return Create("language.ui", ConfigurationScope.User, "\"en\"", null, true);
        yield return Create(
            "language.interaction",
            ConfigurationScope.User,
            "null",
            "language.ui",
            true);
        yield return Create(
            "language.llm",
            ConfigurationScope.User,
            "null",
            "language.interaction",
            true);
        yield return Create(
            "interaction.confirm_destructive",
            ConfigurationScope.User,
            "true",
            null,
            false);
        yield return Create(
            ConfigurationKeys.ProvidersEnabled,
            ConfigurationScope.User,
            "null",
            null,
            false);
        yield return Create(
            "artifacts.language.user_facing",
            ConfigurationScope.Project,
            "\"en\"",
            null,
            false);
        yield return Create(
            "artifacts.language.agent_facing",
            ConfigurationScope.Project,
            "\"en\"",
            null,
            false);
        // ADR 0024: "user-configurable" per ADR 0005's "Notifications are local attention
        // projections" -- defaults to on, matching every other opt-out (rather than opt-in) policy
        // toggle in this registry.
        yield return Create(
            "notifications.enabled",
            ConfigurationScope.User,
            "true",
            null,
            false);
    }

    private static ConfigurationKey Create(
        string name,
        ConfigurationScope scope,
        string defaultJson,
        string? inherits,
        bool sessionOverride) =>
        new(
            name,
            scope,
            JsonSerializer.Deserialize<JsonElement>(defaultJson),
            inherits,
            sessionOverride,
            false);
}
