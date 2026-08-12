using System.Text.Json;

namespace Forge.Updater;

/// <summary>
/// Version-pointer, staging, and archive orchestration shared by every platform whose release packaging is a
/// self-contained "extract a versioned directory" portable bundle. A platform strategy supplies only its OS-specific
/// host names and post-install steps; this type owns no OS-specific behavior.
/// </summary>
public sealed class PortableBundleActivation(string root)
{
    private readonly string root = Path.GetFullPath(root);

    public string VersionRoot => Path.Combine(root, "versions");

    public static async Task DownloadAndVerifyAsync(
        IReleaseAssetDownloader downloader,
        VerifiedRelease release,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(release);
        await using Stream source = await downloader.DownloadAsync(release.Asset, cancellationToken)
            .ConfigureAwait(false);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using System.Security.Cryptography.IncrementalHash hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
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
        output.Flush(true);
        if (size != release.Asset.Size ||
            !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.Cryptography.CryptographicException("The verified release changed before staging.");
        }
    }

    public static void ExtractArchive(string archive, string staging)
    {
        string fullStaging = Path.GetFullPath(staging);
        Directory.CreateDirectory(fullStaging);
        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(archive);
        foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(fullStaging, entry.FullName));
            if (!IsUnder(destination, fullStaging))
            {
                throw new InvalidDataException("The release package contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, destination, false);
        }
    }

    public string? ReadCurrentVersion()
    {
        string path = Path.Combine(root, "current.json");
        if (!File.Exists(path))
        {
            return null;
        }

        string? version = JsonSerializer.Deserialize<CurrentVersion>(File.ReadAllText(path))?.Version;
        if (!SemanticVersion.TryParse(version, out _))
        {
            throw new InvalidDataException("The current release pointer is invalid.");
        }

        return version;
    }

    public void WriteCurrentVersion(string version)
    {
        string path = Path.Combine(root, "current.json");
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new CurrentVersion(version));
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, $"{path}.previous", true);
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

    public void ClearCurrentVersion() => File.Delete(Path.Combine(root, "current.json"));

    public void ArchiveFailedVersion(string source, string version, string archiveId)
    {
        string failed = Path.Combine(root, "failed", $"{version}-{archiveId}");
        Directory.CreateDirectory(Path.GetDirectoryName(failed)!);
        Directory.Move(source, failed);
    }

    public static bool IsUnder(string path, string directory)
    {
        string rootPath = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed record CurrentVersion(string Version);
}
