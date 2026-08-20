using Forge.Application;
using Forge.Bootstrap;
using Forge.Host.Client;
using Forge.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Host;

/// <summary>
/// The Host runtime's entry point (ADR 0005): platform-neutral protocol, lifecycle, and control
/// plane. A composition root (a Windows executable, or <c>Forge.Host.TestHost</c> for
/// cross-platform process tests) supplies the platform adapter installation and provider
/// registrations before calling <see cref="RunAsync"/>.
/// </summary>
public static class ForgeHostApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        Action<IServiceCollection> configureProviders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configureProviders);

        string? projectRoot = null;
        string? instanceId = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--project-root", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                projectRoot = args[++index];
            }
            else if (string.Equals(args[index], "--instance-id", StringComparison.Ordinal) &&
                index + 1 < args.Length)
            {
                instanceId = args[++index];
            }
        }

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            await Console.Error.WriteLineAsync("--project-root is required.").ConfigureAwait(false);
            return 1;
        }

        ControlPlaneOptions options = new(projectRoot, instanceId ?? InstanceIdentity.Default);
        using IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(options);
                // Overrides AddForgeCore's default (compiled-in release/Debug) IEnvironmentPaths with this
                // Host's actual resolved instance id, which for a test-spawned Host is a unique ephemeral id
                // — without this, every ephemeral test instance would collide on the same Debug-build user
                // configuration and worktree paths under the real developer's %LOCALAPPDATA%.
                services.AddSingleton<IEnvironmentPaths>(new SystemEnvironmentPaths(options.InstanceId));
                configureProviders(services);
                // Not registered as its own IHostedService: ControlPlaneHostedService owns its
                // lifetime directly, starting it only after winning the project lease so a Host
                // that loses the lease race never runs a tick against durable state it doesn't own.
                services.AddSingleton(new ResumeSchedulerOptions(projectRoot));
                services.AddSingleton<ResumeSchedulerHostedService>();
                // Same reasoning as ResumeSchedulerHostedService above: ControlPlaneHostedService
                // owns its lifetime directly, starting it only after winning the project lease.
                services.AddSingleton(new NotificationDeliveryOptions(projectRoot));
                services.AddSingleton<NotificationDeliveryHostedService>();
                // Same reasoning again, and it matters most here: this is the one service that
                // executes a node — a Host that lost the lease race must never drive an attempt
                // against durable state another Host owns.
                services.AddSingleton(new IntakeExecutionOptions(projectRoot));
                services.AddSingleton<IntakeExecutionHostedService>();
                // Same reasoning again: this is the second service that executes a node (Stage 11's
                // planning executor, the first to invoke a real ILlmProvider) — a Host that lost the
                // lease race must never drive it either.
                services.AddSingleton(new PlanningExecutionOptions(projectRoot));
                services.AddSingleton<PlanningExecutionHostedService>();
                services.AddHostedService<ControlPlaneHostedService>();
            })
            .Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
