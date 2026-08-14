using Microsoft.Extensions.DependencyInjection;

namespace Forge.Providers.Claude;

public static class ClaudeProviderServices
{
    public static IServiceCollection AddClaudeProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILlmProvider, ClaudeLlmProvider>();
        return services;
    }
}
