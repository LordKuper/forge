using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProviderInstallerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReleaseClientExtractsVersionAndDigestRegardlessOfTagPrefix()
    {
        using HttpClient client = new(new FakeHttpMessageHandler(_ => Json(ReleaseJson(
            "rust-v0.146.0",
            [("codex-x86_64-pc-windows-msvc.exe", 10, "https://example.invalid/codex.exe", "sha256:" + new string('a', 64))]))));
        GitHubProviderReleaseClient sut = new(client);

        ProviderRelease? release = await sut.GetLatestAsync(
            "openai",
            "codex",
            TestContext.Current.CancellationToken);

        Assert.Equal("0.146.0", release!.Version);
        Assert.Equal(new string('a', 64), release.Assets.Single().Sha256);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReleaseClientReturnsNullForANonSuccessResponse()
    {
        using HttpClient client = new(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        GitHubProviderReleaseClient sut = new(client);

        ProviderRelease? release = await sut.GetLatestAsync(
            "openai",
            "codex",
            TestContext.Current.CancellationToken);

        Assert.Null(release);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateStagesVerifiedRawExecutableAndWritesCurrentPointer()
    {
        byte[] payload = "codex-binary"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        Uri assetUri = new("https://example.invalid/codex-x86_64-pc-windows-msvc.exe");
        ProviderRelease release = new(
            "0.146.0",
            [new("codex-x86_64-pc-windows-msvc.exe", payload.Length, assetUri, hash)]);
        using TestEnvironment environment = new();
        using HttpClient download = new(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        GitHubProviderInstaller installer = new(new FixedReleaseClient(release), download, environment);

        ProviderStatus status = await installer.InstallOrUpdateAsync(
            ProviderKind.Codex,
            CodexSpec(),
            "x64",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
        Assert.True(File.Exists(installer.ExecutablePath("codex", "0.146.0", "codex.exe")));
        Assert.Equal("0.146.0", installer.ReadCurrentVersion("codex"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateFailsAndLeavesNoCurrentPointerOnDigestMismatch()
    {
        byte[] payload = "codex-binary"u8.ToArray();
        string wrongHash = Convert.ToHexString(SHA256.HashData("different"u8.ToArray())).ToLowerInvariant();
        Uri assetUri = new("https://example.invalid/codex-x86_64-pc-windows-msvc.exe");
        ProviderRelease release = new(
            "0.146.0",
            [new("codex-x86_64-pc-windows-msvc.exe", payload.Length, assetUri, wrongHash)]);
        using TestEnvironment environment = new();
        using HttpClient download = new(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        GitHubProviderInstaller installer = new(new FixedReleaseClient(release), download, environment);

        ProviderStatus status = await installer.InstallOrUpdateAsync(
            ProviderKind.Codex,
            CodexSpec(),
            "x64",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
        Assert.Null(installer.ReadCurrentVersion("codex"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateFailsWithoutDownloadingWhenGitHubPublishesNoDigestForTheAsset()
    {
        ProviderRelease release = new(
            "0.146.0",
            [new(
                "codex-x86_64-pc-windows-msvc.exe",
                1,
                new("https://example.invalid/codex-x86_64-pc-windows-msvc.exe"),
                null)]);
        using TestEnvironment environment = new();
        using HttpClient download = new(new FakeHttpMessageHandler(
            _ => throw new InvalidOperationException("An unverifiable asset must never be downloaded.")));
        GitHubProviderInstaller installer = new(new FixedReleaseClient(release), download, environment);

        ProviderStatus status = await installer.InstallOrUpdateAsync(
            ProviderKind.Codex,
            CodexSpec(),
            "x64",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateExtractsTheExecutableFromAZipAsset()
    {
        byte[] zip = ZipWithExecutable("claude.exe", "claude-binary"u8.ToArray());
        string hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        Uri assetUri = new("https://example.invalid/claude-win32-x64.zip");
        ProviderRelease release = new("2.1.221", [new("claude-win32-x64.zip", zip.Length, assetUri, hash)]);
        using TestEnvironment environment = new();
        using HttpClient download = new(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));
        GitHubProviderInstaller installer = new(new FixedReleaseClient(release), download, environment);

        ProviderStatus status = await installer.InstallOrUpdateAsync(
            ProviderKind.ClaudeCode,
            ClaudeSpec(),
            "x64",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.True(File.Exists(installer.ExecutablePath("claude-code", "2.1.221", "claude.exe")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRejectsAReleaseBelowTheMinimumVersionWithoutDownloading()
    {
        ProviderRelease release = new(
            "1.9.0",
            [new(
                "claude-win32-x64.zip",
                1,
                new("https://example.invalid/claude-win32-x64.zip"),
                new string('a', 64))]);
        using TestEnvironment environment = new();
        using HttpClient download = new(new FakeHttpMessageHandler(
            _ => throw new InvalidOperationException("A release below the minimum version must never be downloaded.")));
        GitHubProviderInstaller installer = new(new FixedReleaseClient(release), download, environment);

        ProviderStatus status = await installer.InstallOrUpdateAsync(
            ProviderKind.ClaudeCode,
            ClaudeSpec(),
            "x64",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.VersionUnsupported, status.DiagnosticCode);
        Assert.Null(installer.ReadCurrentVersion("claude-code"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRetainsOnlyTheActiveAndPreviousVersion()
    {
        using TestEnvironment environment = new();
        MutableReleaseClient releaseClient = new();
        byte[] payload = "codex-binary"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using HttpClient download = new(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        GitHubProviderInstaller installer = new(releaseClient, download, environment);
        string versionRoot =
            Path.Combine(environment.LocalApplicationData, "Forge", "providers", "codex", "versions");

        foreach (string version in new[] { "0.1.0", "0.2.0", "0.3.0" })
        {
            releaseClient.Release = new(
                version,
                [new(
                    "codex-x86_64-pc-windows-msvc.exe",
                    payload.Length,
                    new($"https://example.invalid/{version}.exe"),
                    hash)]);
            await installer.InstallOrUpdateAsync(
                ProviderKind.Codex,
                CodexSpec(),
                "x64",
                TestContext.Current.CancellationToken);
        }

        string[] remaining = Directory
            .GetDirectories(versionRoot)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;
        Assert.Equal(["0.2.0", "0.3.0"], remaining);
    }

    private static ProviderInstallSpec CodexSpec() => new(
        "codex",
        "openai",
        "codex",
        "codex.exe",
        _ => "codex-x86_64-pc-windows-msvc.exe",
        AssetIsZip: false,
        MinimumVersion: null);

    private static ProviderInstallSpec ClaudeSpec() => new(
        "claude-code",
        "anthropics",
        "claude-code",
        "claude.exe",
        _ => "claude-win32-x64.zip",
        AssetIsZip: true,
        MinimumVersion: new Version(2, 0, 0));

    private static byte[] ZipWithExecutable(string entryName, byte[] contents)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            using Stream entryStream = entry.Open();
            entryStream.Write(contents);
        }

        return stream.ToArray();
    }

    private static string ReleaseJson(string tag, (string Name, long Size, string Url, string Digest)[] assets) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            assets = assets.Select(asset => new
            {
                name = asset.Name,
                size = asset.Size,
                browser_download_url = asset.Url,
                digest = asset.Digest,
            }),
        });

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FixedReleaseClient(ProviderRelease release) : IProviderReleaseClient
    {
        public Task<ProviderRelease?> GetLatestAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken) =>
            Task.FromResult<ProviderRelease?>(release);
    }

    private sealed class MutableReleaseClient : IProviderReleaseClient
    {
        public ProviderRelease? Release { get; set; }

        public Task<ProviderRelease?> GetLatestAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken) =>
            Task.FromResult(Release);
    }
}
