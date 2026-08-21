using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Cli;
using Forge.Configuration;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.AcceptanceTests;

/// <summary>`forge eval` (ADR 0042): pass/fail evaluation of the updater, provider, bootstrap, and
/// workflow subsystems plus the model-policy gate, as JSON. <see cref="Forge.UnitTests.EvaluationTests"/>
/// already proves <see cref="ForgeApplication.RunEvaluationAsync"/> itself is correct and
/// schema-conforming; these tests prove the CLI wiring reaches it, matching
/// `DoctorBundleCliTests`'s own precedent.</summary>
public sealed class EvaluateCliTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task EvalEmitsASchemaConformingReportAndExitsOkWhenEveryCheckPasses()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken)).Succeeded);
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["eval"]);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        EvaluationResults result = ContractSchemas.Load("evaluation-result").Evaluate(
            document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, output.ToString());
        Assert.Equal("passed", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task EvalReportsBlockedBootstrapBeforeTheProjectIsInitializedWithoutFailingTheExitCode()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["eval"]);

        // An uninitialized project blocks bootstrap (state "blocked"), but ExitCodes.Ok is still
        // returned -- only a Failed check moves the exit code, matching `doctor --startup`'s own
        // FirstFailure-only convention (see CreateEvaluateCommand's own doc comment).
        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("blocked", document.RootElement.GetProperty("state").GetString());
    }

    // Round 1 review of PR #87: both prior tests only ever asserted exit 0, so mutating the
    // Failed -> Report(...) branch to always `return ExitCodes.Ok;` would have survived the whole
    // suite despite the CreateEvaluateCommand doc comment's own promise. A real Failed check (a
    // model-policy violation) is the only way to prove the non-zero path actually fires.
    private static readonly string[] ViolatingModelPolicy = ["codex:some-other-model"];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task EvalExitsNonZeroWithTheViolationDiagnosticWhenAModelPolicyCheckFails()
    {
        using TestEnvironment environment = new(
            llmProviders: [new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0")],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken)).Succeeded);
        Assert.True((await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "models.allowed_models",
            JsonSerializer.SerializeToElement(ViolatingModelPolicy),
            cancellationToken)).Succeeded);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter error = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["eval"], error);

        Assert.Equal(ExitCodes.Workflow, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("failed", document.RootElement.GetProperty("state").GetString());
        Assert.Contains(DiagnosticCodes.ModelPolicyViolation, error.ToString());
    }

    private static Task<int> InvokeAsync(
        TestEnvironment environment,
        TextWriter output,
        string[] arguments,
        TextWriter? error = null) =>
        CliApplication
            .CreateRootCommand(
                new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.CurrentUICulture),
                output,
                environment.Application,
                error)
            .Parse(arguments)
            .InvokeAsync(new InvocationConfiguration { Output = output }, TestContext.Current.CancellationToken);
}
