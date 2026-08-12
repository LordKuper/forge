using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Cli;
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
            ["StartupChecksLabel", "StatusLabel", "ProjectStateLabel", "SuggestedActionsLabel"],
        [CapabilityIds.ProjectInitialize] = ["InitializeButton", "ProjectRootEntry"],
        [CapabilityIds.ConfigurationManage] =
            ["ConfigurationScopePicker", "ConfigurationKeyEntry", "ConfigurationSetButton"],
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
