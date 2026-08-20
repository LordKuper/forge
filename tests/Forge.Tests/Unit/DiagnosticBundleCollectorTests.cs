using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.UnitTests;

/// <summary>
/// <see cref="ForgeApplication.CollectDiagnosticBundleAsync"/> (`forge doctor --bundle`, ADR
/// 0005/0038). Unlike <see cref="DiagnosticBundleTests"/> (which proves a synthetic, hand-built
/// bundle satisfies the schema), these tests prove the real collector — driven against a real
/// <see cref="TestEnvironment"/> project — produces correct, schema-conforming output.
/// </summary>
public sealed class DiagnosticBundleCollectorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CollectDiagnosticBundleAsyncOnAnUninitializedProjectReportsUninitializedWithoutOmissions()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DiagnosticBundle bundle = await environment.Application
            .CollectDiagnosticBundleAsync(environment.ProjectRoot, cancellationToken);

        Assert.False(bundle.Project.Initialized);
        Assert.Equal(0, bundle.Project.SprintCount);
        Assert.Empty(bundle.Omissions);
        Assert.True(bundle.EventLogIntegrity.Valid);
        Assert.Equal(0, bundle.WorktreeRegistrations.Count);
        Assert.Equal(RoutingLedger.DefaultRetryBudget, bundle.RetryBudget.Total);
        Assert.Equal(RoutingLedger.DefaultRetryBudget, bundle.RetryBudget.Remaining);
        AssertSchemaValid(bundle);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CollectDiagnosticBundleAsyncOnAnInitializedProjectReportsAccurateCountsAndVersions()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]), cancellationToken);

        DiagnosticBundle bundle = await environment.Application
            .CollectDiagnosticBundleAsync(environment.ProjectRoot, cancellationToken);

        Assert.True(bundle.Project.Initialized);
        Assert.Equal(1, bundle.Project.SprintCount);
        Assert.NotEmpty(bundle.StartupChecks);
        Assert.Contains(bundle.Providers, provider => provider.Id == "fake");
        Assert.Equal(DiagnosticBundle.ContractVersion, bundle.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(bundle.ForgeVersion));
        Assert.False(string.IsNullOrWhiteSpace(bundle.ProtocolVersion));
        Assert.Empty(bundle.Omissions);
        Assert.True(bundle.EventLogIntegrity.Valid);
        // No route decision has ever been recorded for this sprint: the full budget is still there.
        Assert.Equal(RoutingLedger.DefaultRetryBudget, bundle.RetryBudget.Remaining);
        Assert.Empty(bundle.CircuitBreakers);
        // A real, isolated temp directory the test itself created -- both probes must succeed.
        Assert.All(bundle.WritableProbes, probe => Assert.True(probe.Writable, probe.Label));
        Assert.Contains(bundle.WritableProbes, probe => probe.Label == "project");
        Assert.Contains(bundle.WritableProbes, probe => probe.Label == "local_application_data");
        AssertSchemaValid(bundle);
    }

    /// <summary>`FileSprintEventLog` throws `InvalidDataException` reactively per record when
    /// something happens to touch a corrupt sprint (see its own read methods); this proves the
    /// collector's own proactive walk catches the same corruption and reports it, rather than
    /// letting it crash the whole bundle or silently pass event-log integrity as healthy. A corrupt
    /// sprint also breaks the ordinary project-snapshot pathway (it enumerates every sprint too, the
    /// same as `forge status`/`forge tree` would against this project) — that section is correctly
    /// omitted rather than asserted present, since nothing about this fixture proves the collector's
    /// own general omission behavior beyond what <see cref="CollectDiagnosticBundleAsyncOnAnInitializedProjectReportsAccurateCountsAndVersions"/>
    /// already covers for the healthy case.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CollectDiagnosticBundleAsyncDetectsACorruptSprintDefinition()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        await File.WriteAllTextAsync(definitionPath, "not valid json at all", cancellationToken);

        DiagnosticBundle bundle = await environment.Application
            .CollectDiagnosticBundleAsync(environment.ProjectRoot, cancellationToken);

        Assert.False(bundle.EventLogIntegrity.Valid);
        Assert.Equal(DiagnosticCodes.WorkflowLogCorrupted, bundle.EventLogIntegrity.DiagnosticCode);
        AssertSchemaValid(bundle);
    }

    private static void AssertSchemaValid(DiagnosticBundle bundle)
    {
        string json = StatusJson.Serialize(bundle);
        using System.Text.Json.JsonDocument instance = System.Text.Json.JsonDocument.Parse(json);
        EvaluationResults result = ContractSchemas.Load("diagnostic-bundle").Evaluate(
            instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }
}
