using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Forge.Configuration;

internal static class ConfigurationSchemaCodec
{
    /// <summary>Every new write stamps the latest minor version; an older document (missing
    /// <c>providers</c>) still validates under <c>user-config.schema.json</c>'s tolerant
    /// <c>schema_version</c> enum and is silently upgraded the next time it is saved.</summary>
    private const string UserContractVersion = "1.3.0";
    /// <summary>Bumped for ADR 0042's optional, nullable `models.allowed_models` -- an older
    /// document (missing `models`) still validates under `project-manifest.schema.json`'s tolerant
    /// `schema_version` enum, matching ADR 0029's own `context.token_budget` precedent.</summary>
    private const string ProjectContractVersion = "1.2.0";
    private const string WorkflowName = "implementation-critical";
    private static readonly JsonSchema UserSchema = LoadSchema("user-config");
    private static readonly JsonSchema ProjectSchema = LoadSchema("project-manifest");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    public static UserConfiguration ToUser(ConfigurationDocument document)
    {
        RequireVersion(document);
        UserConfiguration persisted = new()
        {
            Language = new()
            {
                Ui = GetOptionalString(document, "language.ui"),
                Interaction = GetOptionalString(document, "language.interaction"),
                Llm = GetOptionalString(document, "language.llm"),
            },
            Interaction = new()
            {
                ConfirmDestructive = GetOptionalBoolean(
                    document,
                    "interaction.confirm_destructive"),
            },
            // Omitted (null) means "no explicit selection" — ADR 0008's "omission selects all
            // registered built-in providers"; a present-but-empty list is the deliberate opposite
            // (blocks model work), so the distinction must survive this round trip.
            Providers = GetOptionalStringArray(document, ConfigurationKeys.ProvidersEnabled) is { } enabled
                ? new() { Enabled = enabled }
                : null,
            // Optional and nullable, like Providers above (not required, like Interaction) --
            // ADR 0024 added this key after schema_version 1.1.0 shipped, so an on-disk document
            // written before this key existed must still validate on read with it entirely absent.
            Notifications = GetOptionalBoolean(document, "notifications.enabled") is { } notificationsEnabled
                ? new() { Enabled = notificationsEnabled }
                : null,
            // Optional and nullable, like Notifications above -- ADR 0050 addendum added this key
            // after schema_version 1.2.0 shipped, so an on-disk document written before this key
            // existed must still validate on read with it entirely absent.
            Shell = GetOptionalBoolean(document, ConfigurationKeys.SidebarCollapsed) is { } sidebarCollapsed
                ? new() { SidebarCollapsed = sidebarCollapsed }
                : null,
        };
        Validate(JsonSerializer.SerializeToElement(persisted, JsonOptions), UserSchema, "user");
        return persisted;
    }

    public static ConfigurationDocument FromUser(JsonElement element)
    {
        Validate(element, UserSchema, "user");
        UserConfiguration persisted = element.Deserialize<UserConfiguration>(JsonOptions) ??
            throw new InvalidDataException("User configuration is empty.");
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        Add(values, "language.ui", persisted.Language.Ui);
        Add(values, "language.interaction", persisted.Language.Interaction);
        Add(values, "language.llm", persisted.Language.Llm);
        Add(values, "interaction.confirm_destructive", persisted.Interaction.ConfirmDestructive);
        Add(values, ConfigurationKeys.ProvidersEnabled, persisted.Providers?.Enabled);
        Add(values, "notifications.enabled", persisted.Notifications?.Enabled);
        Add(values, ConfigurationKeys.SidebarCollapsed, persisted.Shell?.SidebarCollapsed);
        return new(1, values);
    }

    public static ProjectConfiguration ToProject(ConfigurationDocument document)
    {
        RequireVersion(document);
        ProjectConfiguration persisted = new()
        {
            ProjectId = document.ProjectId?.ToString("D") ??
                throw new InvalidDataException("Project configuration requires a project ID."),
            Workflow = document.Workflow ?? WorkflowName,
            Artifacts = new()
            {
                Language = new()
                {
                    UserFacing = GetRequiredString(
                        document,
                        "artifacts.language.user_facing"),
                    AgentFacing = GetRequiredString(
                        document,
                        "artifacts.language.agent_facing"),
                },
            },
            // Optional and nullable, like UserNotifications -- ADR 0029 added this key after
            // schema_version 1.0.0 shipped, so a manifest written before this key existed must still
            // validate on read with it entirely absent.
            Context = GetOptionalInt32(document, "context.token_budget") is { } tokenBudget
                ? new() { TokenBudget = tokenBudget }
                : null,
            // Optional and nullable, like Context above -- ADR 0042 added this key after
            // schema_version 1.1.0 shipped, so a manifest written before this key existed must
            // still validate on read with it entirely absent. A present-but-empty list is the same
            // "no restriction" default as an absent key (ModelPolicyGate.IsAllowed), so no
            // omitted-vs-empty distinction needs preserving the way Providers.Enabled needs one.
            Models = GetOptionalStringArray(document, "models.allowed_models") is { } allowedModels
                ? new() { AllowedModels = allowedModels }
                : null,
        };
        Validate(
            JsonSerializer.SerializeToElement(persisted, JsonOptions),
            ProjectSchema,
            "project");
        return persisted;
    }

    public static ConfigurationDocument FromProject(ProjectConfiguration persisted)
    {
        JsonElement element = JsonSerializer.SerializeToElement(persisted, JsonOptions);
        Validate(element, ProjectSchema, "project");
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal)
        {
            ["artifacts.language.user_facing"] =
                JsonSerializer.SerializeToElement(persisted.Artifacts.Language.UserFacing),
            ["artifacts.language.agent_facing"] =
                JsonSerializer.SerializeToElement(persisted.Artifacts.Language.AgentFacing),
        };
        Add(values, "context.token_budget", persisted.Context?.TokenBudget);
        Add(values, "models.allowed_models", persisted.Models?.AllowedModels);
        return new(
            1,
            values,
            Guid.Parse(persisted.ProjectId),
            persisted.Workflow);
    }

    public static void ValidateProject(JsonElement element) =>
        Validate(element, ProjectSchema, "project");

    private static void RequireVersion(ConfigurationDocument document)
    {
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported configuration schema version '{document.SchemaVersion}'.");
        }
    }

    private static string? GetOptionalString(ConfigurationDocument document, string key)
    {
        if (!document.Values.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw InvalidType(key, "a string");
    }

    private static string GetRequiredString(ConfigurationDocument document, string key) =>
        GetOptionalString(document, key) ??
        throw new InvalidDataException($"Configuration key '{key}' is required.");

    private static bool? GetOptionalBoolean(ConfigurationDocument document, string key)
    {
        if (!document.Values.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw InvalidType(key, "a boolean");
    }

    private static int? GetOptionalInt32(ConfigurationDocument document, string key)
    {
        if (!document.Values.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : throw InvalidType(key, "an integer");
    }

    private static List<string>? GetOptionalStringArray(ConfigurationDocument document, string key)
    {
        if (!document.Values.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidType(key, "an array");
        }

        return [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw InvalidType(key, "an array of strings"))];
    }

    private static InvalidDataException InvalidType(string key, string expected) =>
        new($"Configuration key '{key}' must be {expected}.");

    private static void Add(
        Dictionary<string, JsonElement> values,
        string key,
        string? value)
    {
        if (value is not null)
        {
            values.Add(key, JsonSerializer.SerializeToElement(value));
        }
    }

    private static void Add(
        Dictionary<string, JsonElement> values,
        string key,
        bool? value)
    {
        if (value.HasValue)
        {
            values.Add(key, JsonSerializer.SerializeToElement(value.Value));
        }
    }

    private static void Add(
        Dictionary<string, JsonElement> values,
        string key,
        int? value)
    {
        if (value.HasValue)
        {
            values.Add(key, JsonSerializer.SerializeToElement(value.Value));
        }
    }

    private static void Add(
        Dictionary<string, JsonElement> values,
        string key,
        IReadOnlyList<string>? value)
    {
        if (value is not null)
        {
            values.Add(key, JsonSerializer.SerializeToElement(value));
        }
    }

    private static void Validate(JsonElement element, JsonSchema schema, string scope)
    {
        EvaluationResults result = schema.Evaluate(
            element,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            });
        if (!result.IsValid)
        {
            throw new InvalidDataException(
                $"The {scope} configuration does not conform to contract v1.");
        }
    }

    private static JsonSchema LoadSchema(string name)
    {
        using Stream stream = typeof(ConfigurationSchemaCodec).Assembly.GetManifestResourceStream(
            $"Forge.Configuration.Schemas.{name}.schema.json") ??
            throw new InvalidOperationException($"Embedded configuration schema '{name}' is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return JsonSchema.Build(document.RootElement.Clone());
    }

    internal sealed class UserConfiguration
    {
        public string SchemaVersion { get; set; } = UserContractVersion;

        public UserLanguage Language { get; set; } = new();

        public UserInteraction Interaction { get; set; } = new();

        public UserProviders? Providers { get; set; }

        public UserNotifications? Notifications { get; set; }

        public UserShell? Shell { get; set; }
    }

    internal sealed class UserLanguage
    {
        public string? Ui { get; set; }

        public string? Interaction { get; set; }

        public string? Llm { get; set; }
    }

    internal sealed class UserInteraction
    {
        public bool? ConfirmDestructive { get; set; }
    }

    internal sealed class UserProviders
    {
        public List<string>? Enabled { get; set; }
    }

    internal sealed class UserNotifications
    {
        public bool? Enabled { get; set; }
    }

    internal sealed class UserShell
    {
        public bool? SidebarCollapsed { get; set; }
    }

    internal sealed class ProjectConfiguration
    {
        public string SchemaVersion { get; set; } = ProjectContractVersion;

        public string ProjectId { get; set; } = string.Empty;

        public string Workflow { get; set; } = WorkflowName;

        public ProjectArtifacts Artifacts { get; set; } = new();

        public ProjectContext? Context { get; set; }

        public ProjectModels? Models { get; set; }
    }

    internal sealed class ProjectArtifacts
    {
        public ProjectLanguage Language { get; set; } = new();
    }

    internal sealed class ProjectLanguage
    {
        public string UserFacing { get; set; } = string.Empty;

        public string AgentFacing { get; set; } = string.Empty;
    }

    internal sealed class ProjectContext
    {
        public int? TokenBudget { get; set; }
    }

    internal sealed class ProjectModels
    {
        public List<string>? AllowedModels { get; set; }
    }
}
