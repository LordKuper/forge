using Forge.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Runtime.Posix;

public static class PosixProcessContainmentServices
{
    /// <summary>Plan section 12.4: overrides the cross-platform <c>NullProcessContainment</c>
    /// default <c>AddForgeInfrastructure</c> registers with the best-effort Linux/macOS adapter --
    /// see <see cref="PosixProcessGroupContainment"/>'s own doc comment for exactly what guarantee
    /// that is (and is not). Mirrors
    /// <c>Forge.Runtime.Windows.WindowsProcessContainmentServices.AddForgeRuntimeWindowsProcessContainment</c>'s
    /// shape; a caller decides when to call this (see <c>Forge.Host.TestHost/Program.cs</c>) rather
    /// than this method branching on the current OS itself, since this whole assembly targets a
    /// single, non-OS-specific TFM and its callers already know which OS they are running on.</summary>
    public static IServiceCollection AddForgeRuntimePosixProcessContainment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IProcessContainment, PosixProcessGroupContainment>());
        return services;
    }
}
