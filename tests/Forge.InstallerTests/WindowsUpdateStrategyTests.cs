using System.IO.Compression;
using System.Security.Cryptography;
using Forge.Updater;
using Forge.Updater.Windows;

namespace Forge.InstallerTests;

public sealed class WindowsUpdateStrategyTests
{
    [Fact]
    [Trait("Category", "Installer")]
    public async Task InstallsAVerifiedBundleAndIsIdempotent()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        CountingDownloader downloader = new(archive);
        RecordingShortcut shortcut = new();
        WindowsUpdateStrategy strategy = new(downloader, temporary.Path, new PassingSelfTester(), desktopShortcut: shortcut);
        UpdateTarget target = new("windows", "x64", "portable_bundle");

        WindowsInstallationResult first = await strategy.InstallAsync(Release(archive), target, TestContext.Current.CancellationToken);
        WindowsInstallationResult second = await strategy.InstallAsync(Release(archive), target, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal(Path.Combine(temporary.Path, "versions", "1.1.0"), first.VersionDirectory);
        Assert.True(File.Exists(Path.Combine(first.VersionDirectory!, "forge.exe")));
        Assert.Contains("1.1.0", File.ReadAllText(Path.Combine(temporary.Path, "current.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(temporary.Path, "current", "forge.cmd")));
        Assert.True(File.Exists(Path.Combine(temporary.Path, "current", "forge.ps1")));
        Assert.Equal(Path.Combine(first.VersionDirectory!, "Forge.Desktop.exe"), shortcut.ExecutablePath);
        Assert.True(second.Succeeded);
        Assert.Equal(1, downloader.DownloadCount);
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task DoesNotOverwriteAnUnknownVersionDirectory()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        string existing = Path.Combine(temporary.Path, "versions", "1.1.0");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());

        WindowsInstallationResult result = await strategy.InstallAsync(
            Release(archive),
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(existing, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "current.json")));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task SerializesConcurrentUpdates()
    {
        UpdateTarget target = new("windows", "x64", "portable_bundle");
        WindowsUpdateLock firstLock = new(TimeSpan.FromMilliseconds(100));
        WindowsUpdateLock secondLock = new(TimeSpan.FromMilliseconds(100));

        UpdateLockResult first = await firstLock.AcquireAsync(target, TestContext.Current.CancellationToken);
        UpdateLockResult blocked = await secondLock.AcquireAsync(target, TestContext.Current.CancellationToken);
        await first.Lease!.DisposeAsync();
        UpdateLockResult next = await secondLock.AcquireAsync(target, TestContext.Current.CancellationToken);

        Assert.True(first.IsAcquired);
        Assert.Equal(UpdateDiagnosticCode.UpdateInProgress, blocked.Diagnostic.Code);
        Assert.True(next.IsAcquired);
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Installer")]
    public void AddsTheCurrentDirectoryToUserPathOnlyOnce()
    {
        string current = "C:\\Users\\test\\AppData\\Local\\Forge\\current";
        string? path = "C:\\Tools";
        WindowsUserPathRegistrar registrar = new(() => path, value => path = value);

        UpdateDiagnostic first = registrar.Ensure(current);
        UpdateDiagnostic second = registrar.Ensure(current);

        Assert.Equal(UpdateDiagnosticCode.None, first.Code);
        Assert.Equal(UpdateDiagnosticCode.None, second.Code);
        Assert.Equal("C:\\Tools;C:\\Users\\test\\AppData\\Local\\Forge\\current", path);
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task StagesActivatesAndRollsBackVerifiedBundle()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        VerifiedRelease release = Release(archive);
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());
        UpdateTarget target = new("windows", "x64", "portable_bundle");
        string previous = Path.Combine(temporary.Path, "versions", "1.0.0");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(temporary.Path, "current.json"), "{\"Version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(temporary.Path, "config.json"), "{\"language.ui\":\"ru\"}");

        StageResult staged = await strategy.StageAsync(release, target, TestContext.Current.CancellationToken);
        ActivationResult activated = await strategy.ActivateAsync(
            staged.Staged!,
            new RestartContext("token", "forge.exe", [], temporary.Path, new(release.Version, target, UpdateSurface.Cli)),
            TestContext.Current.CancellationToken);
        RollbackResult rollback = await strategy.RollbackAsync(activated.Receipt!, TestContext.Current.CancellationToken);
        StageResult retryStaged = await strategy.StageAsync(release, target, TestContext.Current.CancellationToken);
        ActivationResult retryActivated = await strategy.ActivateAsync(
            retryStaged.Staged!,
            new RestartContext("token", "forge.exe", [], temporary.Path, new(release.Version, target, UpdateSurface.Cli)),
            TestContext.Current.CancellationToken);

        Assert.True(staged.Succeeded);
        Assert.True(activated.Succeeded);
        Assert.True(rollback.Succeeded);
        Assert.True(retryActivated.Succeeded);
        Assert.True(File.Exists(Path.Combine(temporary.Path, "failed", "1.1.0-" + activated.Receipt!.ActivationId, "forge.exe")));
        Assert.Contains("1.1.0", File.ReadAllText(Path.Combine(temporary.Path, "current.json")), StringComparison.Ordinal);
        Assert.Equal("{\"language.ui\":\"ru\"}", File.ReadAllText(Path.Combine(temporary.Path, "config.json")));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task RejectsAChangedBundleBeforeExtraction()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        VerifiedRelease release = Release(archive) with { Sha256 = Convert.ToHexString(SHA256.HashData([1])) };
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());

        StageResult result = await strategy.StageAsync(
            release,
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.GetDirectories(Path.Combine(temporary.Path, "versions")));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task ArchivesDisplacedAndRolledBackBundlesSeparately()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        VerifiedRelease release = Release(archive);
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());
        UpdateTarget target = new("windows", "x64", "portable_bundle");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "versions", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(temporary.Path, "versions", "1.1.0"));
        File.WriteAllText(Path.Combine(temporary.Path, "current.json"), "{\"Version\":\"1.0.0\"}");

        StageResult staged = await strategy.StageAsync(release, target, TestContext.Current.CancellationToken);
        ActivationResult activated = await strategy.ActivateAsync(
            staged.Staged!,
            new RestartContext("token", "forge.exe", [], temporary.Path, new(release.Version, target, UpdateSurface.Cli)),
            TestContext.Current.CancellationToken);
        RollbackResult rollback = await strategy.RollbackAsync(activated.Receipt!, TestContext.Current.CancellationToken);

        Assert.True(rollback.Succeeded);
        Assert.True(Directory.Exists(Path.Combine(temporary.Path, "failed", $"1.1.0-{activated.Receipt!.ActivationId}-displaced")));
        Assert.True(Directory.Exists(Path.Combine(temporary.Path, "failed", $"1.1.0-{activated.Receipt.ActivationId}")));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task RejectsBundleWhenHostSelfTestFails()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new FailingSelfTester());

        StageResult result = await strategy.StageAsync(
            Release(archive),
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.GetDirectories(Path.Combine(temporary.Path, "versions")));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task RejectsBundleWithoutDesktopHost()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path, includeDesktop: false);
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());

        StageResult result = await strategy.StageAsync(
            Release(archive),
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.GetDirectories(Path.Combine(temporary.Path, "versions")));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task RejectsInvalidCurrentVersionDuringActivation()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        VerifiedRelease release = Release(archive);
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());
        File.WriteAllText(Path.Combine(temporary.Path, "current.json"), "{\"Version\":\"not-a-version\"}");

        StageResult staged = await strategy.StageAsync(
            release,
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);
        ActivationResult result = await strategy.ActivateAsync(
            staged.Staged!,
            new RestartContext("token", "forge.exe", [], temporary.Path, new(release.Version, new("windows", "x64", "portable_bundle"), UpdateSurface.Cli)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(staged.Staged!.Location));
    }

    [Fact]
    [Trait("Category", "Installer")]
    public async Task RejectsActivationWithoutARestorableCurrentBundle()
    {
        using TemporaryDirectory temporary = new();
        byte[] archive = CreateBundle(temporary.Path);
        VerifiedRelease release = Release(archive);
        WindowsUpdateStrategy strategy = new(new MemoryDownloader(archive), temporary.Path, new PassingSelfTester());
        File.WriteAllText(Path.Combine(temporary.Path, "current.json"), "{\"Version\":\"1.0.0\"}");

        StageResult staged = await strategy.StageAsync(
            release,
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);
        ActivationResult result = await strategy.ActivateAsync(
            staged.Staged!,
            new RestartContext("token", "forge.exe", [], temporary.Path, new(release.Version, new("windows", "x64", "portable_bundle"), UpdateSurface.Cli)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(staged.Staged!.Location));
    }

    private static VerifiedRelease Release(byte[] archive) =>
        new(
            SemanticVersion.Parse("1.1.0"),
            new Uri("https://example.test/release"),
            new("forge-windows-x64-portable_bundle.zip", archive.Length, new Uri("https://example.test/asset")),
            Convert.ToHexString(SHA256.HashData(archive)),
            "provenance.intoto.jsonl");

    private static byte[] CreateBundle(string root, bool includeDesktop = true)
    {
        string source = Path.Combine(root, "bundle");
        string archive = Path.Combine(root, "bundle.zip");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "forge.exe"), "test host");
        if (includeDesktop)
        {
            File.WriteAllText(Path.Combine(source, "Forge.Desktop.exe"), "test desktop host");
        }

        ZipFile.CreateFromDirectory(source, archive);
        return File.ReadAllBytes(archive);
    }

    private sealed class MemoryDownloader(byte[] contents) : IReleaseAssetDownloader
    {
        public ValueTask<Stream> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(contents, writable: false));
    }

    private sealed class CountingDownloader(byte[] contents) : IReleaseAssetDownloader
    {
        public int DownloadCount { get; private set; }

        public ValueTask<Stream> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken)
        {
            DownloadCount++;
            return ValueTask.FromResult<Stream>(new MemoryStream(contents, writable: false));
        }
    }

    private sealed class PassingSelfTester : IWindowsHostSelfTester
    {
        public ValueTask<bool> VerifyAsync(string executablePath, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class FailingSelfTester : IWindowsHostSelfTester
    {
        public ValueTask<bool> VerifyAsync(string executablePath, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class RecordingShortcut : IWindowsDesktopShortcut
    {
        public string? ExecutablePath { get; private set; }

        public UpdateDiagnostic Ensure(string executablePath)
        {
            ExecutablePath = executablePath;
            return UpdateDiagnostic.None;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"forge-installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
