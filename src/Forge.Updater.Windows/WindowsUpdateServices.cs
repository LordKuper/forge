using Microsoft.Extensions.DependencyInjection;

namespace Forge.Updater.Windows;

public static class WindowsUpdateServices
{
    public static IServiceCollection AddForgeWindowsUpdater(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IReleaseAssetDownloader, HttpReleaseAssetDownloader>();
        services.AddSingleton<IUpdateLock, WindowsUpdateLock>();
        services.AddSingleton<IPlatformUpdateStrategy>(provider => new WindowsUpdateStrategy(
            provider.GetRequiredService<IReleaseAssetDownloader>(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge")));
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
