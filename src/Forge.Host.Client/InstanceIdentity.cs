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
    public static string ComputePipeName(string instanceId, Guid projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return $"{ComputeHash($"{instanceId}|{projectId:D}")}-pipe";
    }

    /// <summary>
    /// The short, hashed project-lease name for a project — deliberately keyed by
    /// <paramref name="projectId"/> alone, never by instance id: ADR 0005 requires "release,
    /// development, test, CLI, and Desktop instances use the same lease namespace, so distinct
    /// instance data roots cannot become concurrent writers of one <c>.forge/</c> tree." Only the
    /// IPC endpoint (<see cref="ComputePipeName"/>) and each instance's own state root are
    /// instance-scoped; the lease is the one thing every instance of a project must contend for
    /// together.
    /// </summary>
    public static string ComputeLeaseName(Guid projectId) => $"{ComputeHash(projectId.ToString("D"))}-lease";

    private static string ComputeHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"forge-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}
