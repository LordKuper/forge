using System.Text.Json;
using Forge.Application;
using Forge.Tests.Support;
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

        EvaluationResults result = ContractSchemas.Load("execution-profile").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }
}
