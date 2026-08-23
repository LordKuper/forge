using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

/// <summary>Culture-invariant machine representation of the status contracts.</summary>
public static class StatusJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(ProjectSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static string Serialize(IReadOnlyList<SuggestedAction> actions) =>
        JsonSerializer.Serialize(actions, Options);

    public static string Serialize(ProviderHealth health) =>
        JsonSerializer.Serialize(health, Options);

    public static string Serialize(StartupStatus status) =>
        JsonSerializer.Serialize(status, Options);

    public static string Serialize(DiagnosticBundle bundle) =>
        JsonSerializer.Serialize(bundle, Options);

    public static string Serialize(EvaluationReport report) =>
        JsonSerializer.Serialize(report, Options);

    public static string Serialize(ExecutionProfile profile) =>
        JsonSerializer.Serialize(profile, Options);

    public static string Serialize(IntegrationInspectionResult result) =>
        JsonSerializer.Serialize(result, Options);

    public static string Serialize(IntegrationWriteResult result) =>
        JsonSerializer.Serialize(result, Options);

    public static string Serialize(ContextManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    public static string Serialize(ContextQueryPlan plan) =>
        JsonSerializer.Serialize(plan, Options);

    public static string Serialize(ContextResultBundle bundle) =>
        JsonSerializer.Serialize(bundle, Options);

    public static string Serialize(StageTransitionAssessment assessment) =>
        JsonSerializer.Serialize(assessment, Options);

    public static string Serialize(ProjectWorkspaceSummary summary) =>
        JsonSerializer.Serialize(summary, Options);

    public static string Serialize(IReadOnlyList<ProjectWorkspaceSummary> summaries) =>
        JsonSerializer.Serialize(summaries, Options);

    public static string Serialize(SprintTimelinePage page) =>
        JsonSerializer.Serialize(page, Options);

    public static string Serialize(IReadOnlyList<AvailableAction> actions) =>
        JsonSerializer.Serialize(actions, Options);

    public static string Serialize(IReadOnlyList<ProjectCatalogEntry> entries) =>
        JsonSerializer.Serialize(entries, Options);

    public static string Serialize(ProjectCatalogEntry entry) =>
        JsonSerializer.Serialize(entry, Options);

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
