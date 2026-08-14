using Json.Schema;

namespace Forge.Tests.Support;

/// <summary>Builds every docs/contracts/v1/schemas/*.schema.json file against one fresh registry
/// per call — JsonSchema.Net rejects re-registering the same `$id` across calls in one process, so
/// a fresh registry every time keeps repeated test methods from colliding, while building the
/// whole directory together (not just the one requested) lets cross-file `$ref`s resolve.</summary>
internal static class ContractSchemas
{
    public static JsonSchema Load(string name) => LoadAll()[name];

    public static IReadOnlyDictionary<string, JsonSchema> LoadAll()
    {
        string schemaRoot = Path.Combine(
            Forge.UnitTests.RepositoryRoot.Find(), "docs", "contracts", "v1", "schemas");
        BuildOptions buildOptions = new() { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry() };
        return Directory
            .GetFiles(schemaRoot, "*.schema.json")
            .ToDictionary(
                path => Path.GetFileName(path).Replace(".schema.json", string.Empty, StringComparison.Ordinal),
                path => JsonSchema.FromFile(path, buildOptions),
                StringComparer.Ordinal);
    }
}
