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
        // ADR 0029: project-scoped, not user-scoped, because how much of a project's own `.forge/`
        // content fits a budget is a property of that project's document set, not a per-machine
        // preference -- matching artifacts.language.* above rather than notifications.enabled.
        // 32000 mirrors IntakeExecutionHostedService.DefaultTokenBudget, the fallback used when this
        // key is absent or invalid. The first integer-typed key in this registry.
        yield return Create(
            "context.token_budget",
            ConfigurationScope.Project,
            "32000",
            null,
            false);
        // ADR 0042: project-scoped, like context.token_budget above -- which models are acceptable
        // is a property of the project's own policy, not a per-machine preference. Empty means "no
        // per-project model policy configured," the behavior every sprint had before this key
        // existed (ModelPolicyGate.IsAllowed treats an unlisted provider as unrestricted).
        yield return Create(
            "models.allowed_models",
            ConfigurationScope.Project,
            "[]",
            null,
            false);
        // ADR 0050 addendum: a per-installation UI preference (whether the workspace shell's
        // sidebar is collapsed), not tied to any one project -- user-scoped like
        // notifications.enabled above, defaulting to expanded (matching the layout every prior
        // release shipped).
        yield return Create(
            ConfigurationKeys.SidebarCollapsed,
            ConfigurationScope.User,
            "false",
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
