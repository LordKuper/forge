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

    [Fact]
    [Trait("Category", "Installer")]
    public void BundlePublisherProducesTheVerifiedArchiveLayout()
    {
        string script = File.ReadAllText(Path.Combine(FindRoot(), "build", "Publish-WindowsBundle.ps1"));

        Assert.Contains("Forge.Cli.Windows\\Forge.Cli.Windows.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Forge.Desktop\\Forge.Desktop.csproj", script, StringComparison.Ordinal);
        Assert.Contains("forge-windows-$($RuntimeIdentifier.Substring(4))-portable_bundle.zip", script, StringComparison.Ordinal);
        Assert.Contains("ZipArchiveMode]::Create", script, StringComparison.Ordinal);
        Assert.Contains("LastWriteTime", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Installer")]
    public void WindowsUpdateStrategyIsImplementedInTheApplication()
    {
        string root = FindRoot();
        string strategy = File.ReadAllText(Path.Combine(root, "src", "Forge.Updater.Windows", "WindowsUpdateStrategy.cs"));
        string activation = File.ReadAllText(Path.Combine(root, "src", "Forge.Updater", "PortableBundleActivation.cs"));

        Assert.False(File.Exists(Path.Combine(root, "install.ps1")));
        Assert.Contains("DownloadAndVerifyAsync", strategy, StringComparison.Ordinal);
        Assert.Contains("File.Replace", activation, StringComparison.Ordinal);
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
