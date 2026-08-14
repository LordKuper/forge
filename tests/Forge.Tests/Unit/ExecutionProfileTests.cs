using System.Text.Json;
using Forge.Application;
using Json.Schema;

namespace Forge.UnitTests;

public sealed class ExecutionProfileTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void AReviewProfileWithLineageEvidenceSatisfiesTheVersionedExecutionProfileContract()
    {
        ExecutionProfile profile = new(
            ExecutionProfile.ContractVersion,
            ExecutionPhase.Review,
            "codex",
            "gpt-5",
            "high",
            "workspace-write",
            "never",
            ["read_file", "grep"],
            3600,
            300,
            new("claude_code", "sonnet", true));

        AssertValid(profile);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void APlanningProfileWithoutLineageSatisfiesTheVersionedExecutionProfileContract()
    {
        ExecutionProfile profile = new(
            ExecutionProfile.ContractVersion,
            ExecutionPhase.Planning,
            "claude_code",
            "sonnet",
            "medium",
            "workspace-write",
            "never",
            [],
            1800,
            180);

        AssertValid(profile);
    }

    private static void AssertValid(ExecutionProfile profile)
    {
        string json = StatusJson.Serialize(profile);
        using JsonDocument instance = JsonDocument.Parse(json);

        EvaluationResults result = LoadSchema().Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }

    /// <summary>A fresh registry per call: JsonSchema.Net rejects re-registering the same `$id`
    /// across test methods within one process.</summary>
    private static JsonSchema LoadSchema() =>
        JsonSchema.FromFile(
            Path.Combine(RepositoryRoot.Find(), "docs", "contracts", "v1", "schemas", "execution-profile.schema.json"),
            new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry() });
}
