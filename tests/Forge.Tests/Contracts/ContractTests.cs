using System.Text.Json;
using Json.Schema;

namespace Forge.Tests.Contracts;

public sealed class ContractTests
{
    [Fact]
    [Trait("Category", "Contracts")]
    public void Draft202012SchemasMatchCompatibilityFixtures()
    {
        string root = FindRepositoryRoot();
        string schemaRoot = Path.Combine(root, "docs", "contracts", "v1", "schemas");
        string fixturePath = Path.Combine(root, "tests", "Forge.Tests", "Contracts", "fixtures", "contract-cases.json");
        var buildOptions = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry()
        };

        var schemas = Directory.GetFiles(schemaRoot, "*.schema.json")
            .Order()
            .ToDictionary(
                path => Path.GetFileName(path).Replace(".schema.json", "", StringComparison.Ordinal),
                path => JsonSchema.FromFile(path, buildOptions),
                StringComparer.Ordinal);
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Forge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the Forge repository root.");
    }
}
