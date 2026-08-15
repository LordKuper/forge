namespace Forge.InstallerTests;

public sealed class PublishProfileTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [Trait("Category", "Installer")]
    public void BothHostsDefineWindowsPublishProfile(string runtimeIdentifier)
    {
        string root = FindRoot();
        foreach (string host in new[] { "Forge.Cli.Windows", "Forge.Desktop" })
        {
            string path = Path.Combine(
                root,
                "src",
                host,
                "Properties",
                "PublishProfiles",
                $"{runtimeIdentifier}.pubxml");
            Assert.True(File.Exists(path), $"Missing publish profile: {path}");
        }
    }

    [Fact]
    [Trait("Category", "Installer")]
    public void CliDeclaresBothWindowsRuntimeIdentifiers()
    {
        string project = File.ReadAllText(
            Path.Combine(FindRoot(), "src", "Forge.Cli.Windows", "Forge.Cli.Windows.csproj"));

        Assert.Contains("<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>", project, StringComparison.Ordinal);
    }

    /// <summary>The bundle asset name is a release contract: <c>ReleaseAssetVerifier</c> resolves the
    /// asset it downloads by exactly this name, so a publisher that renames it breaks every update.</summary>
    [Fact]
    [Trait("Category", "Installer")]
    public void BundlePublisherProducesTheContractedArchiveName()
    {
        string script = File.ReadAllText(Path.Combine(FindRoot(), "build", "Publish-WindowsBundle.ps1"));

        Assert.Contains("Forge.Cli.Windows\\Forge.Cli.Windows.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Forge.Desktop\\Forge.Desktop.csproj", script, StringComparison.Ordinal);
        Assert.Contains("forge-windows-$($RuntimeIdentifier.Substring(4))-portable_bundle.zip", script, StringComparison.Ordinal);
    }

    private static string FindRoot()
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
