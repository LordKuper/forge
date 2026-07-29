using Forge.Configuration;
using Forge.Infrastructure;
using Forge.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Bootstrap;

public static class ForgeHost
{
    public static IHostBuilder CreateBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddJsonConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.UseUtcTimestamp = true;
                });
            })
            .ConfigureServices(services => services.AddForgeCore());

    public static IServiceCollection AddForgeCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddForgeInfrastructure();
        services.AddSingleton<ILocalizationCatalog, ResourceLocalizationCatalog>();
        services.AddSingleton<IConfigurationRegistry, ConfigurationRegistry>();
        services.AddSingleton<ConfigurationResolver>();
        services.AddSingleton<ConfigurationStoreFactory>();
        return services;
    }
}
