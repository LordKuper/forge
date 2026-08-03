using System.Diagnostics;
using System.Text.Json;

namespace Forge.Updater.Windows;

public interface IWindowsHostSelfTester
{
    ValueTask<bool> VerifyAsync(string executablePath, CancellationToken cancellationToken);
}

public sealed class WindowsHostSelfTester : IWindowsHostSelfTester
{
    public async ValueTask<bool> VerifyAsync(string executablePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--self-test");
        using Process process = new() { StartInfo = startInfo };
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return process.ExitCode == 0;
    }
}

public sealed class WindowsUpdateStrategy : IPlatformUpdateStrategy
{
    private readonly IReleaseAssetDownloader downloader;
    private readonly string root;
    private readonly IWindowsHostSelfTester selfTester;

    public WindowsUpdateStrategy(
        IReleaseAssetDownloader downloader,
        string root,
        IWindowsHostSelfTester? selfTester = null)
    {
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        this.selfTester = selfTester ?? new WindowsHostSelfTester();
    }

    public bool Supports(UpdateTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.Equals(target.OperatingSystem, "windows", StringComparison.Ordinal) &&
            string.Equals(target.Packaging, "portable_bundle", StringComparison.Ordinal) &&
            (string.Equals(target.Architecture, "x64", StringComparison.Ordinal) ||
             string.Equals(target.Architecture, "arm64", StringComparison.Ordinal));
    }

    public async ValueTask<StageResult> StageAsync(
        VerifiedRelease release,
        UpdateTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(target);
        string staging = Path.Combine(VersionRoot, $".staging-{Guid.NewGuid():N}");
        string archive = Path.Combine(root, $".download-{Guid.NewGuid():N}.zip");
        bool staged = false;
        try
        {
            Directory.CreateDirectory(VersionRoot);
            await DownloadAndVerifyAsync(release, archive, cancellationToken).ConfigureAwait(false);
            ExtractArchive(archive, staging);
            if (!File.Exists(Path.Combine(staging, "forge.exe")))
            {
                return StageResult.Failure("The release package has no Forge CLI host.");
            }

            if (!await selfTester.VerifyAsync(Path.Combine(staging, "forge.exe"), cancellationToken).ConfigureAwait(false))
            {
                return StageResult.Failure("The staged Forge CLI host self-test failed.");
            }

            staged = true;
            return new(true, new(staging, release), UpdateDiagnostic.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            return StageResult.Failure("Windows release staging could not complete.");
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            if (!staged)
            {
                DeleteDirectory(staging);
            }
        }
    }

    public ValueTask<ActivationResult> ActivateAsync(
        StagedRelease staged,
        RestartContext restart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(restart);
        cancellationToken.ThrowIfCancellationRequested();
        string version = staged.Release.Version.ToString();
        string destination = Path.Combine(VersionRoot, version);
        string activationId = Guid.NewGuid().ToString("N");
        bool moved = false;
        try
        {
            if (!IsUnder(staged.Location, VersionRoot) || !Directory.Exists(staged.Location))
            {
                return ValueTask.FromResult(ActivationResult.Failure("The staged release is not eligible for activation."));
            }

            string previous = ReadCurrentVersion() ?? string.Empty;
            if (Directory.Exists(destination))
            {
                ArchiveFailedVersion(destination, version, activationId);
            }

            Directory.Move(staged.Location, destination);
            moved = true;
            WriteCurrentVersion(version);
            return ValueTask.FromResult(ActivationResult.Success(new(activationId, previous, version)));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (moved)
            {
                try
                {
                    ArchiveFailedVersion(destination, version, activationId);
                }
                catch (Exception recoveryException) when (recoveryException is IOException or UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(ActivationResult.Failure("Windows release activation recovery could not complete."));
                }
            }

            return ValueTask.FromResult(ActivationResult.Failure("Windows release activation could not complete."));
        }
    }

    public ValueTask<RollbackResult> RollbackAsync(
        ActivationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SemanticVersion.TryParse(receipt.PreviousVersion, out _) ||
            !SemanticVersion.TryParse(receipt.ActivatedVersion, out _) ||
            !Directory.Exists(Path.Combine(VersionRoot, receipt.PreviousVersion)))
        {
            return ValueTask.FromResult(RollbackResult.Failure("No previous verified Windows release is available for rollback."));
        }

        try
        {
            WriteCurrentVersion(receipt.PreviousVersion);
            string activated = Path.Combine(VersionRoot, receipt.ActivatedVersion);
            if (Directory.Exists(activated))
            {
                ArchiveFailedVersion(activated, receipt.ActivatedVersion, receipt.ActivationId);
            }

            return ValueTask.FromResult(new RollbackResult(true, UpdateDiagnostic.None));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(RollbackResult.Failure("Windows release rollback could not complete."));
        }
    }

    private string VersionRoot => Path.Combine(root, "versions");

    private void ArchiveFailedVersion(string source, string version, string activationId)
    {
        string failed = Path.Combine(root, "failed", $"{version}-{activationId}");
        Directory.CreateDirectory(Path.GetDirectoryName(failed)!);
        Directory.Move(source, failed);
    }

    private async Task DownloadAndVerifyAsync(
        VerifiedRelease release,
        string destination,
        CancellationToken cancellationToken)
    {
        await using Stream source = await downloader.DownloadAsync(release.Asset, cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
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

    private static void ExtractArchive(string archive, string staging)
    {
        string fullStaging = Path.GetFullPath(staging);
        string prefix = fullStaging + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(fullStaging);
        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(archive);
        foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(fullStaging, entry.FullName));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

    private string? ReadCurrentVersion()
    {
        string path = Path.Combine(root, "current.json");
        if (!File.Exists(path))
        {
            return null;
        }

        string? version = JsonSerializer.Deserialize<CurrentVersion>(File.ReadAllText(path))?.Version;
        if (!SemanticVersion.TryParse(version, out _))
        {
            throw new InvalidDataException("The current Windows release pointer is invalid.");
        }

        return version;
    }

    private void WriteCurrentVersion(string version)
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

    private static bool IsUnder(string path, string directory)
    {
        string rootPath = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed record CurrentVersion(string Version);
}
