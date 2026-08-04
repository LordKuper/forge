using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Application;

namespace Forge.Providers;

/// <summary>Everything a provider strategy needs to resolve, verify, and stage a release.</summary>
public sealed record ProviderInstallSpec(
    string DirectoryName,
    string Owner,
    string Repo,
    string ExecutableFileName,
    Func<string, string> AssetName,
    bool AssetIsZip,
    Version? MinimumVersion);

/// <summary>
/// Installs a provider CLI the same way Forge installs itself (ADR 0002): verify the release
/// against GitHub's own per-asset SHA-256 digest, stage into an immutable version directory, and
/// atomically switch a `current.json` pointer. One previous version is retained.
/// </summary>
public sealed class GitHubProviderInstaller(
    IProviderReleaseClient releaseClient,
    HttpClient downloadClient,
    IEnvironmentPaths paths)
{
    private readonly IProviderReleaseClient releaseClient =
        releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
    private readonly HttpClient downloadClient =
        downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));
    private readonly IEnvironmentPaths paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public string ProviderRoot(string directoryName) =>
        Path.Combine(paths.LocalApplicationData, "Forge", "providers", directoryName);

    public string? ReadCurrentVersion(string directoryName)
    {
        string path = Path.Combine(ProviderRoot(directoryName), "current.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CurrentVersion>(File.ReadAllText(path))?.Version;
    }

    public string ExecutablePath(string directoryName, string version, string executableFileName) =>
        Path.Combine(ProviderRoot(directoryName), "versions", version, executableFileName);

    public async Task<ProviderStatus> InstallOrUpdateAsync(
        ProviderKind kind,
        ProviderInstallSpec spec,
        string architecture,
        CancellationToken cancellationToken)
    {
        try
        {
            ProviderRelease? release = await releaseClient
                .GetLatestAsync(spec.Owner, spec.Repo, cancellationToken)
                .ConfigureAwait(false);
            if (release is null || !Version.TryParse(release.Version, out Version? version))
            {
                return new(kind, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
            }

            if (spec.MinimumVersion is { } minimum && version < minimum)
            {
                return new(kind, ProviderState.Failed, release.Version, ProviderDiagnosticCodes.VersionUnsupported);
            }

            string assetName = spec.AssetName(architecture);
            ProviderReleaseAsset? asset = SingleOrDefault(release.Assets, assetName);
            if (asset?.Sha256 is not { Length: 64 } expectedHash)
            {
                return new(kind, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
            }

            string root = ProviderRoot(spec.DirectoryName);
            string versionRoot = Path.Combine(root, "versions");
            string destination = Path.Combine(versionRoot, release.Version);
            Directory.CreateDirectory(versionRoot);
            if (!File.Exists(Path.Combine(destination, spec.ExecutableFileName)))
            {
                await StageAsync(asset, expectedHash, destination, spec, cancellationToken).ConfigureAwait(false);
            }

            string? previous = ReadCurrentVersion(spec.DirectoryName);
            WriteCurrentVersion(root, release.Version);
            Prune(versionRoot, keep: [release.Version, previous]);
            return ProviderStatus.Ready(kind, release.Version);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                CryptographicException or HttpRequestException or JsonException)
        {
            return new(kind, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
        }
    }

    private static ProviderReleaseAsset? SingleOrDefault(IReadOnlyList<ProviderReleaseAsset> assets, string name) =>
        assets.SingleOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));

    private async Task StageAsync(
        ProviderReleaseAsset asset,
        string expectedHash,
        string destination,
        ProviderInstallSpec spec,
        CancellationToken cancellationToken)
    {
        string staging = $"{destination}.staging-{Guid.NewGuid():N}";
        string download = $"{destination}.download-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(staging);
            await DownloadAndVerifyAsync(asset, expectedHash, download, cancellationToken).ConfigureAwait(false);
            if (spec.AssetIsZip)
            {
                ExtractZip(download, staging);
            }
            else
            {
                File.Copy(download, Path.Combine(staging, spec.ExecutableFileName));
            }

            if (!File.Exists(Path.Combine(staging, spec.ExecutableFileName)))
            {
                throw new InvalidDataException(
                    $"The release package has no {spec.ExecutableFileName} executable.");
            }

            Directory.Move(staging, destination);
        }
        finally
        {
            if (File.Exists(download))
            {
                File.Delete(download);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    private async Task DownloadAndVerifyAsync(
        ProviderReleaseAsset asset,
        string expectedHash,
        string destination,
        CancellationToken cancellationToken)
    {
        await using Stream source = await downloadClient
            .GetStreamAsync(asset.DownloadUri, cancellationToken)
            .ConfigureAwait(false);
        await using (FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                size += read;
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (size != asset.Size ||
                !string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()),
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException(
                    "The downloaded provider asset does not match the verified checksum.");
            }
        }
    }

    private static void ExtractZip(string archive, string staging)
    {
        string fullStaging = Path.GetFullPath(staging);
        string prefix = fullStaging + Path.DirectorySeparatorChar;
        using ZipArchive zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string destination = Path.GetFullPath(Path.Combine(fullStaging, entry.FullName));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The release package contains an unsafe path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void WriteCurrentVersion(string root, string version)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "current.json");
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new CurrentVersion(version));
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Retains only the active and immediately previous version, mirroring self-update.</summary>
    private static void Prune(string versionRoot, IReadOnlyCollection<string?> keep)
    {
        if (!Directory.Exists(versionRoot))
        {
            return;
        }

        foreach (string directory in Directory.GetDirectories(versionRoot))
        {
            string name = Path.GetFileName(directory);
            if (!keep.Contains(name))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed record CurrentVersion(string Version);
}
