using Forge.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Runtime.Windows;

public static class WindowsProcessContainmentServices
{
    /// <summary>Plan section 12.4: overrides the cross-platform <c>NullProcessContainment</c>
    /// default <c>AddForgeInfrastructure</c> registers, matching how
    /// <see cref="WindowsNotificationServices.AddForgeRuntimeWindowsNotifications"/> overrides
    /// <c>INotificationService</c>'s own default.</summary>
    public static IServiceCollection AddForgeRuntimeWindowsProcessContainment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IProcessContainment, WindowsJobObjectProcessContainment>());
        return services;
    }
}
