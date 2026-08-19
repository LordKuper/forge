using System.Collections;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Forge.Configuration;

public sealed class YamlConfigurationStore(
    string path,
    ConfigurationScope scope,
    IConfigurationRegistry registry) : IConfigurationStore
{
    // Without this, YamlDotNet's untyped Deserialize<object> stringifies every scalar -- the same
    // verified behavior ForgeDocumentCompiler's own typedDeserializer comment documents -- which
    // would silently turn `token_budget: 40000` into the JSON string "40000" and fail
    // project-manifest.schema.json's `"type": "integer"` (ADR 0029). This store cannot deserialize
    // directly into a typed DTO the way ForgeDocumentCompiler does, since NormalizeYaml/
    // StripLegacySprintRegistry must inspect the raw shape before any typed schema applies; this
    // builder option makes the untyped pass itself scalar-type-aware instead.
    private readonly IDeserializer rawDeserializer = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();
    private readonly ISerializer serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public ConfigurationScope Scope { get; } = scope;

    public async Task<ConfigurationDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ConfigurationDocument.Empty;
        }

        try
        {
            return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRecoverable(error) && File.Exists($"{path}.previous"))
        {
            ConfigurationDocument recovered =
                await ReadFileAsync($"{path}.previous", cancellationToken).ConfigureAwait(false);
            byte[] contents = await File.ReadAllBytesAsync(
                $"{path}.previous",
                cancellationToken).ConfigureAwait(false);
            await AtomicConfigurationFile.WriteAsync(
                path,
                contents,
                cancellationToken,
                false).ConfigureAwait(false);
            return recovered;
        }
    }

    public async Task WriteAsync(
        ConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateScope(document);
        ConfigurationSchemaCodec.ProjectConfiguration persisted =
            ConfigurationSchemaCodec.ToProject(document);
        Console.Error.WriteLine($"DIAG persisted.Context={(persisted.Context is null ? "null" : $"TokenBudget={persisted.Context.TokenBudget}")}");
        string yaml = serializer.Serialize(persisted);
        Console.Error.WriteLine($"DIAG yaml=[{yaml}]");
        await AtomicConfigurationFile.WriteAsync(
            path,
            Encoding.UTF8.GetBytes(yaml),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConfigurationDocument> ReadFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        string yaml = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        object? raw = rawDeserializer.Deserialize<object>(yaml);
        object? normalized = NormalizeYaml(raw);
        StripLegacySprintRegistry(normalized);
        JsonElement rawElement = JsonSerializer.SerializeToElement(normalized);
        ConfigurationSchemaCodec.ValidateProject(rawElement);
        ConfigurationSchemaCodec.ProjectConfiguration persisted =
            rawElement.Deserialize<ConfigurationSchemaCodec.ProjectConfiguration>(
                ConfigurationSchemaCodec.SerializerOptions) ??
            throw new InvalidDataException("Project configuration is empty.");
        ConfigurationDocument result = ConfigurationSchemaCodec.FromProject(persisted);
        ValidateScope(result);
        return result;
    }

    private void ValidateScope(ConfigurationDocument document)
    {
        foreach (string key in document.Values.Keys)
        {
            registry.RequireScope(key, Scope);
        }
    }

    private static bool IsRecoverable(Exception error) =>
        error is YamlException or InvalidDataException or ConfigurationScopeException or FormatException;

    /// <summary>Migrates persisted pre-v0.11 data before validating the current pre-1.0 contract.
    /// The removed registry is not exposed by the schema or application API.</summary>
    private static void StripLegacySprintRegistry(object? normalized)
    {
        if (normalized is not Dictionary<string, object?> root ||
            !root.Remove("sprints", out object? legacy))
        {
            return;
        }

        if (legacy is not object?[] values || values.Any(item => item is not string text || !Guid.TryParse(text, out _)) ||
            values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            throw new InvalidDataException("The legacy project sprint registry is invalid.");
        }
    }

    private static object? NormalizeYaml(object? value) =>
        value switch
        {
            IDictionary dictionary => NormalizeDictionary(dictionary),
            IEnumerable collection when value is not string =>
                collection.Cast<object?>().Select(NormalizeYaml).ToArray(),
            _ => value,
        };

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            string key = entry.Key as string ??
                throw new InvalidDataException("YAML configuration keys must be strings.");
            result.Add(key, NormalizeYaml(entry.Value));
        }

        return result;
    }
}
