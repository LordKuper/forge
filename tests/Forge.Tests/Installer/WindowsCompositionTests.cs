using Forge.Bootstrap;
using Forge.Updater;
using Forge.Updater.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.InstallerTests;

public sealed class WindowsCompositionTests
{
    [Fact]
    [Trait("Category", "Installer")]
    public void WindowsProductCompositionResolvesTheWindowsUpdateStrategy()
    {
        using IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services => services.AddForgeWindowsUpdater())
            .Build();

        PlatformUpdateStrategyResolver resolver = host.Services.GetRequiredService<PlatformUpdateStrategyResolver>();
        StrategyResolution result = resolver.Resolve(new UpdateTarget("windows", "x64", "portable_bundle"));

        Assert.IsType<WindowsUpdateStrategy>(result.Strategy);
        Assert.NotNull(host.Services.GetRequiredService<WindowsInstaller>());
        Assert.NotNull(host.Services.GetRequiredService<IPlatformInstaller>());
        Assert.NotNull(host.Services.GetRequiredService<IForgeSelfUpdater>());
    }
}
