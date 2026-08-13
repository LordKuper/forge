using System.Diagnostics;
using Forge.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Infrastructure;

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (request.EnvironmentVariables is not null)
        {
            foreach ((string key, string value) in request.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and termination.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }
}

/// <summary>Resolves the current commit through `git rev-parse HEAD`, never a shell string.</summary>
public sealed class GitRepository(IProcessRunner processRunner) : IRepository
{
    public async Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner
            .RunAsync(new("git", ["rev-parse", "HEAD"], projectRoot), cancellationToken)
            .ConfigureAwait(false);
        string head = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || head.Length == 0)
        {
            throw new InvalidOperationException("'git rev-parse HEAD' did not resolve a commit.");
        }

        return head;
    }
}

public sealed class NetworkClient(HttpClient client) : INetworkClient
{
    public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
        client.GetStreamAsync(uri, cancellationToken);
}

/// <summary>
/// The real, OS-backed <see cref="IEnvironmentPaths"/>. <see cref="InstanceId"/> defaults to the
/// build configuration's release/Debug split — matching <c>Forge.Host.Client.InstanceIdentity</c>'s
/// <c>Release</c>/<c>Debug</c> constants, duplicated rather than referenced because
/// <c>Forge.Host.Client</c> is a leaf transport/protocol project and this neutral engine project
/// takes no dependency on it (see <c>ControlProtocol.JsonOptions</c>'s doc comment for the same
/// pattern). A composition root that already knows a more specific instance id (e.g. Forge.Host's
/// own <c>--instance-id</c>, including the unique ephemeral id automated tests spawn a real Host
/// process with) supplies it explicitly instead.
/// </summary>
public sealed class SystemEnvironmentPaths(string? instanceId = null) : IEnvironmentPaths
{
    private const string ReleaseInstanceId = "forge";
    private const string DebugInstanceId = "forge-dev";

    private static readonly string DefaultInstanceId =
#if DEBUG
        DebugInstanceId;
#else
        ReleaseInstanceId;
#endif

    public string LocalApplicationData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string CurrentDirectory => Environment.CurrentDirectory;

    // Whitespace-only is treated the same as omitted: a blank --instance-id must never silently
    // collapse Path.Combine's instance segment back to the unscoped path this type exists to avoid.
    public string InstanceId { get; } = string.IsNullOrWhiteSpace(instanceId) ? DefaultInstanceId : instanceId;
}

public static class InfrastructureServices
{
    public static IServiceCollection AddForgeInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IRepository, GitRepository>();
        services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
        // Forge's own release bundle download can run into the hundreds of megabytes; the
        // default 100-second HttpClient.Timeout covers the whole request, including the body,
        // and would abort a slow-connection download mid-stream.
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        services.AddSingleton<INetworkClient, NetworkClient>();
        // TryAdd, matching IPlatformPreflight's convention (ForgeHost.cs): signals this is an
        // intended override point. Forge.Host's composition root always overrides this with an
        // instance-scoped SystemEnvironmentPaths afterward via a plain AddSingleton, which still
        // wins for singular resolution regardless of Try here — this only makes that override
        // relationship explicit instead of an accident of registration order.
        services.TryAddSingleton<IEnvironmentPaths, SystemEnvironmentPaths>();
        services.AddSingleton<ISafeLogger, SafeLogger>();
        return services;
    }
}
