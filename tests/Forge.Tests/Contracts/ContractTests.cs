using System.Text.Json;
using Forge.Configuration;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.Tests.Contracts;

public sealed class ContractTests
{
    /// <summary>Round 1 review of PR #64 found `docs/contracts/v1/configuration.json`'s own `keys`
    /// list can drift from `ConfigurationRegistry.CreateDefaultKeys()` (it had, for the new
    /// `notifications.enabled` key) with nothing catching it. Proves both directions: every
    /// registered key is documented, and every documented key is registered, with matching scope,
    /// session-override, inheritance, and default value.</summary>
    [Fact]
    [Trait("Category", "Contracts")]
    public void ConfigurationRegistryMatchesTheContractsKeyList()
    {
        string root = Forge.UnitTests.RepositoryRoot.Find();
        string path = Path.Combine(root, "docs", "contracts", "v1", "configuration.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement keysElement = document.RootElement.GetProperty("keys");

        ConfigurationRegistry registry = new();
        Dictionary<string, ConfigurationKey> registryKeys =
            registry.Keys.ToDictionary(key => key.Name, StringComparer.Ordinal);
        List<string> contractKeyNames = [];

        foreach (JsonElement contractKey in keysElement.EnumerateArray())
        {
            string name = contractKey.GetProperty("key").GetString()!;
            contractKeyNames.Add(name);
            Assert.True(
                registryKeys.TryGetValue(name, out ConfigurationKey? registryKey),
                $"'{name}' is documented in configuration.json but not registered in ConfigurationRegistry.");

            Assert.Equal(
                registryKey!.Scope == ConfigurationScope.User ? "user" : "project",
                contractKey.GetProperty("scope").GetString());
            Assert.Equal(
                registryKey.AllowsSessionOverride, contractKey.GetProperty("session_override").GetBoolean());
            JsonElement inheritsProperty = contractKey.GetProperty("inherits");
            Assert.Equal(
                registryKey.Inherits,
                inheritsProperty.ValueKind == JsonValueKind.Null ? null : inheritsProperty.GetString());

            bool contractHasDynamicDefault =
                contractKey.TryGetProperty("default_is_dynamic", out JsonElement dynamicFlag) &&
                dynamicFlag.GetBoolean();
            if (!contractHasDynamicDefault)
            {
                string expectedDefault = registryKey.DefaultValue.HasValue
                    ? registryKey.DefaultValue.Value.GetRawText()
                    : "null";
                Assert.Equal(expectedDefault, contractKey.GetProperty("default").GetRawText());
            }
        }

        Assert.Equal(
            registryKeys.Keys.OrderBy(name => name, StringComparer.Ordinal),
            contractKeyNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Category", "Contracts")]
    public void Draft202012SchemasMatchCompatibilityFixtures()
    {
        string root = Forge.UnitTests.RepositoryRoot.Find();
        string fixturePath = Path.Combine(root, "tests", "Forge.Tests", "Contracts", "fixtures", "contract-cases.json");
        var buildOptions = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry()
        };
        var schemas = ContractSchemas.LoadAll();
        using var fixtureDocument = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var failures = new List<string>();
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
            AddAnnotationForUnknownKeywords = true
        };

        Assert.ThrowsAny<Exception>(() =>
        {
            using var invalidSchema = JsonDocument.Parse(
                """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":7}""");
            _ = JsonSchema.Build(invalidSchema.RootElement, buildOptions);
        });

        foreach (var testCase in fixtureDocument.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = testCase.GetProperty("name").GetString()!;
            string schemaName = testCase.GetProperty("schema").GetString()!;
            bool expected = testCase.GetProperty("valid").GetBoolean();
            if (!schemas.TryGetValue(schemaName, out JsonSchema? schema) || schema is null)
            {
                failures.Add($"{name}: unknown schema '{schemaName}'.");
                continue;
            }

            var result = schema.Evaluate(testCase.GetProperty("instance"), options);
            if (result.IsValid != expected)
            {
                failures.Add($"{name}: expected valid={expected}, actual valid={result.IsValid}. Result: {JsonSerializer.Serialize(result)}");
            }
        }

        Assert.Empty(failures);
    }
}
