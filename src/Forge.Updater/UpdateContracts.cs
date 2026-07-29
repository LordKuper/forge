namespace Forge.Updater;

public sealed record UpdateTarget
{
    public UpdateTarget(string operatingSystem, string architecture, string packaging)
    {
        OperatingSystem = Normalize(operatingSystem);
        Architecture = Normalize(architecture);
        Packaging = Normalize(packaging);
    }

    public string OperatingSystem { get; }

    public string Architecture { get; }

    public string Packaging { get; }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }
}

public enum UpdateDiagnosticCode
{
    None,
    PlatformNotSupported,
    InvalidComposition,
    NoUpdateAvailable,
    ReleaseUnavailable,
    ReleaseRejected,
    VerificationFailed,
    StagingFailed,
    ActivationFailed,
    RestartFailed,
    HandshakeFailed,
    RollbackFailed,
}

public enum UpdateLifecycleState
{
    Idle,
    StrategyResolved,
    ReleaseVerified,
    Staged,
    Activated,
    RestartRequested,
    RolledBack,
    Failed,
}

public sealed record UpdateDiagnostic(UpdateDiagnosticCode Code, string Detail)
{
    public static UpdateDiagnostic None { get; } = new(UpdateDiagnosticCode.None, string.Empty);
}

public sealed record ReleaseAsset(string Name, long Size, Uri DownloadUri);

public sealed record ReleaseMetadata(
    SemanticVersion Version,
    Uri ReleaseUri,
    bool IsDraft,
    bool IsPrerelease,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record VerifiedRelease(
    SemanticVersion Version,
    Uri ReleaseUri,
    ReleaseAsset Asset,
    string Sha256,
    string ProvenanceBundleName);

public sealed record StagedRelease(string Location, VerifiedRelease Release);

public sealed record ActivationReceipt(string ActivationId, string PreviousVersion, string ActivatedVersion);

public sealed record StageResult(bool Succeeded, StagedRelease? Staged, UpdateDiagnostic Diagnostic)
{
    public static StageResult Failure(string detail) =>
        new(false, null, new(UpdateDiagnosticCode.StagingFailed, detail));
}

public sealed record ActivationResult(bool Succeeded, ActivationReceipt? Receipt, UpdateDiagnostic Diagnostic)
{
    public static ActivationResult Failure(string detail, ActivationReceipt? receipt = null) =>
        new(false, receipt, new(UpdateDiagnosticCode.ActivationFailed, detail));
}

public sealed record RollbackResult(bool Succeeded, UpdateDiagnostic Diagnostic)
{
    public static RollbackResult Failure(string detail) =>
        new(false, new(UpdateDiagnosticCode.RollbackFailed, detail));
}

public sealed record RestartContext(
    string Token,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record UpdateRequest(
    SemanticVersion CurrentVersion,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record UpdateResult(
    UpdateLifecycleState State,
    UpdateDiagnostic Diagnostic,
    SemanticVersion? Version = null,
    bool RollbackAttempted = false);

public interface IUpdateTargetDetector
{
    UpdateTarget Detect();
}

public interface IPlatformUpdateStrategy
{
    bool Supports(UpdateTarget target);

    ValueTask<StageResult> StageAsync(
        VerifiedRelease release,
        UpdateTarget target,
        CancellationToken cancellationToken);

    ValueTask<ActivationResult> ActivateAsync(
        StagedRelease staged,
        RestartContext restart,
        CancellationToken cancellationToken);

    ValueTask<RollbackResult> RollbackAsync(
        ActivationReceipt receipt,
        CancellationToken cancellationToken);
}

public interface IForgeReleaseClient
{
    ValueTask<ReleaseLookupResult> GetLatestStableAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken);
}

public interface IReleaseVerifier
{
    ValueTask<VerificationResult> VerifyAsync(
        ReleaseMetadata release,
        UpdateTarget target,
        CancellationToken cancellationToken);
}

public interface IRestartTokenService
{
    RestartContext Create(UpdateRequest request);

    bool Consume(string token);
}

public interface IRestartCoordinator
{
    ValueTask<UpdateDiagnostic> RestartAsync(
        RestartContext restart,
        CancellationToken cancellationToken);
}

public interface IForgeSelfUpdater
{
    ValueTask<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken);
}
