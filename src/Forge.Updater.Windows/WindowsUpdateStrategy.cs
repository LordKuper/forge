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
    private static readonly string[] HostNames = ["forge.exe", "Forge.Desktop.exe"];
    private readonly IReleaseAssetDownloader downloader;
    private readonly string root;
    private readonly IWindowsHostSelfTester selfTester;
    private readonly IWindowsUserPathRegistrar? pathRegistrar;
    private readonly IWindowsDesktopShortcut? desktopShortcut;

    public WindowsUpdateStrategy(
        IReleaseAssetDownloader downloader,
        string root,
        IWindowsHostSelfTester? selfTester = null,
        IWindowsUserPathRegistrar? pathRegistrar = null,
        IWindowsDesktopShortcut? desktopShortcut = null)
    {
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        this.selfTester = selfTester ?? new WindowsHostSelfTester();
        this.pathRegistrar = pathRegistrar;
        this.desktopShortcut = desktopShortcut;
    }

    public bool Supports(UpdateTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.Equals(target.OperatingSystem, "windows", StringComparison.Ordinal) &&
            string.Equals(target.Packaging, "portable_bundle", StringComparison.Ordinal) &&
            (string.Equals(target.Architecture, "x64", StringComparison.Ordinal) ||
             string.Equals(target.Architecture, "arm64", StringComparison.Ordinal));
    }

    public async ValueTask<WindowsInstallationResult> InstallAsync(
        VerifiedRelease release,
        UpdateTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(target);
        if (!Supports(target))
        {
            return WindowsInstallationResult.Failure(new(
                UpdateDiagnosticCode.PlatformNotSupported,
                "The release target is not supported by the Windows installer."));
        }

        string version = release.Version.ToString();
        string destination = Path.Combine(VersionRoot, version);
        try
        {
            string? current = ReadCurrentVersion();
            if (string.Equals(current, version, StringComparison.Ordinal) && Directory.Exists(destination))
            {
                return CompleteInstallation(destination);
            }

            if (current is not null || Directory.Exists(destination))
            {
                return WindowsInstallationResult.Failure(new(
                    UpdateDiagnosticCode.ActivationFailed,
                    "A different Forge installation already exists; use the updater instead."));
            }

            StageResult staged = await StageAsync(release, target, cancellationToken).ConfigureAwait(false);
            if (!staged.Succeeded)
            {
                return WindowsInstallationResult.Failure(staged.Diagnostic);
            }

            Directory.Move(staged.Staged!.Location, destination);
            DesktopShortcutSnapshot? shortcutSnapshot = desktopShortcut?.Capture();
            try
            {
                WriteCurrentVersion(version);
                WindowsInstallationResult installation = CompleteInstallation(destination);
                if (installation.Succeeded)
                {
                    return installation;
                }

                File.Delete(Path.Combine(root, "current.json"));
                WindowsCommandShim.Remove(root);
                if (shortcutSnapshot is not null)
                {
                    desktopShortcut!.Restore(shortcutSnapshot);
                }
                ArchiveFailedVersion(destination, version, Guid.NewGuid().ToString("N"));
                return installation;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                File.Delete(Path.Combine(root, "current.json"));
                WindowsCommandShim.Remove(root);
                if (shortcutSnapshot is not null)
                {
                    desktopShortcut!.Restore(shortcutSnapshot);
                }
                ArchiveFailedVersion(destination, version, Guid.NewGuid().ToString("N"));
                return WindowsInstallationResult.Failure(new(
                    UpdateDiagnosticCode.ActivationFailed,
                    "Windows installation activation could not complete."));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return WindowsInstallationResult.Failure(new(
                UpdateDiagnosticCode.ActivationFailed,
                "Windows installation could not complete."));
        }
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
            foreach (string hostName in HostNames)
            {
                string hostPath = Path.Combine(staging, hostName);
                if (!File.Exists(hostPath))
                {
                    return StageResult.Failure($"The release package has no {hostName} host.");
                }

                if (!await selfTester.VerifyAsync(hostPath, cancellationToken).ConfigureAwait(false))
                {
                    return StageResult.Failure($"The staged Forge host self-test failed: {hostName}.");
                }
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
        string previous = string.Empty;
        ActivationReceipt? receipt = null;
        bool moved = false;
        try
        {
            if (!IsUnder(staged.Location, VersionRoot) || !Directory.Exists(staged.Location))
            {
                return ValueTask.FromResult(ActivationResult.Failure("The staged release is not eligible for activation."));
            }

            previous = ReadCurrentVersion() ?? string.Empty;
            if (string.IsNullOrEmpty(previous) || !Directory.Exists(Path.Combine(VersionRoot, previous)))
            {
                DeleteDirectory(staged.Location);
                return ValueTask.FromResult(ActivationResult.Failure("No previous verified Windows release is available for rollback."));
            }

            if (Directory.Exists(destination))
            {
                ArchiveFailedVersion(destination, version, $"{activationId}-displaced");
            }

            Directory.Move(staged.Location, destination);
            moved = true;
            receipt = new(
                activationId,
                previous,
                version,
                Path.Combine(destination, restart.ExpectedIdentity.Surface == UpdateSurface.Desktop ? "Forge.Desktop.exe" : "forge.exe"));
            WriteCurrentVersion(version);
            UpdateDiagnostic installationResult = EnsureInstalledSurfaces(destination);
            if (installationResult.Code != UpdateDiagnosticCode.None)
            {
                return ValueTask.FromResult(ActivationResult.Failure(installationResult.Detail, receipt));
            }

            return ValueTask.FromResult(ActivationResult.Success(receipt));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (moved)
            {
                try
                {
                    WriteCurrentVersion(previous);
                    ArchiveFailedVersion(destination, version, activationId);
                }
                catch (Exception recoveryException) when (recoveryException is IOException or UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(ActivationResult.Failure("Windows release activation recovery could not complete.", receipt));
                }
            }
            else
            {
                DeleteDirectory(staged.Location);
            }

            return ValueTask.FromResult(ActivationResult.Failure("Windows release activation could not complete.", receipt));
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
            UpdateDiagnostic installationResult = EnsureInstalledSurfaces(Path.Combine(VersionRoot, receipt.PreviousVersion));
            if (installationResult.Code != UpdateDiagnosticCode.None)
            {
                return ValueTask.FromResult(RollbackResult.Failure(installationResult.Detail));
            }

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

    private WindowsInstallationResult CompleteInstallation(string destination)
    {
        UpdateDiagnostic installationResult = EnsureInstalledSurfaces(destination);
        return installationResult.Code == UpdateDiagnosticCode.None
            ? new(true, destination, UpdateDiagnostic.None)
            : WindowsInstallationResult.Failure(installationResult);
    }

    private UpdateDiagnostic EnsureInstalledSurfaces(string versionDirectory)
    {
        WindowsCommandShim.Ensure(root);
        UpdateDiagnostic shortcutResult = desktopShortcut?.Ensure(Path.Combine(versionDirectory, "Forge.Desktop.exe")) ?? UpdateDiagnostic.None;
        return shortcutResult.Code == UpdateDiagnosticCode.None
            ? pathRegistrar?.Ensure(Path.Combine(root, "current")) ?? UpdateDiagnostic.None
            : shortcutResult;
    }

    private void ArchiveFailedVersion(string source, string version, string archiveId)
    {
        string failed = Path.Combine(root, "failed", $"{version}-{archiveId}");
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
