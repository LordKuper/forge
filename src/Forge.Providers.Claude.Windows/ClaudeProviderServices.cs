using Forge.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Providers.Claude;

public static class ClaudeProviderServices
{
    public static IServiceCollection AddClaudeProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILlmProvider>(provider => new ClaudeLlmProvider(
            provider.GetRequiredService<IEnvironmentPaths>(),
            provider.GetRequiredService<IProcessRunner>(),
            new ClaudeReleaseSource(provider.GetRequiredService<INetworkClient>()),
            provider.GetRequiredService<IProviderReleaseCache>(),
            provider.GetRequiredService<IProviderInstallLock>(),
            provider.GetRequiredService<IClock>()));
        return services;
    }
}
