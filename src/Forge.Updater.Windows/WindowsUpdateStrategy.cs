namespace Forge.Updater.Windows;

public sealed class WindowsUpdateStrategy : IPlatformUpdateStrategy
{
    public bool Supports(UpdateTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.Equals(target.OperatingSystem, "windows", StringComparison.Ordinal) &&
            string.Equals(target.Packaging, "portable_bundle", StringComparison.Ordinal) &&
            (string.Equals(target.Architecture, "x64", StringComparison.Ordinal) ||
             string.Equals(target.Architecture, "arm64", StringComparison.Ordinal));
    }

    public ValueTask<StageResult> StageAsync(
        VerifiedRelease release,
        UpdateTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(StageResult.Failure("Windows staging is implemented in Stage 3."));
    }

    public ValueTask<ActivationResult> ActivateAsync(
        StagedRelease staged,
        RestartContext restart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(restart);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ActivationResult.Failure("Windows activation is implemented in Stage 3."));
    }

    public ValueTask<RollbackResult> RollbackAsync(
        ActivationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RollbackResult.Failure("Windows rollback is implemented in Stage 3."));
    }
}
