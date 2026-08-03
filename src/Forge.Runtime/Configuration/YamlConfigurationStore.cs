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
    private readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();
    private readonly IDeserializer rawDeserializer = new DeserializerBuilder().Build();
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
        string yaml = serializer.Serialize(persisted);
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
        JsonElement rawElement = JsonSerializer.SerializeToElement(NormalizeYaml(raw));
        ConfigurationSchemaCodec.ValidateProject(rawElement);
        ConfigurationSchemaCodec.ProjectConfiguration persisted =
            deserializer.Deserialize<ConfigurationSchemaCodec.ProjectConfiguration>(yaml) ??
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
