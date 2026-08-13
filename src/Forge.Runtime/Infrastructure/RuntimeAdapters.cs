using System.Diagnostics;
using Forge.Application;
using Microsoft.Extensions.DependencyInjection;

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

    public string LocalApplicationData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string CurrentDirectory => Environment.CurrentDirectory;

    public string InstanceId { get; } = instanceId ??
#if DEBUG
        DebugInstanceId;
#else
        ReleaseInstanceId;
#endif
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
        services.AddSingleton<IEnvironmentPaths, SystemEnvironmentPaths>();
        services.AddSingleton<ISafeLogger, SafeLogger>();
        return services;
    }
}
