using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Localization;
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
}
