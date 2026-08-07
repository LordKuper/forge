using Forge.Application;
using Forge.Configuration;
using Forge.Infrastructure;
using Forge.Localization;
using Forge.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // The registry owns the built-in key set; dependency injection must not inject an empty one.
        services.AddSingleton<IConfigurationRegistry>(_ => new ConfigurationRegistry());
        services.AddSingleton<ConfigurationResolver>();
        services.AddSingleton<ConfigurationStoreFactory>();
        services.AddSingleton<ConfigurationMigrator>();
        services.AddSingleton<ScopedConfigurationStores>();
        services.AddSingleton<ScopedConfigurationService>();
        // A platform composition may register before or after the core defaults.
        services.TryAddSingleton<IPlatformPreflight, UnsupportedPlatformPreflight>();
        // Adapters need their own concrete strategy, not "any" IProviderStrategy, so each
        // strategy is resolvable both by its concrete type and through the shared collection.
        services.TryAddSingleton<CodexProviderStrategy>();
        services.TryAddSingleton<ClaudeCodeProviderStrategy>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderStrategy, CodexProviderStrategy>(
            sp => sp.GetRequiredService<CodexProviderStrategy>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderStrategy, ClaudeCodeProviderStrategy>(
            sp => sp.GetRequiredService<ClaudeCodeProviderStrategy>()));
        services.TryAddSingleton<IProviderToolchainManager, ProviderToolchainManager>();
        services.TryAddSingleton<CodexProviderAdapter>();
        services.TryAddSingleton<ClaudeCodeProviderAdapter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderAdapter, CodexProviderAdapter>(
            sp => sp.GetRequiredService<CodexProviderAdapter>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderAdapter, ClaudeCodeProviderAdapter>(
            sp => sp.GetRequiredService<ClaudeCodeProviderAdapter>()));
        services.AddSingleton<ProjectRootResolver>();
        services.AddSingleton<ProjectInitializer>();
        services.AddSingleton<ISprintStore, FileSprintEventLog>();
        services.AddSingleton<SprintScheduler>();
        services.AddSingleton<SprintOrchestrator>();
        services.AddSingleton<StartupRecovery>();
        services.AddSingleton<StartupPipeline>();
        services.AddSingleton<StatusAdvisor>();
        services.AddSingleton<ForgeApplication>();
        return services;
    }
}
