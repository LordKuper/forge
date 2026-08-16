using System.CommandLine;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Host.Client;
using Forge.Localization;
using Forge.Updater;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Cli;

/// <summary>
/// Portable composition root the platform-specific executables call into. Its <c>configurePlatform</c> callback
/// registers the adapter services (installer, self-updater) a composition root composes; without one the CLI still
/// runs, only omitting the install/update commands.
/// </summary>
public static class CliHost
{
    public static async Task<int> RunAsync(
        string[] args,
        Action<IServiceCollection>? configurePlatform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        using IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services => configurePlatform?.Invoke(services))
            .Build();
        ILocalizationCatalog catalog = host.Services.GetRequiredService<ILocalizationCatalog>();
        StartupArguments startupArguments = StartupArguments.Parse(args);
        if (startupArguments.RestartToken is not null)
        {
            IRestartTokenService? restartTokens = host.Services.GetService<IRestartTokenService>();
            IUpdateTargetDetector? targetDetector = host.Services.GetService<IUpdateTargetDetector>();
            if (restartTokens is null || targetDetector is null)
            {
                return 1;
            }

            UpdateDiagnostic handshake = new StartupHandshake(restartTokens).Confirm(
                startupArguments.RestartToken,
                new(
                    SemanticVersion.Parse(typeof(CliApplication).Assembly.GetName().Version!.ToString(3)),
                    targetDetector.Detect(),
                    UpdateSurface.Cli));
            if (handshake.Code != UpdateDiagnosticCode.None)
            {
                return 1;
            }
        }

        args = [.. startupArguments.Remaining];
        if (startupArguments.IsSelfTest)
        {
            return 0;
        }

        IPlatformInstaller? installer = host.Services.GetService<IPlatformInstaller>();
        IForgeSelfUpdater? updater = host.Services.GetService<IForgeSelfUpdater>();
        ForgeApplication application = host.Services.GetRequiredService<ForgeApplication>();
        // The startup sequence resolves the UI language before any text is rendered.
        StartupStatus startup = await application.GetStartupStatusAsync(null, cancellationToken)
            .ConfigureAwait(false);
        // Created lazily, at most once, only by a command that actually mutates — never for a
        // read-only command (`status`, `next`, ...), and always scoped to THAT command's own
        // resolved `--project-root`, never the CWD-resolved root above (which is only for language
        // selection). Tracked here so it can be disposed once the command finishes, however it
        // resolved (falling back to the local `application` never sets this).
        RemoteForgeMutations? created = null;
        RootCommand root = CliApplication.CreateRootCommand(
            SurfaceText.For(catalog, startup.Language.Ui),
            Console.Out,
            application,
            Console.Error,
            installer is null ? null : installer.InstallLatestAsync,
            updater is null
                ? null
                : ct => updater.UpdateAsync(
                    new(
                        SemanticVersion.Parse(typeof(CliApplication).Assembly.GetName().Version!.ToString(3)),
                        Environment.ProcessPath ??
                            throw new InvalidOperationException("The Forge executable path is unavailable."),
                        ["status"],
                        Environment.CurrentDirectory,
                        UpdateSurface.Cli),
                    ct),
            async (mutationRoot, ct) =>
            {
                IForgeMutations mutations = await CreateMutationsAsync(host, application, mutationRoot, ct)
                    .ConfigureAwait(false);
                created = mutations as RemoteForgeMutations;
                return mutations;
            });
        try
        {
            return await root.Parse(args).InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (created is not null)
            {
                await created.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// ADR 0005: every `.forge/` mutation routes through the project's Host once one can exist for
    /// <paramref name="projectRoot"/> — which requires a project id, so an uninitialized project (no
    /// manifest yet) falls back to the local <see cref="ForgeApplication"/> here, matching the one
    /// bootstrap mutation that can precede a Host (`init`). The Host itself is started lazily, on
    /// the first actual mutation.
    /// </summary>
    private static async Task<IForgeMutations> CreateMutationsAsync(
        IHost host,
        ForgeApplication application,
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ProjectRootResolver rootResolver = host.Services.GetRequiredService<ProjectRootResolver>();
        ProjectRootStatus status = await rootResolver.ResolveAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!status.Initialized)
        {
            return application;
        }

        IConfigurationRegistry registry = host.Services.GetRequiredService<IConfigurationRegistry>();
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

        IEnvironmentPaths paths = host.Services.GetRequiredService<IEnvironmentPaths>();
        string clientVersion = typeof(CliApplication).Assembly.GetName().Version!.ToString(3);
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
