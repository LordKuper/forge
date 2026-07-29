using System.Xml.Linq;
using Forge.Configuration;

namespace Forge.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    public void DomainHasNoProjectDependencies()
    {
        string project = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Domain",
            "Forge.Domain.csproj");
        Assert.Empty(ProjectReferences(project));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void PresentationDoesNotReferenceInfrastructure()
    {
        string project = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Presentation",
            "Forge.Presentation.csproj");

        Assert.DoesNotContain(
            ProjectReferences(project),
            reference => reference.Contains("Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void UpdaterCoreDoesNotReferencePlatformProjects()
    {
        string project = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Updater",
            "Forge.Updater.csproj");

        Assert.DoesNotContain(
            ProjectReferences(project),
            reference => reference.Contains("Windows", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void HostsDoNotContainHardCodedLabelText()
    {
        string root = RepositoryRoot.Find();
        string[] xamlFiles =
            Directory.GetFiles(Path.Combine(root, "src", "Forge.Desktop"), "*.xaml", SearchOption.AllDirectories);

        Assert.DoesNotContain(
            xamlFiles.SelectMany(File.ReadLines),
            line => line.Contains("Text=\"", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void HostsDoNotBypassConfigurationStores()
    {
        string sourceRoot = Path.Combine(RepositoryRoot.Find(), "src");
        string[] boundaryProjects =
        [
            "Forge.Bootstrap",
            "Forge.Cli",
            "Forge.Desktop",
            "Forge.Presentation",
        ];
        string[] bypasses = boundaryProjects
            .SelectMany(project => Directory.GetFiles(
                Path.Combine(sourceRoot, project),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => File.ReadAllText(path).Contains(
                "File.WriteAllText",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bypasses);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ArtifactVersionMatchesCanonicalVersion()
    {
        string expected = $"{File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "VERSION")).Trim()}.0";
        string actual = typeof(ConfigurationRegistry).Assembly.GetName().Version!.ToString();

        Assert.Equal(expected, actual);
    }

    private static IEnumerable<string> ProjectReferences(string project) =>
        XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(item => (string)item.Attribute("Include")!);
}

internal static class RepositoryRoot
{
    public static string Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Forge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the Forge repository root.");
    }
}
