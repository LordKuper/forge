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
    UpdateInProgress,
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
    NoUpdateAvailable,
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
    string Sha256);

public sealed record StagedRelease(string Location, VerifiedRelease Release);

public sealed record ActivationReceipt(
    string ActivationId,
    string PreviousVersion,
    string ActivatedVersion,
    string? ExecutablePath = null);

public sealed record StageResult(bool Succeeded, StagedRelease? Staged, UpdateDiagnostic Diagnostic)
{
    public static StageResult Failure(string detail) =>
        new(false, null, new(UpdateDiagnosticCode.StagingFailed, detail));
}

public sealed record ActivationResult
{
    private ActivationResult(bool succeeded, ActivationReceipt? receipt, UpdateDiagnostic diagnostic)
    {
        Succeeded = succeeded;
        Receipt = receipt;
        Diagnostic = diagnostic;
    }

    public bool Succeeded { get; }

    public ActivationReceipt? Receipt { get; }

    public UpdateDiagnostic Diagnostic { get; }

    public static ActivationResult Success(ActivationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(true, receipt, UpdateDiagnostic.None);
    }

    public static ActivationResult Failure(string detail, ActivationReceipt? receipt = null) =>
        new(false, receipt, new(UpdateDiagnosticCode.ActivationFailed, detail));
}

public sealed record RollbackResult(bool Succeeded, UpdateDiagnostic Diagnostic)
{
    public static RollbackResult Failure(string detail) =>
        new(false, new(UpdateDiagnosticCode.RollbackFailed, detail));
}

public enum UpdateSurface
{
    Cli,
    Desktop,
}

public sealed record RestartIdentity(
    SemanticVersion Version,
    UpdateTarget Target,
    UpdateSurface Surface);

public sealed record RestartContext(
    string Token,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    RestartIdentity ExpectedIdentity);

public sealed record UpdateRequest(
    SemanticVersion CurrentVersion,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    UpdateSurface Surface)
{
    public IProgress<UpdateProgress>? Progress { get; init; }
}

public sealed record UpdateProgress(int Step, int TotalSteps, string Detail);

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

public sealed record UpdateLockResult(IAsyncDisposable? Lease, UpdateDiagnostic Diagnostic)
{
    public bool IsAcquired => Lease is not null && Diagnostic.Code == UpdateDiagnosticCode.None;
}

public interface IUpdateLock
{
    ValueTask<UpdateLockResult> AcquireAsync(
        UpdateTarget target,
        CancellationToken cancellationToken);
}

public interface IRestartTokenService
{
    RestartContext Create(UpdateRequest request, RestartIdentity expectedIdentity);

    bool Consume(string token, RestartIdentity actualIdentity);

    void Revoke(string token);
}

public interface IRestartTokenStore
{
    bool TryCreate(string token, RestartIdentity identity);

    bool TryConsume(string token, RestartIdentity identity);

    void Revoke(string token);

    bool Exists(string token);
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
