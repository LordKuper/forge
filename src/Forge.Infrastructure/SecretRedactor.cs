using System.Text.RegularExpressions;

namespace Forge.Infrastructure;

public sealed partial class SecretRedactor
{
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SecretPattern().Replace(value, match => $"{match.Groups[1].Value}=<redacted>");
    }

    public static IReadOnlyDictionary<string, object?> RedactProperties(
        IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.ToDictionary(
            item => item.Key,
            item => SensitiveNamePattern().IsMatch(item.Key)
                ? "<redacted>"
                : item.Value is string value ? Redact(value) : item.Value,
            StringComparer.Ordinal);
    }

    [GeneratedRegex(
        @"(?i)\b(token|password|secret|api[_-]?key)\s*=\s*[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(
        @"(?i)(token|password|secret|api[_-]?key)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();
}
