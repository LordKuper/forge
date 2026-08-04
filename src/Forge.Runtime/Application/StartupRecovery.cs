using System.Globalization;
using Forge.Configuration;
using YamlDotNet.Core;

namespace Forge.Application;

public sealed record RecoverStartupResult(bool Succeeded, StartupCheckId? Check, string DiagnosticCode);

/// <summary>
/// Repairs the one failure class Forge owns: configuration that cannot be read at all. The file
/// is re-validated before anything is touched, so a readable file is never quarantined.
/// </summary>
public sealed class StartupRecovery(ScopedConfigurationStores stores, IEnvironmentPaths environment)
{
    public const string QuarantineSuffix = ".invalid";

    /// <summary>Only configuration failures have an in-app repair; other checks need the user.</summary>
    public static bool CanRecover(StartupCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Id is StartupCheckId.UserConfiguration or StartupCheckId.Language or
            StartupCheckId.ProjectConfiguration;
    }

    public async Task<RecoverStartupResult> RecoverAsync(
        StartupStatus startup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        StartupCheck? failure = startup.FirstFailure;
        if (failure is null)
        {
            return new(true, null, DiagnosticCodes.None);
        }

        if (!CanRecover(failure))
        {
            return new(false, failure.Id, failure.DiagnosticCode);
        }

        bool project = failure.Id == StartupCheckId.ProjectConfiguration;
        if (project && !startup.Project.Exists)
        {
            return new(false, failure.Id, failure.DiagnosticCode);
        }

        IConfigurationStore store = project ? stores.Project(startup.Project.Root) : stores.User;
        string path = project
            ? ConfigurationStoreFactory.ProjectPath(startup.Project.Root)
            : ConfigurationStoreFactory.UserPath(environment.LocalApplicationData);
        try
        {
            // A readable file is never the failure this repair owns.
            await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new(false, failure.Id, failure.DiagnosticCode);
        }
        catch (Exception error) when (IsUnreadable(error))
        {
            return Quarantine(path, failure.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, failure.Id, DiagnosticCodes.InternalError);
        }
    }

    private static RecoverStartupResult Quarantine(string path, StartupCheckId check)
    {
        try
        {
            // The retained previous file is quarantined too: the store already tried it.
            Move(path);
            Move($"{path}.previous");
            return new(true, check, DiagnosticCodes.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, check, DiagnosticCodes.InternalError);
        }
    }

    private static void Move(string path)
    {
        if (File.Exists(path))
        {
            File.Move(path, FreeQuarantinePath(path));
        }
    }

    /// <summary>Every quarantined revision is kept for diagnosis; none is overwritten.</summary>
    private static string FreeQuarantinePath(string path)
    {
        string candidate = $"{path}{QuarantineSuffix}";
        for (int ordinal = 1; File.Exists(candidate); ordinal++)
        {
            candidate = string.Create(
                CultureInfo.InvariantCulture,
                $"{path}{QuarantineSuffix}.{ordinal}");
        }

        return candidate;
    }

    private static bool IsUnreadable(Exception error) =>
        error is System.Text.Json.JsonException or YamlException or InvalidDataException or
            FormatException or ConfigurationScopeException;
}
