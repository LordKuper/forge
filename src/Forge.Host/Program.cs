using Forge.Application;
using Forge.Bootstrap;
using Forge.Host;
using Forge.Host.Client;
using Forge.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

string? projectRoot = null;
string? instanceId = null;
for (int index = 0; index < args.Length; index++)
{
    if (string.Equals(args[index], "--project-root", StringComparison.Ordinal) && index + 1 < args.Length)
    {
        projectRoot = args[++index];
    }
    else if (string.Equals(args[index], "--instance-id", StringComparison.Ordinal) && index + 1 < args.Length)
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
        services.AddHostedService<ControlPlaneHostedService>();
    })
    .Build();
await host.RunAsync().ConfigureAwait(false);
return 0;
