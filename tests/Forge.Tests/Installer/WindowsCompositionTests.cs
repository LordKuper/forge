using Forge.Application;
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

    [Fact]
    [Trait("Category", "Installer")]
    public void WindowsProductCompositionReplacesThePlatformPreflightWithTheWindowsOne()
    {
        // Regression coverage: ForgeHost.AddForgeCore only registers a TryAdd fallback
        // (UnsupportedPlatformPreflight); without AddForgeWindowsUpdater's services.Replace(...),
        // every Windows composition root (CLI, Desktop, and the Host process) would silently fall
        // back to it, failing StartupPipeline's platform check on a healthy machine.
        using IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services => services.AddForgeWindowsUpdater())
            .Build();

        IPlatformPreflight preflight = host.Services.GetRequiredService<IPlatformPreflight>();

        Assert.IsType<WindowsPlatformPreflight>(preflight);
    }
}
