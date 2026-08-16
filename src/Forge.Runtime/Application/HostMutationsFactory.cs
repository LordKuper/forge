using Forge.Configuration;
using Forge.Host.Client;

namespace Forge.Application;

/// <summary>
/// ADR 0005: every `.forge/` mutation routes through the project's Host once one can exist for a
/// given project root — which requires a project id, so an uninitialized project (no manifest yet)
/// falls back to the local <see cref="ForgeApplication"/>, matching the one bootstrap mutation that
/// can precede a Host (`init`). The Host itself is started lazily, on the first actual mutation.
/// Shared by every composition root (CLI, Desktop) so the discovery/launch wiring exists exactly
/// once instead of being re-derived per adapter.
/// </summary>
public static class HostMutationsFactory
{
    /// <summary>
    /// Binds every dependency <see cref="CreateAsync"/> needs except the per-call project root, so a
    /// composition root threads one delegate through its command layer (matching
    /// <c>CliApplication.CreateRootCommand</c>'s <c>resolveMutations</c> parameter) instead of the 3
    /// raw services plus <paramref name="application"/> and <paramref name="clientVersion"/> — and
    /// so the two same-typed <c>string</c> parameters on <see cref="CreateAsync"/> are never both
    /// live at a call site captured here.
    /// </summary>
    public static Func<string?, CancellationToken, Task<IForgeMutations>> CreateResolver(
        ProjectRootResolver rootResolver,
        IConfigurationRegistry registry,
        IEnvironmentPaths paths,
        ForgeApplication application,
        string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrEmpty(clientVersion);
        return (projectRoot, cancellationToken) =>
            CreateAsync(rootResolver, registry, paths, application, clientVersion, projectRoot, cancellationToken);
    }

    public static async Task<IForgeMutations> CreateAsync(
        ProjectRootResolver rootResolver,
        IConfigurationRegistry registry,
        IEnvironmentPaths paths,
        ForgeApplication application,
        string clientVersion,
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrEmpty(clientVersion);

        ProjectRootStatus status = await rootResolver.ResolveAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!status.Initialized)
        {
            return application;
        }

        Guid projectId;
        try
        {
            projectId = await ProjectIdentity
                .ReadProjectIdAsync(status.Root, registry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException or InvalidDataException
            or FormatException or ConfigurationScopeException or IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // The same unreadable-manifest condition ControlPlaneHostedService itself treats as
            // "this project cannot be served" — with no project id to key a Host connection on,
            // the caller falls back to the local ForgeApplication, which will independently hit
            // (and correctly diagnose) this same failure when it actually runs the command.
            return application;
        }

        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, paths.InstanceId, clientVersion));
        string hostExecutablePath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ??
                throw new InvalidOperationException("The Forge executable path is unavailable."),
            "Forge.Host" + Path.GetExtension(Environment.ProcessPath));
        return new RemoteForgeMutations(
            client,
            async ct => await ForgeHostLauncher
                .StartAsync(hostExecutablePath, status.Root, paths.InstanceId, ct)
                .ConfigureAwait(false));
    }
}
