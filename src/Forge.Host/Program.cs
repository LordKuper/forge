using Forge.Bootstrap;
using Forge.Host;
using Forge.Host.Client;
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
        services.AddHostedService<ControlPlaneHostedService>();
    })
    .Build();
await host.RunAsync().ConfigureAwait(false);
return 0;
