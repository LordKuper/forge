using System.Text.Json;
using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;
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
            [new("claude_code|sonnet|sprint", CircuitState.Closed)],
            new(10, 7),
            [new("project_root", true)],
            ["circuit_breaker_state omitted: redaction could not be proven"]);

        string json = StatusJson.Serialize(bundle);
        using JsonDocument instance = JsonDocument.Parse(json);

        EvaluationResults result = ContractSchemas.Load("diagnostic-bundle").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }
}
