using Forge.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Runtime.Windows;

public static class WindowsNotificationServices
{
    /// <summary>ADR 0024: overrides the cross-platform <c>NullNotificationService</c> default
    /// <c>AddForgeCore</c> registers, matching how <c>AddForgeWindowsUpdater</c> overrides
    /// <c>IPlatformPreflight</c>'s own default.</summary>
    public static IServiceCollection AddForgeRuntimeWindowsNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<INotificationService, WindowsNotificationService>());
        return services;
    }
}
