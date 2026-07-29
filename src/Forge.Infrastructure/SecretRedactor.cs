using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Infrastructure;

public sealed partial class SecretRedactor
{
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = PrivateKeyPattern().Replace(
            value,
            "[REDACTED:private_key]");
        redacted = AuthorizationPattern().Replace(
            redacted,
            match => $"{match.Groups[1].Value} [REDACTED:authorization]");
        redacted = CredentialUriPattern().Replace(
            redacted,
            match => $"{match.Groups[1].Value}[REDACTED:credential]@");
        redacted = JwtPattern().Replace(redacted, "[REDACTED:token]");
        return SecretAssignmentPattern().Replace(
            redacted,
            match =>
                $"{match.Groups[1].Value}{match.Groups[2].Value}[REDACTED:{GetKind(match.Groups[1].Value)}]");
    }

    public static IReadOnlyDictionary<string, object?> RedactProperties(
        IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.ToDictionary(
            item => item.Key,
            item => RedactValue(item.Key, item.Value),
            StringComparer.Ordinal);
    }

    private static object? RedactValue(string? name, object? value)
    {
        if (name is not null && TryGetKind(name, out string? kind))
        {
            return $"[REDACTED:{kind}]";
        }

        if (value is not null &&
            TryGetGenericDictionaryKeyType(value.GetType(), out Type? keyType))
        {
            return keyType == typeof(string)
                ? RedactSerializable(value)
                : DroppedPayload();
        }

        return value switch
        {
            null => null,
            string text => Redact(text),
            JsonElement element => RedactElement(element),
            IReadOnlyDictionary<string, object?> dictionary =>
                RedactProperties(dictionary),
            IDictionary dictionary => RedactDictionary(dictionary),
            IEnumerable collection => collection
                .Cast<object?>()
                .Select(item => RedactValue(null, item))
                .ToArray(),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal or char or Guid or DateTime or DateTimeOffset =>
                value,
            _ => RedactSerializable(value),
        };
    }

    private static bool TryGetGenericDictionaryKeyType(Type type, out Type? keyType)
    {
        Type? dictionary = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(
                item =>
                    item.IsGenericType &&
                    (item.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                        item.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        keyType = dictionary?.GetGenericArguments()[0];
        return dictionary is not null;
    }

    private static Dictionary<string, object?> RedactDictionary(IDictionary dictionary)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach (DictionaryEntry item in dictionary)
        {
            if (item.Key is not string key)
            {
                return DroppedPayload();
            }

            result[key] = RedactValue(key, item.Value);
        }

        return result;
    }

    private static object? RedactElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Array => element
                .EnumerateArray()
                .Select(item => RedactValue(null, item))
                .ToArray(),
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Object => element
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => RedactValue(property.Name, property.Value),
                    StringComparer.Ordinal),
            JsonValueKind.String => Redact(element.GetString() ?? string.Empty),
            JsonValueKind.True => true,
            _ => DroppedPayload(),
        };

    private static object RedactSerializable(object value)
    {
        try
        {
            return RedactElement(JsonSerializer.SerializeToElement(value)) ??
                DroppedPayload();
        }
        catch (Exception error) when (
            error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return DroppedPayload();
        }
    }

    private static Dictionary<string, object?> DroppedPayload() =>
        new(StringComparer.Ordinal)
        {
            ["event"] = "redaction_payload_dropped",
        };

    [GeneratedRegex(
        """(?i)\b(password|secret|token|api[_-]?key|authorization|cookie|credential|private[_ -]?key|provider[_ -]?session)\b(\s*=\s*)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\s,;]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(
        @"(?i)(password|secret|token|api[_-]?key|authorization|cookie|credential|private[_ -]?key|provider[_ -]?session)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();

    [GeneratedRegex(
        @"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/=]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(
        @"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(
        @"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUriPattern();

    [GeneratedRegex(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    private static bool TryGetKind(string name, out string? kind)
    {
        Match match = SensitiveNamePattern().Match(name);
        if (!match.Success)
        {
            kind = null;
            return false;
        }

        kind = GetKind(match.Value);
        return true;
    }

    private static string GetKind(string name)
    {
        string normalized = name
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "api_key" => "credential",
            "password" => "credential",
            "private_key" => "private_key",
            "provider_session" => "provider_session",
            _ => normalized,
        };
    }
}
