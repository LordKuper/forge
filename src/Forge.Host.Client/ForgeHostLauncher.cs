using System.Diagnostics;

namespace Forge.Host.Client;

/// <summary>Starts a detached Forge Host process for a project. The Host takes a moment to begin listening.</summary>
public static class ForgeHostLauncher
{
    /// <summary>Starts the Host and returns its process id; a detached process, not owned by the caller.</summary>
    public static Task<int> StartAsync(
        string executablePath,
        string projectRoot,
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--project-root");
        startInfo.ArgumentList.Add(projectRoot);
        startInfo.ArgumentList.Add("--instance-id");
        startInfo.ArgumentList.Add(instanceId);
        using Process process = new() { StartInfo = startInfo };
        process.Start();
        return Task.FromResult(process.Id);
    }
}
