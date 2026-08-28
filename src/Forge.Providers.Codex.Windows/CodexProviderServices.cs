using Forge.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Providers.Codex;

public static class CodexProviderServices
{
    public static IServiceCollection AddCodexProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILlmProvider>(provider => new CodexLlmProvider(
            provider.GetRequiredService<IEnvironmentPaths>(),
            provider.GetRequiredService<IProcessRunner>(),
            new CodexReleaseSource(provider.GetRequiredService<INetworkClient>()),
            provider.GetRequiredService<IProviderReleaseCache>(),
            provider.GetRequiredService<IProviderDefaultModelCache>(),
            provider.GetRequiredService<IProviderModelCatalogCache>(),
            provider.GetRequiredService<IProviderInstallLock>(),
            provider.GetRequiredService<IClock>()));
        services.AddSingleton<IProviderIntegrationGenerator, CodexIntegrationGenerator>();
        return services;
    }
}
