using System.Text.Json;
using Forge.Presentation;
using Forge.UnitTests;

namespace Forge.AcceptanceTests;

public sealed class SurfaceParityTests
{
    private static readonly string[] SharedOperations =
    [
        "GetStartupStatusAsync",
        "GetProjectStatusAsync",
        "InitializeProjectAsync",
        "GetUserConfigurationAsync",
        "SetConfigurationAsync",
    ];

    [Fact]
    [Trait("Category", "Acceptance")]
    public void BothSurfacesDispatchTheSharedApplicationOperations()
    {
        string cli = ReadSources("Forge.Cli");
        string desktop = ReadSources("Forge.Desktop");

        Assert.All(
            SharedOperations,
            operation =>
            {
                Assert.Contains(operation, cli, StringComparison.Ordinal);
                Assert.Contains(operation, desktop, StringComparison.Ordinal);
            });
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void ImplementedCapabilitiesDeclareBothSurfaces()
    {
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "contracts",
            "v1",
            "capabilities.json")));
        Dictionary<string, JsonElement> capabilities = contract.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

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

    private static string ReadSources(string project) =>
        string.Concat(Directory
            .GetFiles(
                Path.Combine(RepositoryRoot.Find(), "src", project),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
}
