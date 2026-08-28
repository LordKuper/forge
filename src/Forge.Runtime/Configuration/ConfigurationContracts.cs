using System.Text.Json;

namespace Forge.Configuration;

public enum ConfigurationScope
{
    User,
    Project,
}

/// <summary>Configuration key names referenced by more than one class, kept in one place so a
/// rename cannot silently desync a reader from a writer.</summary>
public static class ConfigurationKeys
{
    public const string ProvidersEnabled = "providers.enabled";

    /// <summary>ADR 0067: the user's preferred order for routing among the providers
    /// <see cref="ProvidersEnabled"/> allows. Declared, validated, and resolved only -- no routing
    /// code reads it yet.</summary>
    public const string ProvidersPriority = "providers.priority";

    /// <summary>ADR 0067: a map from model id to one of <c>ProviderEffortLevels.KnownLevels</c>.
    /// Declared, validated, and resolved only -- <c>ExecutionProfilePolicy.Freeze</c> does not read
    /// it.</summary>
    public const string ModelsEffort = "models.effort";

    /// <summary>ADR 0067: whether the mandatory human-approval gate may be approved automatically.
    /// A schema placeholder with no enforcement path -- see that ADR before wiring a consumer.</summary>
    public const string AutoApproveGate = "interaction.auto_approve_gate";

    /// <summary>Desktop-instance-level UI preference (ADR 0050 addendum): whether the workspace
    /// shell's sidebar is collapsed to its icon-only rail. User-scoped like
    /// <c>notifications.enabled</c> -- a per-installation preference, never tied to one project.</summary>
    public const string SidebarCollapsed = "shell.sidebar_collapsed";

    /// <summary>ADR 0067: the desktop shell's colour theme. Grouped with
    /// <see cref="SidebarCollapsed"/> because both are per-installation shell appearance
    /// preferences. <c>light</c> is a valid value with no palette behind it until S24.</summary>
    public const string ShellTheme = "shell.theme";
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
