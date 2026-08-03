using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Localization;
using Forge.Updater;
using Forge.Updater.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.IntegrationTests;

public sealed class BootstrapTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHostRegistersCoreAbstractions()
    {
        using IHost host = ForgeHost.CreateBuilder().Build();

        Assert.NotNull(host.Services.GetRequiredService<IClock>());
        Assert.NotNull(host.Services.GetRequiredService<ILocalizationCatalog>());
        Assert.NotNull(host.Services.GetRequiredService<IConfigurationRegistry>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void WindowsProductCompositionResolvesTheWindowsUpdateStrategy()
    {
        using IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services => services.AddForgeWindowsUpdater())
            .Build();

        PlatformUpdateStrategyResolver resolver = host.Services.GetRequiredService<PlatformUpdateStrategyResolver>();
        StrategyResolution result = resolver.Resolve(new UpdateTarget("windows", "x64", "portable_bundle"));

        Assert.IsType<WindowsUpdateStrategy>(result.Strategy);
        Assert.NotNull(host.Services.GetRequiredService<WindowsInstaller>());
    }
}
