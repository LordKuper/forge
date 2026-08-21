using Forge.Application;
using Forge.Configuration;
using Forge.Providers;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.UnitTests;

/// <summary>
/// <see cref="ForgeApplication.RunEvaluationAsync"/> (`forge eval`, ADR 0042). Each area reuses an
/// existing command's own logic (<see cref="StartupPipeline"/> for updater/provider/bootstrap,
/// <see cref="ModelPolicyGate"/> for the model-policy area) — these tests prove the report reflects
/// that underlying state correctly and stays schema-conforming, not that new probing logic exists.
/// </summary>
public sealed class EvaluationTests
{
    private static readonly string[] ViolatingModelPolicy = ["codex:some-other-model"];


    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunEvaluationAsyncOnAnUninitializedProjectReportsBootstrapBlocked()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        EvaluationReport report = await environment.Application
            .RunEvaluationAsync(environment.ProjectRoot, cancellationToken);

        Assert.Equal(EvaluationState.Blocked, report.State);
        Assert.Contains(
            report.Checks,
            check => check.Area == EvaluationArea.Bootstrap && check.State == EvaluationState.Blocked);
        // Model policy still evaluates the enabled/registered providers directly — it does not
        // depend on project initialization, matching a dry-run report requiring no sprint.
        Assert.Contains(report.Checks, check => check.Area == EvaluationArea.ModelPolicy);
        Assert.Contains(report.Checks, check => check.Area == EvaluationArea.Workflow);
        AssertSchemaValid(report);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunEvaluationAsyncOnAnInitializedProjectWithNoPolicyReportsEveryAreaPassing()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);

        EvaluationReport report = await environment.Application
            .RunEvaluationAsync(environment.ProjectRoot, cancellationToken);

        Assert.Equal(EvaluationState.Passed, report.State);
        // "updater/release" is deliberately Skipped, not Passed: ADR 0042 reuses
        // StartupPipeline.RunAsync verbatim, and the release check is always deferred to
        // `forge update`'s own on-demand lifecycle (Stage 2) regardless of caller. Skipped does
        // not move the aggregate state off Passed, matching StartupState.Aggregate's own rule.
        Assert.All(
            report.Checks,
            check => Assert.True(
                check.State is EvaluationState.Passed or EvaluationState.Skipped,
                $"{check.Area}/{check.Name} was {check.State}."));
        EvaluationCheck modelPolicy = Assert.Single(
            report.Checks, check => check.Area == EvaluationArea.ModelPolicy);
        Assert.Equal("codex", modelPolicy.Name);
        AssertSchemaValid(report);
    }

    // ADR 0042: models.allowed_models restricts codex to a model FakeLlmProvider never resolves —
    // the ModelPolicy area must report the violation without touching any other area.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunEvaluationAsyncReportsAModelPolicyViolationWithoutFailingOtherAreas()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "models.allowed_models",
            System.Text.Json.JsonSerializer.SerializeToElement(ViolatingModelPolicy),
            cancellationToken);
        Assert.True(configured.Succeeded);

        EvaluationReport report = await environment.Application
            .RunEvaluationAsync(environment.ProjectRoot, cancellationToken);

        Assert.Equal(EvaluationState.Failed, report.State);
        EvaluationCheck modelPolicy = Assert.Single(
            report.Checks, check => check.Area == EvaluationArea.ModelPolicy);
        Assert.Equal(EvaluationState.Failed, modelPolicy.State);
        Assert.Equal(DiagnosticCodes.ModelPolicyViolation, modelPolicy.DiagnosticCode);
        Assert.DoesNotContain(
            report.Checks,
            check => check.Area != EvaluationArea.ModelPolicy && check.State == EvaluationState.Failed);
        AssertSchemaValid(report);
    }

    private static void AssertSchemaValid(EvaluationReport report)
    {
        string json = StatusJson.Serialize(report);
        using System.Text.Json.JsonDocument instance = System.Text.Json.JsonDocument.Parse(json);
        EvaluationResults result = ContractSchemas.Load("evaluation-result").Evaluate(
            instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }
}
