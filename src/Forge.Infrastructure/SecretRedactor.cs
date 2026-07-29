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
            item => TryGetKind(item.Key, out string? kind)
                ? $"[REDACTED:{kind}]"
                : item.Value is string value ? Redact(value) : item.Value,
            StringComparer.Ordinal);
    }

    [GeneratedRegex(
        @"(?i)\b(password|secret|token|api[_-]?key|authorization|cookie|credential|private[_ -]?key|provider[_ -]?session)\b(\s*=\s*)[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(
        @"(?i)(password|secret|token|api[_-]?key|authorization|cookie|credential|private[_ -]?key|provider[_ -]?session)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();

    [GeneratedRegex(
        @"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9+/=_\-.:]+",
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
