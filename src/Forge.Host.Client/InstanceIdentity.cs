using System.Security.Cryptography;
using System.Text;

namespace Forge.Host.Client;

/// <summary>
/// Namespaces IPC endpoints and the project lease by instance so release, Debug, and test processes never collide.
/// Release uses <see cref="Release"/>, Debug defaults to <see cref="Debug"/>, and automated tests use
/// <see cref="CreateEphemeral"/>.
/// </summary>
public static class InstanceIdentity
{
    public const string Release = "forge";
    public const string Debug = "forge-dev";

    public static string Default =>
#if DEBUG
        Debug;
#else
        Release;
#endif

    public static string CreateEphemeral() => $"forge-test-{Guid.NewGuid():N}";

    /// <summary>The short, hashed pipe name for a project under this instance; see ADR 0005.</summary>
    public static string ComputePipeName(string instanceId, Guid projectId) =>
        $"{ComputeHash(instanceId, projectId)}-pipe";

    /// <summary>The short, hashed project-lease name for a project under this instance; see ADR 0005.</summary>
    public static string ComputeLeaseName(string instanceId, Guid projectId) =>
        $"{ComputeHash(instanceId, projectId)}-lease";

    private static string ComputeHash(string instanceId, Guid projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{instanceId}|{projectId:D}"));
        return $"forge-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}
