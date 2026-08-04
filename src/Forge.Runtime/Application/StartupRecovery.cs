using Forge.Configuration;

namespace Forge.Application;

public sealed record RecoverStartupResult(bool Succeeded, StartupCheckId? Check, string DiagnosticCode);

/// <summary>
/// Repairs the one failure class Forge owns: unreadable configuration. The invalid file is
/// quarantined for diagnosis and the retained previous file is restored when it exists.
/// </summary>
public sealed class StartupRecovery(IEnvironmentPaths environment)
{
    public const string QuarantineSuffix = ".invalid";

    public RecoverStartupResult Recover(StartupStatus startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        StartupCheck? failure = startup.FirstFailure;
        if (failure is null)
        {
            return new(true, null, DiagnosticCodes.None);
        }

        string? path = failure.Id switch
        {
            StartupCheckId.UserConfiguration or StartupCheckId.Language =>
                ConfigurationStoreFactory.UserPath(environment.LocalApplicationData),
            StartupCheckId.ProjectConfiguration when startup.Project.Exists =>
                ConfigurationStoreFactory.ProjectPath(startup.Project.Root),
            _ => null,
        };
        if (path is null)
        {
            return new(false, failure.Id, DiagnosticCodes.RecoveryUnavailable);
        }

        try
        {
            Quarantine(path);
            return new(true, failure.Id, DiagnosticCodes.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, failure.Id, DiagnosticCodes.InternalError);
        }
    }

    private static void Quarantine(string path)
    {
        if (File.Exists(path))
        {
            File.Move(path, $"{path}{QuarantineSuffix}", true);
        }

        string previous = $"{path}.previous";
        if (File.Exists(previous))
        {
            File.Copy(previous, path, true);
        }
    }
}
