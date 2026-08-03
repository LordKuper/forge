namespace Forge.Updater.Windows;

public sealed record WindowsInstallationResult(
    bool Succeeded,
    string? VersionDirectory,
    UpdateDiagnostic Diagnostic)
{
    public static WindowsInstallationResult Failure(UpdateDiagnostic diagnostic) =>
        new(false, null, diagnostic);
}
