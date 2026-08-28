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
        // Fully qualified: unqualified `Host` now resolves to the `Forge.Host` namespace (reachable
        // from here via Forge.Host.Client, which RemoteForgeMutations added as a dependency) rather
        // than Microsoft.Extensions.Hosting.Host, since namespace lookup wins over a `using`-imported
        // type when both share an enclosing namespace segment.
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
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
        // No ILlmProvider is registered here (ADR 0008: the core owns no concrete provider
        // registration) — a Windows composition root adds them via AddCodexProvider()/
        // AddClaudeProvider(). IEnumerable<ILlmProvider> resolves empty until then, and
        // ProviderCatalog's uniqueness check simply has nothing to reject.
        services.TryAddSingleton<ProviderCatalog>();
        services.TryAddSingleton<IProviderEnablementSource, ScopedConfigurationProviderEnablementSource>();
        services.TryAddSingleton<IProviderReleaseCache, FileProviderReleaseCache>();
        services.TryAddSingleton<IProviderDefaultModelCache, FileProviderDefaultModelCache>();
        services.TryAddSingleton<IProviderModelCatalogCache, FileProviderModelCatalogCache>();
        services.TryAddSingleton<IProviderInstallLock, ProviderInstallLock>();
        services.TryAddSingleton<IProviderToolchainManager, ProviderToolchainManager>();
        services.AddSingleton<ProjectRootResolver>();
        services.AddSingleton<ProjectInitializer>();
        services.AddSingleton<ISprintStore, FileSprintEventLog>();
        services.AddSingleton<SprintScheduler>();
        services.AddSingleton<SprintOrchestrator>();
        services.AddSingleton<SprintGitIsolation>();
        services.AddSingleton<ActiveOperationRegistry>();
        services.AddSingleton<StopOperationCoordinator>();
        services.AddSingleton<StageTransitionAssessor>();
        services.AddSingleton<StageTransitionCoordinator>();
        services.AddSingleton<RoutingLedger>();
        services.AddSingleton<ControlEventsReader>();
        services.AddSingleton<ProjectCatalogStore>();
        services.AddSingleton<WorkspaceSummaryProjector>();
        services.AddSingleton<SprintTimelineProjector>();
        services.AddSingleton<AvailableActionProjector>();
        // ADR 0024: overridden by a platform composition root the same way IPlatformPreflight is —
        // no OS adapter means notifications are silently discarded, never an error.
        services.TryAddSingleton<INotificationService, NullNotificationService>();
        services.AddSingleton<StartupRecovery>();
        services.AddSingleton<StartupPipeline>();
        services.AddSingleton<StatusAdvisor>();
        services.AddSingleton<ForgeApplication>();
        services.AddSingleton<IntegrationGenerationService>();
        services.AddSingleton<IntegrationInstallationService>();
        return services;
    }
}
