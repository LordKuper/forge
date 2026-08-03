using System.ComponentModel;
using System.Diagnostics;

namespace Forge.Updater.Windows;

public sealed class WindowsRestartCoordinator(
    IRestartTokenStore tokenStore,
    TimeSpan? handshakeTimeout = null) : IRestartCoordinator
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);
    private readonly IRestartTokenStore tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    private readonly TimeSpan handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;

    public async ValueTask<UpdateDiagnostic> RestartAsync(
        RestartContext restart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restart);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(restart.ExecutablePath) || !Directory.Exists(restart.WorkingDirectory))
        {
            return new(UpdateDiagnosticCode.RestartFailed, "The updated Forge host or working directory is unavailable.");
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = restart.ExecutablePath,
                WorkingDirectory = restart.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--restart-token");
            startInfo.ArgumentList.Add(restart.Token);
            foreach (string argument in restart.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo) ?? throw new Win32Exception("The updated Forge host could not be started.");
            DateTimeOffset deadline = DateTimeOffset.UtcNow + handshakeTimeout;
            while (tokenStore.Exists(restart.Token))
            {
                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return new(UpdateDiagnosticCode.HandshakeFailed, "The updated Forge host did not confirm startup in time.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
            }

            return UpdateDiagnostic.None;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return new(UpdateDiagnosticCode.RestartFailed, "The updated Forge host could not be restarted.");
        }
    }
}
