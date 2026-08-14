using Microsoft.Extensions.DependencyInjection;

namespace Forge.Providers.Codex;

public static class CodexProviderServices
{
    public static IServiceCollection AddCodexProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILlmProvider, CodexLlmProvider>();
        return services;
    }
}
