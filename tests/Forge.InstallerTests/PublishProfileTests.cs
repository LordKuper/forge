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
    public void InstallerRequiresVerifiedStagingAndAnAtomicCurrentPointer()
    {
        string installer = File.ReadAllText(Path.Combine(FindRoot(), "install.ps1"));

        Assert.Contains("gh attestation verify", installer, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", installer, StringComparison.Ordinal);
        Assert.Contains(".staging-", installer, StringComparison.Ordinal);
        Assert.Contains("--self-test", installer, StringComparison.Ordinal);
        Assert.Contains("File]::Replace", installer, StringComparison.Ordinal);
        Assert.Contains("Add-ForgePath", installer, StringComparison.Ordinal);
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
