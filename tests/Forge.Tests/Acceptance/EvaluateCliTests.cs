using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Cli;
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
