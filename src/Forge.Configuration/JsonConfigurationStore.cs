using System.Text;
using System.Text.Json;

namespace Forge.Configuration;

public sealed class JsonConfigurationStore(
    string path,
    ConfigurationScope scope,
    IConfigurationRegistry registry) : IConfigurationStore
{
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
        ConfigurationSchemaCodec.UserConfiguration persisted =
            ConfigurationSchemaCodec.ToUser(document);
        byte[] contents = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(persisted, ConfigurationSchemaCodec.SerializerOptions));
        await AtomicConfigurationFile.WriteAsync(
            path,
            contents,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConfigurationDocument> ReadFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        using JsonDocument document =
            await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        ConfigurationDocument result =
            ConfigurationSchemaCodec.FromUser(document.RootElement);
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
        error is JsonException or InvalidDataException or ConfigurationScopeException;
}
