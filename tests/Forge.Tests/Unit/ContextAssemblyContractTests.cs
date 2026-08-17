using System.Text.Json;
using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.UnitTests;

public sealed class ContextAssemblyContractTests
{
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    [Trait("Category", "Unit")]
    public void AContextManifestWithEveryLayerSatisfiesTheVersionedContract()
    {
        ContextManifestItem item = new("rules/testing.md", "sha256:" + new string('a', 64), 10, "rule");
        ContextManifest manifest = new(
            ContextManifest.ContractVersion,
            Guid.NewGuid(),
            SourceCommit,
            "implementation-critical",
            "1.0.0",
            1000,
            10,
            new([item], [], [], [], []),
            [new("rules/dropped.md", 2000, "over_budget")],
            "sha256:" + new string('b', 64));

        AssertValid(manifest, "context-manifest");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AContextQueryPlanWithBothOperationKindsSatisfiesTheVersionedContract()
    {
        ContextQueryPlan plan = new(
            ContextQueryPlan.ContractVersion,
            SourceCommit,
            [
                new("read-file", ContextQueryOperationKind.GitShow, Path: "src/foo.cs", MaxResultBytes: 4096),
                new("search", ContextQueryOperationKind.GitGrep, Pattern: "TODO", PathScope: "src"),
            ]);

        AssertValid(plan, "context-query-plan");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AContextResultBundleSatisfiesTheVersionedContract()
    {
        ContextResultBundle bundle = new(
            ContextResultBundle.ContractVersion,
            "sha256:" + new string('c', 64),
            SourceCommit,
            [
                new("read-file", ContextQueryOperationDiagnostic.None, "content", "sha256:" + new string('d', 64), 7, false),
                new("missing", ContextQueryOperationDiagnostic.NotFound, null, null, 0, false),
            ]);

        AssertValid(bundle, "context-result-bundle");
    }

    private static void AssertValid(ContextManifest manifest, string schemaName) => AssertValid(StatusJson.Serialize(manifest), schemaName);

    private static void AssertValid(ContextQueryPlan plan, string schemaName) => AssertValid(StatusJson.Serialize(plan), schemaName);

    private static void AssertValid(ContextResultBundle bundle, string schemaName) => AssertValid(StatusJson.Serialize(bundle), schemaName);

    private static void AssertValid(string json, string schemaName)
    {
        using JsonDocument instance = JsonDocument.Parse(json);
        EvaluationResults result = ContractSchemas.Load(schemaName).Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }
}
