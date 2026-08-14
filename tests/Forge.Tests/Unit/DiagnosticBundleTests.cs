using System.Text.Json;
using Forge.Application;
using Json.Schema;

namespace Forge.UnitTests;

public sealed class DiagnosticBundleTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ASampleBundleSatisfiesTheVersionedDiagnosticBundleContract()
    {
        DiagnosticBundle bundle = new(
            DiagnosticBundle.ContractVersion,
            DateTimeOffset.UnixEpoch,
            "0.16.0",
            "1.0.0",
            [new("codex", "0.146.0"), new("claude_code", null)],
            [StartupCheck.Passed(StartupCheckId.UserConfiguration)],
            new(true, 2),
            new(true, "none"),
            new(3, 0),
            [new("claude_code|sonnet|sprint", DiagnosticCircuitState.Closed)],
            new(10, 7),
            [new("project_root", true)],
            ["circuit_breaker_state omitted: redaction could not be proven"]);

        string json = StatusJson.Serialize(bundle);
        using JsonDocument instance = JsonDocument.Parse(json);

        EvaluationResults result = LoadSchema("diagnostic-bundle").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }

    /// <summary>Builds every schema against one fresh registry (a new one per call, since
    /// JsonSchema.Net rejects re-registering the same `$id` across test methods) so a schema like
    /// diagnostic-bundle's cross-file `$ref` into startup-check.schema.json resolves.</summary>
    private static JsonSchema LoadSchema(string name)
    {
        string schemaRoot = Path.Combine(RepositoryRoot.Find(), "docs", "contracts", "v1", "schemas");
        BuildOptions buildOptions = new() { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry() };
        Dictionary<string, JsonSchema> schemas = Directory
            .GetFiles(schemaRoot, "*.schema.json")
            .ToDictionary(
                path => Path.GetFileName(path).Replace(".schema.json", string.Empty, StringComparison.Ordinal),
                path => JsonSchema.FromFile(path, buildOptions),
                StringComparer.Ordinal);
        return schemas[name];
    }
}
