using System.Text.Json;
using Json.Schema;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Forge.ContractTests <repository-root>");
    return 2;
}

var repositoryRoot = Path.GetFullPath(args[0]);
var schemaRoot = Path.Combine(repositoryRoot, "docs", "contracts", "v1", "schemas");
var fixturePath = Path.Combine(repositoryRoot, "tests", "contracts", "fixtures", "contract-cases.json");

Dialect.Default = Dialect.Draft202012;

var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
foreach (var schemaPath in Directory.GetFiles(schemaRoot, "*.schema.json").Order())
{
    var name = Path.GetFileName(schemaPath).Replace(".schema.json", "", StringComparison.Ordinal);
    schemas.Add(name, JsonSchema.FromFile(schemaPath));
}

using var fixtureDocument = JsonDocument.Parse(File.ReadAllText(fixturePath));
var cases = fixtureDocument.RootElement.GetProperty("cases");
var failures = new List<string>();
var options = new EvaluationOptions
{
    OutputFormat = OutputFormat.List,
    RequireFormatValidation = true,
    AddAnnotationForUnknownKeywords = true
};

try
{
    using var invalidSchemaDocument = JsonDocument.Parse(
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":7}""");
    _ = JsonSchema.Build(invalidSchemaDocument.RootElement);
    failures.Add("meta-schema validation accepted an invalid keyword value.");
}
catch (Exception)
{
    // Expected: schema construction validates keyword shapes against Draft 2020-12.
}

foreach (var testCase in cases.EnumerateArray())
{
    var name = testCase.GetProperty("name").GetString()!;
    var schemaName = testCase.GetProperty("schema").GetString()!;
    var expected = testCase.GetProperty("valid").GetBoolean();
    var instance = testCase.GetProperty("instance");

    if (!schemas.TryGetValue(schemaName, out var schema))
    {
        failures.Add($"{name}: unknown schema '{schemaName}'.");
        continue;
    }

    var result = schema.Evaluate(instance, options);
    if (result.IsValid != expected)
    {
        failures.Add($"{name}: expected valid={expected}, actual valid={result.IsValid}. Result: {JsonSerializer.Serialize(result)}");
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine($"Draft 2020-12 validation passed: {schemas.Count} schemas, {cases.GetArrayLength()} compatibility fixtures.");
return 0;
