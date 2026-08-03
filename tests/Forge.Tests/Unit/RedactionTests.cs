using System.Collections.Frozen;
using System.Text.Json;
using Forge.Infrastructure;

namespace Forge.UnitTests;

public sealed class RedactionTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void KnownSecretAssignmentsAreRedacted()
    {
        string result = SecretRedactor.Redact("provider token=abc123 api_key=qwerty");

        Assert.DoesNotContain("abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("qwerty", result, StringComparison.Ordinal);
        Assert.Equal(
            "provider token=[REDACTED:token] api_key=[REDACTED:credential]",
            result);
    }

    [Theory]
    [InlineData("password", "credential")]
    [InlineData("secret", "secret")]
    [InlineData("token", "token")]
    [InlineData("authorization", "authorization")]
    [InlineData("cookie", "cookie")]
    [InlineData("credential", "credential")]
    [InlineData("private_key", "private_key")]
    [InlineData("provider_session", "provider_session")]
    [Trait("Category", "Security")]
    public void SensitiveStructuredPropertiesAreRedactedByName(string name, string kind)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["provider"] = "codex",
            [name] = "abc123",
        };

        IReadOnlyDictionary<string, object?> result =
            SecretRedactor.RedactProperties(properties);

        Assert.Equal("codex", result["provider"]);
        Assert.Equal($"[REDACTED:{kind}]", result[name]);
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "abcdefghijklmnop")]
    [InlineData("value=aaaabbbb.ccccdddd.eeeeffff", "aaaabbbb.ccccdddd.eeeeffff")]
    [InlineData("https://user:password@example.test/path", "user:password")]
    [InlineData(
        "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----",
        "secret")]
    [InlineData("password=\"correct horse battery staple\"", "correct horse battery staple")]
    [InlineData("Authorization: Bearer abc~def", "abc~def")]
    [Trait("Category", "Security")]
    public void SensitiveValuesAreRedacted(string input, string secret)
    {
        string result = SecretRedactor.Redact(input);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", result, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NestedStructuredValuesAreRecursivelyRedacted()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["payload"] = new Dictionary<string, object?>
            {
                ["items"] = new object?[]
                {
                    new ProviderPayload("codex", "abc123"),
                },
            },
        };

        IReadOnlyDictionary<string, object?> result =
            SecretRedactor.RedactProperties(properties);
        string json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:token]", json, StringComparison.Ordinal);
        Assert.Contains("codex", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GenericReadOnlyDictionariesPreserveSensitiveFieldNames()
    {
        IReadOnlyDictionary<string, string> payload =
            new Dictionary<string, string>
            {
                ["provider"] = "codex",
                ["token"] = "abc123",
            }.ToFrozenDictionary(StringComparer.Ordinal);

        string json = JsonSerializer.Serialize(
            SecretRedactor.RedactProperties(
                new Dictionary<string, object?> { ["payload"] = payload }));

        Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:token]", json, StringComparison.Ordinal);
    }

    private sealed record ProviderPayload(string Provider, string Token);
}
