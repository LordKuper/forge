using Forge.Application;

namespace Forge.Cli;

/// <summary>
/// Maps diagnostic codes to the exit-code categories frozen in
/// `docs/contracts/v1/README.md`.
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 2;
    public const int Configuration = 3;
    public const int Project = 4;
    public const int Platform = 5;
    public const int Update = 6;
    public const int Provider = 7;
    public const int Authorization = 8;
    public const int Confirmation = 9;
    public const int Concurrency = 10;
    public const int Workflow = 11;
    public const int Internal = 13;

    public static int For(string diagnosticCode) => diagnosticCode switch
    {
        DiagnosticCodes.None or DiagnosticCodes.ProjectAlreadyInitialized => Ok,
        DiagnosticCodes.ConfigurationKeyUnknown or DiagnosticCodes.ProjectRootNotAbsolute or
            DiagnosticCodes.SprintNotFound or DiagnosticCodes.SupersessionInstructionTooLong or
            DiagnosticCodes.SupersessionInstructionUnreadable or
            DiagnosticCodes.SupersessionInstructionRequired or
            DiagnosticCodes.ConfirmationEvidenceKindInvalid or DiagnosticCodes.ConfirmationTextRequired or
            DiagnosticCodes.TestWorkJustificationRequired => Usage,
        DiagnosticCodes.ConfigurationScopeViolation or DiagnosticCodes.ConfigurationInvalid =>
            Configuration,
        DiagnosticCodes.ProjectNotInitialized or DiagnosticCodes.ProjectDirectoryUnknown or
            DiagnosticCodes.ProjectRootMissing => Project,
        DiagnosticCodes.PlatformNotSupported => Platform,
        DiagnosticCodes.UpdateCheckDeferred => Update,
        DiagnosticCodes.ProviderPreflightPending or DiagnosticCodes.ProviderUpdateFailed or
            DiagnosticCodes.IntegrationLanguageUnsupported or DiagnosticCodes.IntegrationPartiallyRefused =>
            Provider,
        DiagnosticCodes.PermissionDenied => Authorization,
        DiagnosticCodes.ConfirmationRequired => Confirmation,
        DiagnosticCodes.SuggestionStale or DiagnosticCodes.ControlCursorStale => Concurrency,
        DiagnosticCodes.ModelPolicyViolation or DiagnosticCodes.NoActiveOperation or
            DiagnosticCodes.ActiveOperationChanged => Workflow,
        _ => Internal,
    };
}
