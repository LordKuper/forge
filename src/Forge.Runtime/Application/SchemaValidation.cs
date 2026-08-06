using System.Text.Json;
using Json.Schema;

namespace Forge.Application;

/// <summary>Shared Draft 2020-12 validation for the embedded v1 contract schemas.</summary>
internal static class SchemaValidation
{
    public static JsonSchema LoadEmbedded(string logicalName)
    {
        using Stream stream = typeof(SchemaValidation).Assembly.GetManifestResourceStream(logicalName) ??
            throw new InvalidOperationException($"The embedded schema '{logicalName}' is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return JsonSchema.Build(document.RootElement.Clone());
    }

    public static void Validate(JsonElement element, JsonSchema schema, string scope)
    {
        EvaluationResults result = schema.Evaluate(
            element,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (!result.IsValid)
        {
            throw new InvalidDataException($"The {scope} does not conform to contract v1.");
        }
    }
}
