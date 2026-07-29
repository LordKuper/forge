using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Forge.Configuration;

internal static class ConfigurationSchemaCodec
{
    private const string ContractVersion = "1.0.0";
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
            Sprints = document.Sprints?.Select(item => item.ToString("D")).ToArray(),
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
        return new(
            1,
            values,
            Guid.Parse(persisted.ProjectId),
            persisted.Workflow,
            persisted.Sprints?.Select(Guid.Parse).ToArray());
    }

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
            : throw InvalidType(key, "string");
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
            : throw InvalidType(key, "boolean");
    }

    private static InvalidDataException InvalidType(string key, string expected) =>
        new($"Configuration key '{key}' must be a {expected}.");

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
        public string SchemaVersion { get; set; } = ContractVersion;

        public UserLanguage Language { get; set; } = new();

        public UserInteraction Interaction { get; set; } = new();
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

    internal sealed class ProjectConfiguration
    {
        public string SchemaVersion { get; set; } = ContractVersion;

        public string ProjectId { get; set; } = string.Empty;

        public string Workflow { get; set; } = WorkflowName;

        public ProjectArtifacts Artifacts { get; set; } = new();

        public IReadOnlyList<string>? Sprints { get; set; }
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
}
