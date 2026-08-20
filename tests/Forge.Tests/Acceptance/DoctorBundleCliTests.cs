using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.AcceptanceTests;

/// <summary>`forge doctor --bundle` (ADR 0005/0038): allowlisted, redacted operational evidence as
/// JSON. `DiagnosticBundleCollectorTests` (Unit) already proves the collector itself is correct and
/// schema-conforming against a <see cref="TestEnvironment"/> project directly; these tests prove the
/// CLI wiring reaches it and prints exactly that JSON, matching every other CLI command's own
/// `InvokeAsync`-through-`CliApplication.CreateRootCommand` pattern.</summary>
public sealed class DoctorBundleCliTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorBundleEmitsASchemaConformingBundleForAnInitializedProject()
    {
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["doctor", "--bundle"]);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        EvaluationResults result = ContractSchemas.Load("diagnostic-bundle").Evaluate(
            document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, output.ToString());
        Assert.True(document.RootElement.GetProperty("project").GetProperty("initialized").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("project").GetProperty("sprint_count").GetInt32());
    }

    /// <summary>Round 1 review of PR #79: the original version of this test asserted a marker never
    /// appeared without ever planting that marker anywhere the collector could reach, so it passed
    /// vacuously and proved nothing about the "a collection failure omits that section rather than
    /// leaking anything about it" design. A corrupt sprint definition containing sensitive-looking
    /// text is the concrete leak vector this proves closed: `CollectEventLogIntegrityAsync` catches
    /// `InvalidDataException` by type only and reports a fixed
    /// <see cref="DiagnosticCodes.WorkflowLogCorrupted"/> code, never the exception's own message
    /// (which -- via `FileSprintEventLog`'s own wrapping -- would otherwise carry this file's raw
    /// content back out).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorBundleNeverLeaksTheContentOfACorruptSprintFile()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        const string marker = "PROMPT_SECRET_MARKER sk-live-not-a-real-key";
        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        await File.WriteAllTextAsync(definitionPath, marker, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["doctor", "--bundle"]);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("event_log_integrity").GetProperty("valid").GetBoolean());
        Assert.Equal(
            DiagnosticCodes.WorkflowLogCorrupted,
            document.RootElement.GetProperty("event_log_integrity").GetProperty("diagnostic_code").GetString());
        Assert.DoesNotContain(marker, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorBundleWorksBeforeTheProjectIsInitialized()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["doctor", "--bundle"]);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("project").GetProperty("initialized").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("omissions").EnumerateArray());
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
