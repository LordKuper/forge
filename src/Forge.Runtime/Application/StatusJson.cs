using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Application;

/// <summary>Culture-invariant machine representation of the status contracts.</summary>
public static class StatusJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(ProjectStatusSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static string Serialize(IReadOnlyList<SuggestedAction> actions) =>
        JsonSerializer.Serialize(actions, Options);

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
