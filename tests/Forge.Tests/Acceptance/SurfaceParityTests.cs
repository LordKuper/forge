using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Cli;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Presentation;
using Forge.Tests.Support;
using Forge.UnitTests;

namespace Forge.AcceptanceTests;

public sealed class SurfaceParityTests
{
    /// <summary>The Desktop control that exposes each implemented capability.</summary>
    private static readonly Dictionary<string, string[]> DesktopControls = new(StringComparer.Ordinal)
    {
        [CapabilityIds.ProjectSnapshot] =
        [
            "StartupChecksLabel",
            "StatusLabel",
            "ProjectStateLabel",
            "SuggestedActionsLabel",
            "SprintsLabel",
            "SprintDetailsLabel",
            "SprintIdEntry",
        ],
        [CapabilityIds.ProjectInitialize] = ["InitializeButton", "ProjectRootEntry"],
        [CapabilityIds.ConfigurationManage] =
            ["ConfigurationScopePicker", "ConfigurationKeyEntry", "ConfigurationSetButton"],
        [CapabilityIds.ProviderHealth] = ["ProvidersLabel"],
    };

    [Fact]
    [Trait("Category", "Acceptance")]
    public void CliExposesEveryDocumentedCapabilityCommand()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        Assert.All(
            CapabilityIds.Implemented,
            id =>
            {
                string[] tokens = DocumentedCli(id);
                Command command = Assert.Single(
                    root.Subcommands,
                    subcommand => subcommand.Name == tokens[0]);
                foreach (string option in tokens.Where(token => token.StartsWith("--", StringComparison.Ordinal)))
                {
                    Assert.True(
                        HasOption(command, option),
                        $"'{command.Name}' does not expose '{option}'.");
                }

                foreach (string subcommand in Alternatives(tokens))
                {
                    Assert.Contains(command.Subcommands, item => item.Name == subcommand);
                }
            });
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void DesktopExposesEveryImplementedCapability()
    {
        string page = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Desktop",
            "MainPage.xaml"));

        Assert.All(
            CapabilityIds.Implemented,
            id => Assert.All(
                DesktopControls[id],
                control => Assert.Contains($"x:Name=\"{control}\"", page, StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void DesktopControlsAreWiredInCodeBehind()
    {
        // A declared control (checked above) that the code-behind never assigns is dead XAML — the
        // exact shape of the round-1 P8.83-88 bug, where ProvidersLabel existed nowhere at all and
        // a later fix could just as easily add the label without wiring it.
        string codeBehind = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Desktop",
            "MainPage.xaml.cs"));

        Assert.All(
            CapabilityIds.Implemented,
            id => Assert.All(
                DesktopControls[id],
                control => Assert.Contains(control, codeBehind, StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSprintTreeAndDetailForOneSnapshot()
    {
        // Sharing SurfaceFormatting is not by itself the no-drift guarantee this refactor claims:
        // either surface can still wrap, reorder, or filter the shared lines on its way to the
        // screen (the Desktop path already wraps them in Render(...) and trims). This compares the
        // two rendered projections of one project directly, so any such divergence fails here.
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken)).SprintId!;
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter tree = new(CultureInfo.InvariantCulture);
        StringWriter inspect = new(CultureInfo.InvariantCulture);
        string id = sprintId.Value.ToString("D", CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, tree, environment.Application)
            .Parse(["tree", "--project-root", environment.ProjectRoot, "--sprint", id])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, inspect, environment.Application)
            .Parse(["sprint", "inspect", id, "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        MainPageSnapshot desktop = await new MainPageViewModel(text, environment.Application)
            .RefreshAsync(environment.ProjectRoot, id, cancellationToken);

        // `forge tree` prefixes the project line WriteProject writes; the sprint sections after it
        // are what both surfaces share, so compare from the sprint title onwards.
        Assert.Equal(SprintSection(tree.ToString(), text.Resolve(MessageKeys.SprintsTitle)), desktop.SprintsText);
        Assert.Equal(
            SprintSection(inspect.ToString(), text.Resolve(MessageKeys.SprintDetailsTitle)),
            desktop.SprintDetailsText);
    }

    private static string SprintSection(string cliOutput, string title) =>
        cliOutput[cliOutput.IndexOf(title, StringComparison.Ordinal)..].TrimEnd();

    [Fact]
    [Trait("Category", "Acceptance")]
    public void ImplementedCapabilitiesDeclareBothSurfaces()
    {
        using JsonDocument contract = ReadCapabilities();
        Dictionary<string, JsonElement> capabilities = Index(contract);

        Assert.All(
            CapabilityIds.Implemented,
            id =>
            {
                JsonElement capability = capabilities[id];
                Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("cli").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("desktop").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("permission").GetString()));
            });
    }

    private static string[] DocumentedCli(string capabilityId)
    {
        using JsonDocument contract = ReadCapabilities();
        return Index(contract)[capabilityId]
            .GetProperty("cli")
            .GetString()!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(token => token.Trim('[', ']'))
            .ToArray();
    }

    private static IEnumerable<string> Alternatives(IEnumerable<string> tokens) =>
        tokens
            .Where(token => token.StartsWith('<') && token.EndsWith('>') && token.Contains('|', StringComparison.Ordinal))
            .SelectMany(token => token.Trim('<', '>').Split('|'));

    private static bool HasOption(Command command, string option) =>
        command.Options.Any(item => item.Name == option) ||
        command.Subcommands.Any(subcommand => HasOption(subcommand, option));

    private static JsonDocument ReadCapabilities() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "contracts",
            "v1",
            "capabilities.json")));

    private static Dictionary<string, JsonElement> Index(JsonDocument contract) =>
        contract.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
}
