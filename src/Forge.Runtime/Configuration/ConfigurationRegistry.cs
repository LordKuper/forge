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
        // ADR 0067: user-scoped like interaction.confirm_destructive above, and deliberately
        // separate from it -- confirm_destructive governs surface confirmations, this governs the
        // workflow's own human-approval node. Defaults to false because that is the behavior every
        // prior release shipped: the gate is unconditional today and nothing reads this key yet.
        yield return Create(
            ConfigurationKeys.AutoApproveGate,
            ConfigurationScope.User,
            "false",
            null,
            false);
        yield return Create(
            ConfigurationKeys.ProvidersEnabled,
            ConfigurationScope.User,
            "null",
            null,
            false);
        // ADR 0067 (plan decision Q23: user scope only): the preferred routing order among enabled
        // providers. Empty is "no preference" -- the registration order every release has used --
        // so, unlike providers.enabled above, an omitted and an explicitly empty list mean the same
        // thing and no omitted-vs-empty distinction needs preserving (matching
        // models.allowed_models below).
        yield return Create(
            ConfigurationKeys.ProvidersPriority,
            ConfigurationScope.User,
            "[]",
            null,
            false);
        // ADR 0067 (plan decision Q23: user scope only): model id -> effort level. User-scoped
        // rather than project-scoped like models.allowed_models below, because how hard a given
        // model should think is a per-operator preference, not a property of the project's policy.
        // The first object-typed key in this registry; empty is "no per-model preference," which is
        // what ExecutionProfilePolicy's frozen per-phase efforts already express.
        yield return Create(
            ConfigurationKeys.ModelsEffort,
            ConfigurationScope.User,
            "{}",
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
        // ADR 0067 (plan decision Q24, shape only): user-scoped shell appearance, grouped with
        // shell.sidebar_collapsed above. Defaults to "dark" because App.xaml declares dark tokens
        // only -- "light" and "system" are valid configuration today with no palette behind them
        // until the light ramp lands (slice S24).
        yield return Create(
            ConfigurationKeys.ShellTheme,
            ConfigurationScope.User,
            "\"dark\"",
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
