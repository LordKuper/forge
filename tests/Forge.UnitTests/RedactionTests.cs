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
        Assert.Equal("provider token=<redacted> api_key=<redacted>", result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SensitiveStructuredPropertiesAreRedactedByName()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["provider"] = "codex",
            ["access_token"] = "abc123",
        };

        IReadOnlyDictionary<string, object?> result =
            SecretRedactor.RedactProperties(properties);

        Assert.Equal("codex", result["provider"]);
        Assert.Equal("<redacted>", result["access_token"]);
    }
}
