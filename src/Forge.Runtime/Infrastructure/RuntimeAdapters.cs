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

public sealed class NetworkClient(HttpClient client) : INetworkClient
{
    public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
        client.GetStreamAsync(uri, cancellationToken);
}

public static class InfrastructureServices
{
    public static IServiceCollection AddForgeInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<INetworkClient, NetworkClient>();
        services.AddSingleton<ISafeLogger, SafeLogger>();
        return services;
    }
}
