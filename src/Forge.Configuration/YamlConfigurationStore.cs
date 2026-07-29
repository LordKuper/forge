using System.Text;
using System.Text.Json;
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
    private readonly ISerializer serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public ConfigurationScope Scope { get; } = scope;

    public async Task<ConfigurationDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ConfigurationDocument.Empty;
        }

        string yaml = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        YamlDocument document = deserializer.Deserialize<YamlDocument>(yaml) ??
            throw new InvalidDataException("Configuration document is empty.");
        Dictionary<string, JsonElement> values = document.Values.ToDictionary(
            item => item.Key,
            item => JsonSerializer.SerializeToElement(item.Value),
            StringComparer.Ordinal);
        ConfigurationDocument result = new(document.SchemaVersion, values);
        Validate(result);
        return result;
    }

    public async Task WriteAsync(
        ConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        Dictionary<string, object?> values = document.Values.ToDictionary(
            item => item.Key,
            item => ConvertElement(item.Value),
            StringComparer.Ordinal);
        string yaml = serializer.Serialize(new YamlDocument
        {
            SchemaVersion = document.SchemaVersion,
            Values = values,
        });

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("Configuration path has no directory.");
        Directory.CreateDirectory(directory);
        string tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        string previousPath = $"{fullPath}.previous";
        try
        {
            byte[] contents = Encoding.UTF8.GetBytes(yaml);
            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, previousPath, true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void Validate(ConfigurationDocument document)
    {
        foreach (string key in document.Values.Keys)
        {
            registry.RequireScope(key, Scope);
        }
    }

    private static object? ConvertElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertElement).ToArray(),
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ConvertElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            _ => throw new InvalidDataException(
                $"Unsupported JSON value kind '{element.ValueKind}'."),
        };

    private sealed class YamlDocument
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, object?> Values { get; set; } = new(StringComparer.Ordinal);
    }
}
