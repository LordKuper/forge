using System.CommandLine;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
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
        IAsyncDisposable? created = null;
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations = HostMutationsFactory.CreateResolver(
            host.Services.GetRequiredService<ProjectRootResolver>(),
            host.Services.GetRequiredService<IConfigurationRegistry>(),
            host.Services.GetRequiredService<IEnvironmentPaths>(),
            application,
            typeof(CliApplication).Assembly.GetName().Version!.ToString(3));
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
                IForgeMutations mutations = await resolveMutations(mutationRoot, ct).ConfigureAwait(false);
                created = mutations as IAsyncDisposable;
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
}
