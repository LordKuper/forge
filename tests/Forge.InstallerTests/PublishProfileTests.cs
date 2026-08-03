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
        foreach (string host in new[] { "Forge.Cli", "Forge.Desktop" })
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
    public void WindowsUpdateStrategyIsImplementedInTheApplication()
    {
        string root = FindRoot();
        string strategy = File.ReadAllText(Path.Combine(root, "src", "Forge.Updater.Windows", "WindowsUpdateStrategy.cs"));

        Assert.False(File.Exists(Path.Combine(root, "install.ps1")));
        Assert.Contains("DownloadAndVerifyAsync", strategy, StringComparison.Ordinal);
        Assert.Contains("File.Replace", strategy, StringComparison.Ordinal);
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
