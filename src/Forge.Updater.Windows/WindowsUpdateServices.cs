using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Updater.Windows;

public static class WindowsUpdateServices
{
    public static IServiceCollection AddForgeWindowsUpdater(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge");
        services.AddSingleton<IReleaseAssetDownloader, HttpReleaseAssetDownloader>();
        services.AddSingleton<IReleaseApi, GitHubReleaseApi>();
        services.AddSingleton<IForgeReleaseClient, ForgeReleaseClient>();
        services.AddSingleton<IReleaseVerifier>(provider => new ReleaseAssetVerifier(
            new("forge-{0}-{1}-{2}.zip", "checksums.txt"),
            provider.GetRequiredService<IReleaseAssetDownloader>()));
        services.AddSingleton<IUpdateTargetDetector, RuntimeUpdateTargetDetector>();
        services.AddSingleton<IUpdateLock, WindowsUpdateLock>();
        services.AddSingleton<IWindowsUserPathRegistrar, WindowsUserPathRegistrar>();
        services.AddSingleton<IWindowsDesktopShortcut, WindowsDesktopShortcut>();
        services.AddSingleton<IRestartTokenStore>(_ => new FileRestartTokenStore(Path.Combine(root, "restart")));
        services.AddSingleton<IRestartTokenService, RestartTokenService>();
        services.AddSingleton<IRestartCoordinator, WindowsRestartCoordinator>();
        services.AddSingleton<IForgeSelfUpdater, ForgeSelfUpdater>();
        services.AddSingleton(provider => new WindowsUpdateStrategy(
            provider.GetRequiredService<IReleaseAssetDownloader>(),
            root,
            pathRegistrar: provider.GetRequiredService<IWindowsUserPathRegistrar>(),
            desktopShortcut: provider.GetRequiredService<IWindowsDesktopShortcut>()));
        services.AddSingleton<IPlatformUpdateStrategy>(provider => provider.GetRequiredService<WindowsUpdateStrategy>());
        services.AddSingleton<PlatformUpdateStrategyResolver>();
        services.Replace(ServiceDescriptor
            .Singleton<Forge.Application.IPlatformPreflight, WindowsPlatformPreflight>());
        services.AddSingleton<WindowsInstaller>();
        services.AddSingleton<IPlatformInstaller>(provider => provider.GetRequiredService<WindowsInstaller>());
        return services;
    }
}

public sealed class WindowsInstaller(
    IUpdateTargetDetector targetDetector,
    IUpdateLock updateLock,
    IForgeReleaseClient releaseClient,
    IReleaseVerifier releaseVerifier,
    WindowsUpdateStrategy strategy) : IPlatformInstaller
{
    private readonly IUpdateTargetDetector targetDetector = targetDetector ?? throw new ArgumentNullException(nameof(targetDetector));
    private readonly IUpdateLock updateLock = updateLock ?? throw new ArgumentNullException(nameof(updateLock));
    private readonly IForgeReleaseClient releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
    private readonly IReleaseVerifier releaseVerifier = releaseVerifier ?? throw new ArgumentNullException(nameof(releaseVerifier));
    private readonly WindowsUpdateStrategy strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));

    async ValueTask<InstallationResult> IPlatformInstaller.InstallLatestAsync(CancellationToken cancellationToken)
    {
        WindowsInstallationResult result = await InstallLatestAsync(cancellationToken).ConfigureAwait(false);
        return new(result.Succeeded, result.VersionDirectory, result.Diagnostic);
    }

    async ValueTask<InstallationResult> IPlatformInstaller.InstallLatestAsync(
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        WindowsInstallationResult result = await InstallLatestAsync(progress, cancellationToken).ConfigureAwait(false);
        return new(result.Succeeded, result.VersionDirectory, result.Diagnostic);
    }

    public ValueTask<WindowsInstallationResult> InstallLatestAsync(CancellationToken cancellationToken) =>
        InstallLatestAsync(null, cancellationToken);

    public async ValueTask<WindowsInstallationResult> InstallLatestAsync(
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(1, 6, "Detecting the installation target."));
        UpdateTarget target = targetDetector.Detect();
        if (!strategy.Supports(target))
        {
            return WindowsInstallationResult.Failure(new(
                UpdateDiagnosticCode.PlatformNotSupported,
                "The release target is not supported by the Windows installer."));
        }

        progress?.Report(new(2, 6, "Waiting for the installation lock."));
        UpdateLockResult lockResult = await updateLock.AcquireAsync(target, cancellationToken).ConfigureAwait(false);
        if (!lockResult.IsAcquired)
        {
            return WindowsInstallationResult.Failure(lockResult.Diagnostic);
        }

        await using IAsyncDisposable updateLease = lockResult.Lease!;

        progress?.Report(new(3, 6, "Resolving the latest stable release."));
        ReleaseLookupResult lookup = await releaseClient.GetLatestStableAsync(
            SemanticVersion.Parse("0.0.0"),
            cancellationToken).ConfigureAwait(false);
        if (!lookup.IsUpdateAvailable)
        {
            return WindowsInstallationResult.Failure(lookup.Diagnostic);
        }

        progress?.Report(new(4, 6, $"Downloading and verifying Forge {lookup.Release!.Version}."));
        VerificationResult verification = await releaseVerifier.VerifyAsync(
            lookup.Release!,
            target,
            cancellationToken).ConfigureAwait(false);
        if (!verification.Succeeded)
        {
            return WindowsInstallationResult.Failure(verification.Diagnostic);
        }

        progress?.Report(new(5, 6, "Downloading, extracting, and self-testing the release."));
        WindowsInstallationResult result = await strategy.InstallAsync(
            verification.Release!, target, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            progress?.Report(new(6, 6, "Installation activated; command, PATH, and desktop shortcut are registered."));
        }

        return result;
    }
}

public sealed class HttpReleaseAssetDownloader(HttpClient client) : IReleaseAssetDownloader
{
    private readonly HttpClient client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<Stream> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return await client.GetStreamAsync(asset.DownloadUri, cancellationToken).ConfigureAwait(false);
    }
}
