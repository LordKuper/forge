using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;
using Forge.UnitTests;
using Json.Schema;

namespace Forge.AcceptanceTests;

public sealed class StartupCliTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorReportsTheOrderedStartupChecks()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["doctor", "--startup"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("user_configuration passed none", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "providers blocked provider_preflight_pending",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorWithoutTheStartupFlagOmitsTheCheckList()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["doctor"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("provider_preflight_pending", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task InitializationDisplaysTheRootAndRequiresConfirmation()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();

        int exitCode = await InvokeAsync(
            environment,
            output,
            ["init", "--project-root", environment.ProjectRoot]);

        Assert.Equal(ExitCodes.Confirmation, exitCode);
        Assert.Contains(environment.ProjectRoot, output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            catalog.Resolve(MessageKeys.InitConfirmationRequired),
            output.ToString(),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmedInitializationCreatesTheProjectTree()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(
            environment,
            output,
            ["init", "--project-root", environment.ProjectRoot, "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(ProjectRootResolver.ManifestPath(environment.ProjectRoot)));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task MachineStatusMatchesTheVersionedContract()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["status", "--json"]);

        Assert.Equal(0, exitCode);
        AssertValid("project-snapshot", output.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task MachineRecommendationsMatchTheVersionedContract()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(environment, output, ["next", "--json"]);

        Assert.Equal(0, exitCode);
        using JsonDocument actions = JsonDocument.Parse(output.ToString());
        foreach (JsonElement action in actions.RootElement.EnumerateArray())
        {
            AssertValid("suggested-action", action.GetRawText());
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task MachineEventsMatchTheVersionedContract()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);

        int exitCode = await InvokeAsync(
            environment, output, ["events", "--project-root", environment.ProjectRoot, "--json"]);

        Assert.Equal(0, exitCode);
        AssertValid("control-event-page", output.ToString());
        using JsonDocument page = JsonDocument.Parse(output.ToString());
        Assert.True(page.RootElement.GetProperty("events").GetArrayLength() > 0);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfigurationEditorReportsProvenanceAndRejectsWrongScope()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter error = new(CultureInfo.InvariantCulture);

        Assert.Equal(
            ExitCodes.Ok,
            await InvokeAsync(environment, output, ["config", "user", "language.ui", "ru"], error));
        Assert.Equal(
            ExitCodes.Configuration,
            await InvokeAsync(
                environment,
                output,
                ["config", "user", "artifacts.language.user_facing", "ru"],
                error));
        Assert.Equal(
            ExitCodes.Ok,
            await InvokeAsync(environment, output, ["config", "show"], error));

        Assert.Contains(DiagnosticCodes.ConfigurationScopeViolation, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            DiagnosticCodes.ConfigurationScopeViolation,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("language.ui = \"ru\" (user)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("language.llm = \"ru\" (inherited)", output.ToString(), StringComparison.Ordinal);
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

    private static void AssertValid(string schemaName, string json)
    {
        string schemaRoot = Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "contracts",
            "v1",
            "schemas");
        BuildOptions buildOptions = new()
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry(),
        };
        Dictionary<string, JsonSchema> schemas = Directory
            .GetFiles(schemaRoot, "*.schema.json")
            .ToDictionary(
                path => Path.GetFileName(path).Replace(".schema.json", string.Empty, StringComparison.Ordinal),
                path => JsonSchema.FromFile(path, buildOptions),
                StringComparer.Ordinal);
        using JsonDocument instance = JsonDocument.Parse(json);
        EvaluationResults result = schemas[schemaName].Evaluate(
            instance.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            });

        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }
}
