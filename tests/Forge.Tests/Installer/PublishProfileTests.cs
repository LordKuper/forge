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

    /// <summary>Covers every project <c>Publish-WindowsBundle.ps1</c> actually passes
    /// <c>--runtime win-x64</c>/<c>--runtime win-arm64</c> to (not the two projects
    /// <see cref="BothHostsDefineWindowsPublishProfile"/> happens to check, which are unrelated
    /// `.pubxml` files the script never reads) — a missing RID here breaks `dotnet publish
    /// --self-contained true` for that host outright. Round 1 review of PR #84 found the prior
    /// version of this test only checked <c>Forge.Cli.Windows</c>, leaving
    /// <c>Forge.Host.Windows</c> and <c>Forge.Desktop</c> unverified despite the plan item's own
    /// closure note claiming "already fully covered by the tests above".</summary>
    [Theory]
    [InlineData("Forge.Cli.Windows")]
    [InlineData("Forge.Host.Windows")]
    [InlineData("Forge.Desktop")]
    [Trait("Category", "Installer")]
    public void EveryPublishedHostDeclaresBothWindowsRuntimeIdentifiers(string host)
    {
        string project = File.ReadAllText(Path.Combine(FindRoot(), "src", host, $"{host}.csproj"));

        Assert.Contains("<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>", project, StringComparison.Ordinal);
    }

    /// <summary>Three release contracts nothing else guards, since no test executes the publisher and
    /// the release workflow only hashes whatever bundle it produces. Every host the product ships must
    /// be published into the bundle, or the released archive is missing an executable. The asset name is
    /// how <c>ReleaseAssetVerifier</c> resolves the asset it downloads, so renaming it breaks every
    /// update. A fixed 1980 entry timestamp, assigned to every entry walked in a
    /// path-ordered sequence, is what makes the archive byte-for-byte reproducible from a clean
    /// checkout (AGENTS.md, "Quality") and its published checksum therefore verifiable — so the
    /// timestamp's own value, the sort key, and the staging-relative entry name are pinned here, not
    /// just the statements that use them. The entry name matters as much as the other two: the
    /// staging directory carries a fresh <c>Guid</c> per run, so an entry name that keeps any of it
    /// changes the archive on every build.</summary>
    [Fact]
    [Trait("Category", "Installer")]
    public void BundlePublisherProducesTheContractedReproducibleArchive()
    {
        string script = File.ReadAllText(Path.Combine(FindRoot(), "build", "Publish-WindowsBundle.ps1"));

        Assert.Contains("Forge.Cli.Windows\\Forge.Cli.Windows.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Forge.Host.Windows\\Forge.Host.Windows.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Forge.Desktop\\Forge.Desktop.csproj", script, StringComparison.Ordinal);
        Assert.Contains("forge-windows-$($RuntimeIdentifier.Substring(4))-portable_bundle.zip", script, StringComparison.Ordinal);
        Assert.Contains("ZipArchiveMode]::Create", script, StringComparison.Ordinal);
        Assert.Contains(
            "$timestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Sort-Object { $_.FullName.Substring($stagingDirectory.Length).TrimStart('\\', '/') }",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$name = $_.FullName.Substring($stagingDirectory.Length).TrimStart('\\', '/') -replace '\\\\', '/'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$entry.LastWriteTime = $timestamp", script, StringComparison.Ordinal);
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
