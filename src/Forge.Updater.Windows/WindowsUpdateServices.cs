using Microsoft.Extensions.DependencyInjection;

namespace Forge.Updater.Windows;

public static class WindowsUpdateServices
{
    public static IServiceCollection AddForgeWindowsUpdater(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge");
        services.AddSingleton<IReleaseAssetDownloader, HttpReleaseAssetDownloader>();
        services.AddSingleton<IUpdateTargetDetector, RuntimeUpdateTargetDetector>();
        services.AddSingleton<IUpdateLock, WindowsUpdateLock>();
        services.AddSingleton<IWindowsUserPathRegistrar, WindowsUserPathRegistrar>();
        services.AddSingleton<IWindowsDesktopShortcut, WindowsDesktopShortcut>();
        services.AddSingleton<IRestartTokenStore>(_ => new FileRestartTokenStore(Path.Combine(root, "restart")));
        services.AddSingleton<IRestartTokenService, RestartTokenService>();
        services.AddSingleton<IRestartCoordinator, WindowsRestartCoordinator>();
        services.AddSingleton(provider => new WindowsUpdateStrategy(
            provider.GetRequiredService<IReleaseAssetDownloader>(),
            root,
            pathRegistrar: provider.GetRequiredService<IWindowsUserPathRegistrar>(),
            desktopShortcut: provider.GetRequiredService<IWindowsDesktopShortcut>()));
        services.AddSingleton<IPlatformUpdateStrategy>(provider => provider.GetRequiredService<WindowsUpdateStrategy>());
        services.AddSingleton<PlatformUpdateStrategyResolver>();
        return services;
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
