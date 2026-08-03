namespace Forge.Updater;

public sealed class ForgeSelfUpdater(
    IUpdateTargetDetector targetDetector,
    PlatformUpdateStrategyResolver strategyResolver,
    IUpdateLock updateLock,
    IForgeReleaseClient releaseClient,
    IReleaseVerifier releaseVerifier,
    IRestartTokenService restartTokens,
    IRestartCoordinator restartCoordinator) : IForgeSelfUpdater
{
    private readonly IUpdateTargetDetector targetDetector = targetDetector ?? throw new ArgumentNullException(nameof(targetDetector));
    private readonly PlatformUpdateStrategyResolver strategyResolver = strategyResolver ?? throw new ArgumentNullException(nameof(strategyResolver));
    private readonly IUpdateLock updateLock = updateLock ?? throw new ArgumentNullException(nameof(updateLock));
    private readonly IForgeReleaseClient releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
    private readonly IReleaseVerifier releaseVerifier = releaseVerifier ?? throw new ArgumentNullException(nameof(releaseVerifier));
    private readonly IRestartTokenService restartTokens = restartTokens ?? throw new ArgumentNullException(nameof(restartTokens));
    private readonly IRestartCoordinator restartCoordinator = restartCoordinator ?? throw new ArgumentNullException(nameof(restartCoordinator));

    public async ValueTask<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        UpdateTarget target = targetDetector.Detect();
        StrategyResolution resolution = strategyResolver.Resolve(target);
        if (!resolution.IsSuccess)
        {
            return Failed(resolution.Diagnostic);
        }

        UpdateLockResult lockResult = await updateLock.AcquireAsync(target, cancellationToken).ConfigureAwait(false);
        if (!lockResult.IsAcquired)
        {
            return Failed(lockResult.Diagnostic);
        }

        await using IAsyncDisposable updateLease = lockResult.Lease!;
        ReleaseLookupResult lookup = await releaseClient.GetLatestStableAsync(
            request.CurrentVersion,
            cancellationToken).ConfigureAwait(false);
        if (!lookup.IsUpdateAvailable)
        {
            if (lookup.Diagnostic.Code == UpdateDiagnosticCode.NoUpdateAvailable)
            {
                return new(UpdateLifecycleState.NoUpdateAvailable, UpdateDiagnostic.None, request.CurrentVersion);
            }

            return Failed(lookup.Diagnostic);
        }

        VerificationResult verification = await releaseVerifier.VerifyAsync(
            lookup.Release!,
            target,
            cancellationToken).ConfigureAwait(false);
        if (!verification.Succeeded)
        {
            return Failed(verification.Diagnostic);
        }

        IPlatformUpdateStrategy strategy = resolution.Strategy!;
        StageResult staged = await strategy.StageAsync(
            verification.Release!,
            target,
            cancellationToken).ConfigureAwait(false);
        if (!staged.Succeeded)
        {
            return Failed(staged.Diagnostic);
        }

        RestartIdentity restartIdentity = new(verification.Release!.Version, target, request.Surface);
        RestartContext restart = restartTokens.Create(request, restartIdentity);
        ActivationResult activated = await strategy.ActivateAsync(
            staged.Staged!,
            restart,
            cancellationToken).ConfigureAwait(false);
        if (!activated.Succeeded)
        {
            restartTokens.Revoke(restart.Token);
            return await RollbackIfNeededAsync(strategy, activated.Receipt, activated.Diagnostic, CancellationToken.None).ConfigureAwait(false);
        }

        UpdateDiagnostic restartResult;
        try
        {
            RestartContext launch = activated.Receipt!.ExecutablePath is { Length: > 0 } executablePath
                ? restart with { ExecutablePath = executablePath }
                : restart;
            restartResult = await restartCoordinator.RestartAsync(launch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            restartResult = new(UpdateDiagnosticCode.RestartFailed, "Restart coordination did not complete.");
        }

        if (restartResult.Code != UpdateDiagnosticCode.None)
        {
            restartTokens.Revoke(restart.Token);
            return await RollbackIfNeededAsync(strategy, activated.Receipt, restartResult, CancellationToken.None).ConfigureAwait(false);
        }

        return new(UpdateLifecycleState.RestartRequested, UpdateDiagnostic.None, verification.Release!.Version);
    }

    private static UpdateResult Failed(UpdateDiagnostic diagnostic) =>
        new(UpdateLifecycleState.Failed, diagnostic);

    private static async ValueTask<UpdateResult> RollbackIfNeededAsync(
        IPlatformUpdateStrategy strategy,
        ActivationReceipt? receipt,
        UpdateDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        if (receipt is null)
        {
            return Failed(diagnostic);
        }

        RollbackResult rollback = await strategy.RollbackAsync(receipt, cancellationToken).ConfigureAwait(false);
        return rollback.Succeeded
            ? new(UpdateLifecycleState.RolledBack, diagnostic, RollbackAttempted: true)
            : new(UpdateLifecycleState.Failed, rollback.Diagnostic, RollbackAttempted: true);
    }
}
