namespace Forge.Updater;

/// <summary>The neutral result of a first-time platform install; adapters map their own result onto this.</summary>
public sealed record InstallationResult(bool Succeeded, string? InstalledPath, UpdateDiagnostic Diagnostic)
{
    public static InstallationResult Failure(UpdateDiagnostic diagnostic) => new(false, null, diagnostic);
}

/// <summary>Port a composed platform adapter implements so CLI/Desktop composition roots stay OS-agnostic.</summary>
public interface IPlatformInstaller
{
    ValueTask<InstallationResult> InstallLatestAsync(
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<InstallationResult> InstallLatestAsync(CancellationToken cancellationToken) =>
        InstallLatestAsync(null, cancellationToken);
}
