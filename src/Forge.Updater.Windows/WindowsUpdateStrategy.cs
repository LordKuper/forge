using System.Diagnostics;
using System.Text.Json;

namespace Forge.Updater.Windows;

public interface IWindowsHostSelfTester
{
    ValueTask<bool> VerifyAsync(string executablePath, CancellationToken cancellationToken);
}

public sealed class WindowsHostSelfTester(TimeSpan? timeout = null) : IWindowsHostSelfTester
{
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(30);

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
        using CancellationTokenSource deadline = new(this.timeout);
        using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }

        return process.ExitCode == 0;
    }
}

/// <summary>
/// Windows-specific slice of an update: host self-test naming, command shim, desktop shortcut, and PATH
/// registration. Version-pointer tracking, staging, and archive orchestration are shared, OS-agnostic behavior
/// owned by <see cref="PortableBundleActivation"/>.
/// </summary>
public sealed class WindowsUpdateStrategy : IPlatformUpdateStrategy
{
    private static readonly string[] HostNames = ["forge.exe", "Forge.Desktop.exe"];
    private readonly IReleaseAssetDownloader downloader;
    private readonly string root;
    private readonly PortableBundleActivation activation;
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
        activation = new PortableBundleActivation(this.root);
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
        string destination = Path.Combine(activation.VersionRoot, version);
        try
        {
            string? current = activation.ReadCurrentVersion();
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
            DesktopShortcutSnapshot? shortcutSnapshot = null;
            try
            {
                shortcutSnapshot = desktopShortcut?.Capture();
                activation.WriteCurrentVersion(version);
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
                activation.ArchiveFailedVersion(destination, version, Guid.NewGuid().ToString("N"));
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
                activation.ArchiveFailedVersion(destination, version, Guid.NewGuid().ToString("N"));
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
        string staging = Path.Combine(activation.VersionRoot, $".staging-{Guid.NewGuid():N}");
        string archive = Path.Combine(root, $".download-{Guid.NewGuid():N}.zip");
        bool staged = false;
        try
        {
            Directory.CreateDirectory(activation.VersionRoot);
            await PortableBundleActivation.DownloadAndVerifyAsync(downloader, release, archive, cancellationToken).ConfigureAwait(false);
            PortableBundleActivation.ExtractArchive(archive, staging);
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
                PortableBundleActivation.DeleteDirectory(staging);
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
        string destination = Path.Combine(activation.VersionRoot, version);
        string activationId = Guid.NewGuid().ToString("N");
        string previous = string.Empty;
        ActivationReceipt? receipt = null;
        bool moved = false;
        try
        {
            if (!PortableBundleActivation.IsUnder(staged.Location, activation.VersionRoot) || !Directory.Exists(staged.Location))
            {
                return ValueTask.FromResult(ActivationResult.Failure("The staged release is not eligible for activation."));
            }

            previous = activation.ReadCurrentVersion() ?? string.Empty;
            if (string.IsNullOrEmpty(previous) || !Directory.Exists(Path.Combine(activation.VersionRoot, previous)))
            {
                PortableBundleActivation.DeleteDirectory(staged.Location);
                return ValueTask.FromResult(ActivationResult.Failure("No previous verified Windows release is available for rollback."));
            }

            if (Directory.Exists(destination))
            {
                activation.ArchiveFailedVersion(destination, version, $"{activationId}-displaced");
            }

            Directory.Move(staged.Location, destination);
            moved = true;
            receipt = new(
                activationId,
                previous,
                version,
                Path.Combine(destination, restart.ExpectedIdentity.Surface == UpdateSurface.Desktop ? "Forge.Desktop.exe" : "forge.exe"));
            activation.WriteCurrentVersion(version);
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
                    activation.WriteCurrentVersion(previous);
                    activation.ArchiveFailedVersion(destination, version, activationId);
                }
                catch (Exception recoveryException) when (recoveryException is IOException or UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(ActivationResult.Failure("Windows release activation recovery could not complete.", receipt));
                }
            }
            else
            {
                PortableBundleActivation.DeleteDirectory(staged.Location);
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
            !Directory.Exists(Path.Combine(activation.VersionRoot, receipt.PreviousVersion)))
        {
            return ValueTask.FromResult(RollbackResult.Failure("No previous verified Windows release is available for rollback."));
        }

        try
        {
            activation.WriteCurrentVersion(receipt.PreviousVersion);
            UpdateDiagnostic installationResult = EnsureInstalledSurfaces(Path.Combine(activation.VersionRoot, receipt.PreviousVersion));
            if (installationResult.Code != UpdateDiagnosticCode.None)
            {
                return ValueTask.FromResult(RollbackResult.Failure(installationResult.Detail));
            }

            string activated = Path.Combine(activation.VersionRoot, receipt.ActivatedVersion);
            if (Directory.Exists(activated))
            {
                activation.ArchiveFailedVersion(activated, receipt.ActivatedVersion, receipt.ActivationId);
            }

            return ValueTask.FromResult(new RollbackResult(true, UpdateDiagnostic.None));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(RollbackResult.Failure("Windows release rollback could not complete."));
        }
    }

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
}
